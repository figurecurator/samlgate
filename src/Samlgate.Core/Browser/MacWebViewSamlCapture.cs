using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Samlgate.Browser;

/// <summary>
/// Runs the AppKit/WKWebView helper. Assertions travel only through a private child-process pipe;
/// native diagnostics are discarded and only fixed, non-sensitive errors reach the CLI.
/// </summary>
public sealed partial class MacWebViewSamlCapture : ISamlCapture
{
    public string ProfileName => "webview";
    public string DisplayName => "macOS WebView";
    internal static string HelperPath => Path.Combine(AppContext.BaseDirectory, "samlgate-webview");

    public async Task<string> CaptureAsync(SamlCaptureRequest request, Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            throw new SamlgateException("The native WebView requires macOS 14 or later.");
        }

        if (!File.Exists(HelperPath))
        {
            throw new SamlgateException("The native WebView helper is missing. Build on macOS with Xcode command-line tools, and keep samlgate-webview beside samlgate.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var settings = new NativeCaptureSettings
        {
            Url = request.IdpUrl.AbsoluteUri,
            ProfileId = GetProfileId(request.ProfileDirectory).ToString(),
            TargetUrl = request.TargetUrl,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(HelperPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        // WebKit's system diagnostics may contain URLs. Never forward them to the terminal.
        var drainErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(settings, NativeCaptureJsonContext.Default.NativeCaptureSettings)
                    .AsMemory(), timeout.Token);
            process.StandardInput.Close();
            onStatus?.Invoke("Complete the sign-in in the browser window.");
            var response = await ReadAssertionAsync(process.StandardOutput, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await drainErrors;

            if (process.ExitCode != 0)
            {
                throw new SamlgateException(process.ExitCode switch
                {
                    2 => "The browser was closed before the sign-in finished.",
                    4 => "The IdP posted to the AWS sign-in endpoint, but the WebView could not read its SAMLResponse.",
                    5 => "The native WebView could not load the sign-in page. Check your network connection and IdP URL.",
                    _ => "The native WebView could not complete the sign-in.",
                });
            }

            if (response.Length == 0)
            {
                throw new SamlgateException("The native WebView returned no SAML response.");
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SamlgateException($"Timed out after {request.Timeout.TotalSeconds:0} seconds waiting for the sign-in to finish.");
        }
        finally
        {
            if (!process.HasExited)
            {
                // Give AppKit a chance to close its windows and persist the WebKit session.
                _ = SendSignal(process.Id, 15);
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }

            await drainErrors;
        }
    }

    internal static Guid GetProfileId(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var path = Path.Combine(directory, "webkit-profile-id");
        if (File.Exists(path))
        {
            if (Guid.TryParse(File.ReadAllText(path).Trim(), out var existing))
            {
                return existing;
            }

            throw new SamlgateException("The native WebView profile identifier is invalid. Restore it or choose a new data directory.");
        }

        var id = Guid.NewGuid();
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var file = new FileStream(path, fileOptions);
        using var writer = new StreamWriter(file);
        writer.Write(id.ToString());
        return id;
    }

    private static async Task<string> ReadAssertionAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (output.Length + count > 16 * 1024 * 1024)
            {
                throw new SamlgateException("The native WebView response exceeded the size limit.");
            }

            output.Append(buffer, 0, count);
        }

        return output.ToString().Trim();
    }

    [LibraryImport("libc", EntryPoint = "kill")]
    private static partial int SendSignal(int processId, int signal);
}

internal sealed record NativeCaptureSettings
{
    public required string Url { get; init; }
    public required string ProfileId { get; init; }
    public string? TargetUrl { get; init; }
}

[JsonSerializable(typeof(NativeCaptureSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class NativeCaptureJsonContext : JsonSerializerContext;
