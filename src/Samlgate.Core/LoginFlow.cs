using Samlgate.Aws;
using Samlgate.Browser;
using Samlgate.Config;

namespace Samlgate;

public sealed record LoginOptions
{
    /// <summary>Role picker for multiple roles. Null when no one can answer (e.g. credential_process).</summary>
    public Func<IReadOnlyList<AwsSamlRole>, AwsSamlRole?, AwsSamlRole>? PickRole { get; init; }

    public Action<string>? OnStatus { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Read the base64 SAMLResponse from this reader instead of opening a browser (fallback / automation).</summary>
    public TextReader? SamlResponseInput { get; init; }
}

/// <summary>Sign in with the IdP → pick a role → STS AssumeRoleWithSAML → write the credentials profile.</summary>
public sealed class LoginFlow(StsClient sts, StateStore state, ISamlCapture? capture)
{
    // Keep the existing public constructor for applications already using Samlgate.Core.
    public LoginFlow(StsClient sts, StateStore state) : this(sts, state, null) { }

    public async Task<AwsSessionCredentials> RunAsync(
        AccountConfig account, LoginOptions options, CancellationToken cancellationToken = default)
    {
        var samlResponse = options.SamlResponseInput is { } input
            ? await input.ReadToEndAsync(cancellationToken)
            : await CaptureInBrowserAsync(account, options, cancellationToken);

        var assertion = SamlAssertion.Parse(samlResponse);
        var role = ChooseRole(account, assertion.Roles, options.PickRole);

        options.OnStatus?.Invoke($"Requesting AWS credentials for {role.DisplayName}...");
        var credentials = await sts.AssumeRoleWithSamlAsync(
            assertion, role, account.Region, account.SessionDurationSeconds, cancellationToken);

        CredentialsFile.Write(account.ResolvedCredentialsFile, account.Profile, credentials);
        if (assertion.Roles.Count > 1)
        {
            state.SetLastRole(account.Name, role.RoleArn);
        }

        return credentials;
    }

    private async Task<string> CaptureInBrowserAsync(
        AccountConfig account, LoginOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.Url) || !Uri.TryCreate(account.Url, UriKind.Absolute, out var idpUrl))
        {
            throw new SamlgateException($"No IdP URL for account '{account.Name}'. Run: samlgate configure --url <IdP sign-in URL>");
        }

        var browser = capture ?? SamlCaptureFactory.Create(account.BrowserType, account.BrowserExecutablePath);
        options.OnStatus?.Invoke($"Opening {browser.DisplayName}...");

        return await browser.CaptureAsync(new SamlCaptureRequest
        {
            IdpUrl = idpUrl,
            ProfileDirectory = Path.Combine(AppPaths.DataDir, "browser", browser.ProfileName),
            TargetUrl = account.TargetUrl,
            Timeout = options.Timeout,
        }, options.OnStatus, cancellationToken);
    }

    internal AwsSamlRole ChooseRole(
        AccountConfig account, IReadOnlyList<AwsSamlRole> roles,
        Func<IReadOnlyList<AwsSamlRole>, AwsSamlRole?, AwsSamlRole>? pickRole)
    {
        if (!string.IsNullOrWhiteSpace(account.RoleArn))
        {
            return roles.FirstOrDefault(r => r.RoleArn.Equals(account.RoleArn, StringComparison.Ordinal))
                ?? throw new SamlgateException($"Role {account.RoleArn} is not in the SAML response. Available roles:\n  {string.Join("\n  ", roles.Select(r => r.RoleArn))}");
        }

        if (roles.Count == 1)
        {
            return roles[0];
        }

        var lastArn = state.GetLastRole(account.Name);
        var last = roles.FirstOrDefault(r => r.RoleArn == lastArn);

        if (pickRole is not null)
        {
            return pickRole(roles, last);
        }

        return last ?? throw new SamlgateException("Multiple roles are available and none is selected. Set role_arn (samlgate configure --role <arn>) or run 'samlgate login' once to choose.");
    }
}
