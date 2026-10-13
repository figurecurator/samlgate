using System.CommandLine;
using System.CommandLine.Parsing;
using Samlgate.Config;

namespace Samlgate;

/// <summary>The command tree. Parsing, help and usage errors come from System.CommandLine.</summary>
internal static class Cli
{
    public static readonly Option<string> Account = new("--idp-account", "-a")
    {
        Description = "Account section in ~/.samlgate",
        HelpName = "name",
        DefaultValueFactory = _ => AccountConfig.DefaultAccount,
    };

    public static readonly Option<string?> Profile = new("--profile", "-p") { Description = "Credentials profile to write (default: saml)", HelpName = "name" };
    public static readonly Option<string?> Role = new("--role") { Description = "Role to assume without asking", HelpName = "arn" };
    public static readonly Option<string?> Url = new("--url") { Description = "IdP-initiated sign-in URL", HelpName = "url" };
    public static readonly Option<string?> Region = new("--region") { Description = "STS region (default: the partition's global region)", HelpName = "region" };
    public static readonly Option<int?> SessionDuration = Positive(new Option<int?>("--session-duration") { Description = "Requested session length in seconds", HelpName = "seconds" });
    public static readonly Option<string?> Browser = new("--browser") { Description = "edge | chrome | brave | chromium | webview (macOS 14+)", HelpName = "kind" };
    public static readonly Option<string?> BrowserPath = new("--browser-path") { Description = "Browser executable", HelpName = "path" };
    public static readonly Option<string?> TargetUrl = new("--target-url") { Description = "Additional ACS URL to intercept", HelpName = "url" };
    public static readonly Option<string?> CredentialsFile = new("--credentials-file") { Description = "Credentials file (default: ~/.aws/credentials)", HelpName = "path" };

    public static readonly Option<int?> Timeout = Positive(new Option<int?>("--timeout") { Description = "Give up waiting for the sign-in after this many seconds (default: 300)", HelpName = "seconds" });
    public static readonly Option<bool> Force = new("--force", "-f") { Description = "Sign in even if the current session is still valid" };
    public static readonly Option<bool> SamlStdin = new("--saml-stdin") { Description = "Read a base64 SAMLResponse from stdin instead of opening a browser" };

    public static readonly Argument<string[]> ExecArgs = new("command")
    {
        Description = "Command to run, after --",
        Arity = ArgumentArity.OneOrMore,
    };

    public static RootCommand Build()
    {
        Option[] account = [Account, Profile, Role, Url, Region, SessionDuration, Browser, BrowserPath, TargetUrl, CredentialsFile];
        Option[] signIn = [.. account, Timeout, Force, SamlStdin];

        var root = new RootCommand("""
            samlgate — AWS credentials from a browser-based SAML sign-in (Microsoft Entra ID and other IdPs).
            Without ~/.samlgate, the same account section is read from ~/.saml2aws.
            """)
        {
            Command("configure", "Save an account to ~/.samlgate", account,
                (cli, _) => Task.FromResult(Commands.Configure(cli))),
            Command("login", "Sign in and write the credentials profile", signIn, Commands.LoginAsync),
            Command("status", "Show the session (exit 0 if valid)", account,
                (cli, _) => Task.FromResult(Commands.Status(cli))),
            Command("credential-process", "Print credentials as credential_process JSON", signIn,
                Commands.CredentialProcessAsync),
        };

        var exec = Command("exec", "Run a command with the credentials in its environment", signIn, Commands.ExecAsync);
        exec.Arguments.Add(ExecArgs);
        // Without --, the child's own flags (e.g. aws --profile) would be taken as ours
        exec.SetAction((result, cancellationToken) => result.Tokens.Any(t => t.Type == TokenType.DoubleDash)
            ? Commands.ExecAsync(CliOptions.From(result), cancellationToken)
            : throw new UsageException("exec needs a command after --, e.g. samlgate exec -- aws sts get-caller-identity"));
        root.Subcommands.Add(exec);

        var version = root.Options.OfType<VersionOption>().Single();
        root.Options.Remove(version);
        root.Options.Add(new VersionOption("--version", "-v"));

        return root;
    }

    private static Command Command(
        string name, string description, Option[] options, Func<CliOptions, CancellationToken, Task<int>> action)
    {
        var command = new Command(name, description);
        foreach (var option in options)
        {
            command.Options.Add(option);
        }

        command.SetAction((result, cancellationToken) => action(CliOptions.From(result), cancellationToken));
        return command;
    }

    private static Option<int?> Positive(Option<int?> option)
    {
        option.Validators.Add(result =>
        {
            // Non-numbers are already reported by the parser
            if (result.Tokens is [var token] && int.TryParse(token.Value, out var value) && value <= 0)
            {
                result.AddError($"{option.Name} must be a positive number.");
            }
        });
        return option;
    }
}

/// <summary>Parsed options of the invoked command.</summary>
internal sealed record CliOptions
{
    public string Account { get; init; } = AccountConfig.DefaultAccount;
    public string? Profile { get; init; }
    public string? Role { get; init; }
    public string? Url { get; init; }
    public string? Region { get; init; }
    public int? SessionDuration { get; init; }
    public string? Browser { get; init; }
    public string? BrowserPath { get; init; }
    public string? TargetUrl { get; init; }
    public string? CredentialsFile { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public bool Force { get; init; }
    public bool SamlStdin { get; init; }
    public IReadOnlyList<string> ExecArgs { get; init; } = [];

    public static CliOptions From(ParseResult result) => new()
    {
        Account = result.GetValue(Cli.Account) ?? AccountConfig.DefaultAccount,
        Profile = result.GetValue(Cli.Profile),
        Role = result.GetValue(Cli.Role),
        Url = result.GetValue(Cli.Url),
        Region = result.GetValue(Cli.Region),
        SessionDuration = result.GetValue(Cli.SessionDuration),
        Browser = result.GetValue(Cli.Browser),
        BrowserPath = result.GetValue(Cli.BrowserPath),
        TargetUrl = result.GetValue(Cli.TargetUrl),
        CredentialsFile = result.GetValue(Cli.CredentialsFile),
        Timeout = TimeSpan.FromSeconds(result.GetValue(Cli.Timeout) ?? 300),
        Force = result.GetValue(Cli.Force),
        SamlStdin = result.GetValue(Cli.SamlStdin),
        ExecArgs = result.GetValue(Cli.ExecArgs) ?? [],
    };

    /// <summary>Command-line flags win over the config file.</summary>
    public AccountConfig ApplyTo(AccountConfig config) => config with
    {
        Profile = Profile ?? config.Profile,
        RoleArn = Role ?? config.RoleArn,
        Url = Url ?? config.Url,
        Region = Region ?? config.Region,
        SessionDurationSeconds = SessionDuration ?? config.SessionDurationSeconds,
        BrowserType = Browser ?? config.BrowserType,
        BrowserExecutablePath = BrowserPath ?? config.BrowserExecutablePath,
        TargetUrl = TargetUrl ?? config.TargetUrl,
        CredentialsFile = CredentialsFile ?? config.CredentialsFile,
    };
}

public sealed class UsageException(string message) : Exception(message);
