namespace Samlgate.Config;

/// <summary>
/// One IdP account (a section in ~/.samlgate). Key names match saml2aws so an existing
/// ~/.saml2aws file works unchanged.
/// </summary>
public sealed record AccountConfig
{
    public const string DefaultAccount = "default";
    public const string DefaultProfile = "saml";

    /// <summary>Section name, selected with -a/--idp-account.</summary>
    public required string Name { get; init; }

    /// <summary>IdP-initiated sign-in URL (e.g. the Entra "My Apps" launcher URL of the AWS app). Key: url.</summary>
    public string? Url { get; init; }

    /// <summary>Profile written to the AWS credentials file. Key: aws_profile.</summary>
    public string Profile { get; init; } = DefaultProfile;

    /// <summary>Role to assume without asking. Key: role_arn.</summary>
    public string? RoleArn { get; init; }

    /// <summary>STS region. Defaults to the partition's global region. Key: region.</summary>
    public string? Region { get; init; }

    /// <summary>Requested session length; falls back to the assertion's SessionDuration. Key: aws_session_duration.</summary>
    public int? SessionDurationSeconds { get; init; }

    /// <summary>Credentials file to write. Key: credentials_file.</summary>
    public string? CredentialsFile { get; init; }

    /// <summary>Extra ACS URL to intercept besides the built-in AWS ones. Key: target_url.</summary>
    public string? TargetUrl { get; init; }

    /// <summary>edge, chrome, chromium, brave or webview (macOS 14+). Key: browser_type.</summary>
    public string? BrowserType { get; init; }

    /// <summary>Explicit browser executable. Key: browser_executable_path.</summary>
    public string? BrowserExecutablePath { get; init; }

    /// <summary>File the account was read from, for messages. Null when built from command-line flags only.</summary>
    public string? Source { get; init; }

    public string ResolvedCredentialsFile =>
        string.IsNullOrWhiteSpace(CredentialsFile) ? AppPaths.DefaultCredentialsFile : AppPaths.ExpandHome(CredentialsFile);
}
