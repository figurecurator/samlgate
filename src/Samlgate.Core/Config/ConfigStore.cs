using System.Globalization;

namespace Samlgate.Config;

/// <summary>
/// Loads accounts from ~/.samlgate, falling back to ~/.saml2aws (read-only) so saml2aws users
/// can switch without touching their config.
/// </summary>
public sealed class ConfigStore(string configFile, string saml2AwsConfigFile)
{
    /// <summary>saml2aws' placeholder URL for its AzureAD provider, which identifies the app by app_id instead.</summary>
    private const string Saml2AwsAzureDefaultUrl = "https://account.activedirectory.windowsazure.com";

    public static ConfigStore Default => new(AppPaths.ConfigFile, AppPaths.Saml2AwsConfigFile);

    public string ConfigFile { get; } = configFile;

    public AccountConfig? Load(string account)
    {
        if (IniFile.ReadSection(ConfigFile, account) is { } own)
        {
            return FromSection(account, own, ConfigFile);
        }

        if (IniFile.ReadSection(saml2AwsConfigFile, account) is { } saml2aws)
        {
            return FromSection(account, saml2aws, saml2AwsConfigFile);
        }

        return null;
    }

    public void Save(AccountConfig config)
    {
        var values = new List<(string, string)> { ("name", config.Name) };
        Add("url", config.Url);
        Add("aws_profile", config.Profile);
        Add("role_arn", config.RoleArn);
        Add("region", config.Region);
        Add("aws_session_duration", config.SessionDurationSeconds?.ToString(CultureInfo.InvariantCulture));
        Add("credentials_file", config.CredentialsFile);
        Add("target_url", config.TargetUrl);
        Add("browser_type", config.BrowserType);
        Add("browser_executable_path", config.BrowserExecutablePath);
        Add("browser_language", config.BrowserLanguage);
        Add("browser_mode", config.BrowserMode);
        Add("silent_timeout", config.SilentTimeoutSeconds?.ToString(CultureInfo.InvariantCulture));
        IniFile.WriteSection(ConfigFile, config.Name, values);

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add((key, value));
            }
        }
    }

    internal static AccountConfig FromSection(string account, IReadOnlyDictionary<string, string> section, string source)
    {
        return new AccountConfig
        {
            Name = account,
            Url = ResolveUrl(section),
            Profile = Value(section, "aws_profile") ?? AccountConfig.DefaultProfile,
            RoleArn = Value(section, "role_arn"),
            Region = Value(section, "region"),
            SessionDurationSeconds = int.TryParse(Value(section, "aws_session_duration"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : null,
            CredentialsFile = Value(section, "credentials_file"),
            TargetUrl = Value(section, "target_url"),
            BrowserType = Value(section, "browser_type"),
            BrowserExecutablePath = Value(section, "browser_executable_path"),
            BrowserLanguage = Value(section, "browser_language"),
            BrowserMode = Value(section, "browser_mode"),
            SilentTimeoutSeconds = PositiveSeconds(section, "silent_timeout"),
            Source = source,
        };
    }

    private static int? PositiveSeconds(IReadOnlyDictionary<string, string> section, string key)
    {
        if (Value(section, key) is not { } raw)
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : throw new SamlgateException($"{key} must be a positive number of seconds.");
    }

    /// <summary>
    /// saml2aws' AzureAD provider stores a placeholder url plus app_id; turn that into the
    /// My Apps launcher URL, which starts the same IdP-initiated flow in a browser.
    /// </summary>
    private static string? ResolveUrl(IReadOnlyDictionary<string, string> section)
    {
        var url = Value(section, "url");
        var appId = Value(section, "app_id");
        var isAzurePlaceholder = url is null ||
            url.TrimEnd('/').Equals(Saml2AwsAzureDefaultUrl, StringComparison.OrdinalIgnoreCase);

        if (isAzurePlaceholder && appId is not null)
        {
            return $"https://launcher.myapps.microsoft.com/api/signin/{Uri.EscapeDataString(appId)}";
        }

        return url;
    }

    private static string? Value(IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
