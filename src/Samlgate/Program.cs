using System.CommandLine;
using System.Text;
using Amazon.Runtime;
using Samlgate;

// Human-readable progress goes to stderr; stdout carries only what a command is asked to print
// (status line, credential_process JSON), so it stays safe to pipe.

// Windows consoles default to the legacy ANSI code page, which mangles non-ASCII text (no BOM)
try
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (IOException)
{
    // No console attached (e.g. credential_process started by a GUI app)
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C: close the browser gracefully. Second: default behaviour (terminate).
    if (!cancellation.IsCancellationRequested)
    {
        e.Cancel = true;
        cancellation.Cancel();
    }
};

try
{
    return await Cli.Build().Parse(args).InvokeAsync(new InvocationConfiguration
    {
        // Our own handlers below keep errors short and Ctrl+C graceful (the browser must close cleanly)
        EnableDefaultExceptionHandler = false,
        ProcessTerminationTimeout = null,
    }, cancellation.Token);
}
catch (UsageException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine("Run 'samlgate --help' for usage.");
    return 2;
}
catch (SamlgateException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}
catch (AmazonServiceException e)
{
    Console.Error.WriteLine($"error: STS {e.ErrorCode}: {e.Message}");
    return 1;
}
catch (HttpRequestException e)
{
    Console.Error.WriteLine($"error: could not reach AWS STS: {e.Message}");
    return 1;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception e)
{
    // Anything else is a bug; keep the output short but reportable
    Console.Error.WriteLine($"unexpected error: {e.GetType().Name}: {e.Message}");
    Console.Error.WriteLine("Please report it at https://github.com/figurecurator/samlgate/issues");
    return 1;
}
