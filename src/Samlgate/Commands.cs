using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Samlgate.Aws;
using Samlgate.Config;
using Samlgate.Browser;

namespace Samlgate;

internal static class Commands
{
    /// <summary>Don't hand out credentials that expire within this window (long-running tools would break mid-way).</summary>
    private static readonly TimeSpan ReuseMargin = TimeSpan.FromMinutes(5);

    public static int Configure(CliOptions cli)
    {
        var store = ConfigStore.Default;
        // Starting from Load() also imports a matching ~/.saml2aws section on first configure
        var existing = store.Load(cli.Account) ?? new AccountConfig { Name = cli.Account };
        var config = cli.ApplyTo(existing);
        _ = BrowserPreferences.ValidateLanguage(config.BrowserLanguage);
        _ = BrowserPreferences.ParseMode(config.BrowserMode);

        if (string.IsNullOrWhiteSpace(config.Url) || !Uri.TryCreate(config.Url, UriKind.Absolute, out _))
        {
            throw new UsageException("configure needs --url <IdP sign-in URL>.");
        }

        store.Save(config);
        Console.Error.WriteLine($"Saved account '{config.Name}' to {store.ConfigFile}.");
        return 0;
    }

    public static int Status(CliOptions cli)
    {
        var account = ResolveAccount(cli);
        var credentials = CredentialsFile.Read(account.ResolvedCredentialsFile, account.Profile);

        if (credentials is null)
        {
            Console.WriteLine($"{account.Profile}: not signed in — run: samlgate login{AccountFlag(account)}");
            return 1;
        }

        if (!credentials.IsValid(TimeSpan.Zero))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{account.Profile}: expired at {credentials.Expiration.ToLocalTime():yyyy-MM-dd HH:mm} — run: samlgate login{AccountFlag(account)}"));
            return 1;
        }

        Console.WriteLine(Describe(account, credentials));
        return 0;
    }

    public static async Task<int> LoginAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        var account = ResolveAccount(cli);

        if (!cli.Force && !cli.SamlStdin &&
            CredentialsFile.Read(account.ResolvedCredentialsFile, account.Profile) is { } current &&
            current.IsValid(ReuseMargin))
        {
            Console.Error.WriteLine("Already signed in (use --force to sign in again).");
            Console.WriteLine(Describe(account, current));
            return 0;
        }

        var credentials = await SignInAsync(cli, account, interactive: true, cancellationToken);
        Console.Error.WriteLine("Signed in.");
        Console.WriteLine(Describe(account, credentials));
        return 0;
    }

    /// <summary>
    /// For ~/.aws/config: credential_process = samlgate credential-process -a default
    /// Use it on a profile other than the one samlgate writes, or the static keys in the credentials file win.
    /// </summary>
    public static async Task<int> CredentialProcessAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        var credentials = await EnsureCredentialsAsync(cli, interactive: false, cancellationToken);

        using var stdout = Console.OpenStandardOutput();
        using var json = new Utf8JsonWriter(stdout);
        json.WriteStartObject();
        json.WriteNumber("Version", 1);
        json.WriteString("AccessKeyId", credentials.AccessKeyId);
        json.WriteString("SecretAccessKey", credentials.SecretAccessKey);
        json.WriteString("SessionToken", credentials.SessionToken);
        json.WriteString("Expiration",
            credentials.Expiration.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        json.WriteEndObject();
        json.Flush();
        return 0;
    }

    public static async Task<int> ExecAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        var credentials = await EnsureCredentialsAsync(cli, interactive: true, cancellationToken);
        var account = ResolveAccount(cli);

        var startInfo = ExecResolver.CreateStartInfo(cli.ExecArgs);
        startInfo.Environment["AWS_ACCESS_KEY_ID"] = credentials.AccessKeyId;
        startInfo.Environment["AWS_SECRET_ACCESS_KEY"] = credentials.SecretAccessKey;
        startInfo.Environment["AWS_SESSION_TOKEN"] = credentials.SessionToken;
        startInfo.Environment["AWS_SECURITY_TOKEN"] = credentials.SessionToken;
        startInfo.Environment["AWS_CREDENTIAL_EXPIRATION"] =
            credentials.Expiration.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        startInfo.Environment["SAMLGATE_PROFILE"] = account.Profile;
        // An inherited profile would make SDKs prefer it over the variables above
        startInfo.Environment.Remove("AWS_PROFILE");
        startInfo.Environment.Remove("AWS_DEFAULT_PROFILE");
        if (!string.IsNullOrWhiteSpace(account.Region))
        {
            startInfo.Environment["AWS_REGION"] = account.Region;
            startInfo.Environment["AWS_DEFAULT_REGION"] = account.Region;
        }

        using var process = Process.Start(startInfo) ?? throw new SamlgateException($"Could not start {cli.ExecArgs[0]}.");
        // Ctrl+C reaches the child directly (same console); just wait for it
        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode;
    }

    private static async Task<AwsSessionCredentials> EnsureCredentialsAsync(
        CliOptions cli, bool interactive, CancellationToken cancellationToken)
    {
        var account = ResolveAccount(cli);
        if (!cli.Force && !cli.SamlStdin &&
            CredentialsFile.Read(account.ResolvedCredentialsFile, account.Profile) is { } current &&
            current.IsValid(ReuseMargin))
        {
            return current;
        }

        return await SignInAsync(cli, account, interactive, cancellationToken);
    }

    private static async Task<AwsSessionCredentials> SignInAsync(
        CliOptions cli, AccountConfig account, bool interactive, CancellationToken cancellationToken)
    {
        using var signInLock = await LoginLock.AcquireAsync(
            Path.Combine(AppPaths.DataDir, "login.lock"), cli.Timeout + TimeSpan.FromMinutes(1),
            message => Console.Error.WriteLine(message), cancellationToken);

        // Another process may have signed in while this one waited for the lock
        if (!cli.Force && !cli.SamlStdin &&
            CredentialsFile.Read(account.ResolvedCredentialsFile, account.Profile) is { } refreshed &&
            refreshed.IsValid(ReuseMargin))
        {
            return refreshed;
        }

        var flow = new LoginFlow(new StsClient(), StateStore.Default);

        return await flow.RunAsync(account, new LoginOptions
        {
            OnStatus = message => Console.Error.WriteLine(message),
            PickRole = interactive && !cli.SamlStdin && !Console.IsInputRedirected ? RolePrompt.Pick : null,
            Timeout = cli.Timeout,
            SamlResponseInput = cli.SamlStdin ? Console.In : null,
        }, cancellationToken);
    }

    private static AccountConfig ResolveAccount(CliOptions cli)
    {
        var stored = ConfigStore.Default.Load(cli.Account);
        if (stored is null && cli.Account != AccountConfig.DefaultAccount && cli.Url is null)
        {
            throw new SamlgateException($"Account '{cli.Account}' is not configured in {AppPaths.ConfigFile}.");
        }

        return cli.ApplyTo(stored ?? new AccountConfig { Name = cli.Account });
    }

    private static string Describe(AccountConfig account, AwsSessionCredentials credentials)
    {
        var remaining = credentials.Expiration - DateTimeOffset.Now;
        var who = credentials.PrincipalArn.Length > 0 ? CredentialsFile.DescribePrincipal(credentials.PrincipalArn) : "?";
        return string.Create(CultureInfo.InvariantCulture,
            $"{account.Profile}: {who} · valid until {credentials.Expiration.ToLocalTime():yyyy-MM-dd HH:mm} ({(int)remaining.TotalHours}h {remaining.Minutes:00}m left)");
    }

    private static string AccountFlag(AccountConfig account) =>
        account.Name == AccountConfig.DefaultAccount ? string.Empty : $" -a {account.Name}";
}
