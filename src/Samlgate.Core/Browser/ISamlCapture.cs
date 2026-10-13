namespace Samlgate.Browser;

/// <summary>A browser-independent request to capture an IdP's AWS SAML POST.</summary>
public sealed record SamlCaptureRequest
{
    public required Uri IdpUrl { get; init; }
    public required string ProfileDirectory { get; init; }
    public string? TargetUrl { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public interface ISamlCapture
{
    string ProfileName { get; }
    string DisplayName { get; }
    Task<string> CaptureAsync(SamlCaptureRequest request, Action<string>? onStatus = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Adapts the existing CDP capture without changing its public test API.</summary>
public sealed class ChromiumSamlCapture(BrowserInfo browser) : ISamlCapture
{
    public string ProfileName => browser.Kind;
    public string DisplayName => browser.DisplayName;

    public Task<string> CaptureAsync(SamlCaptureRequest request, Action<string>? onStatus = null,
        CancellationToken cancellationToken = default) => BrowserSamlCapture.CaptureAsync(new CaptureOptions
        {
            IdpUrl = request.IdpUrl,
            Browser = browser,
            ProfileDirectory = request.ProfileDirectory,
            TargetUrl = request.TargetUrl,
            Timeout = request.Timeout,
        }, onStatus, cancellationToken);
}

public static class SamlCaptureFactory
{
    public static ISamlCapture Create(string? browserType, string? executablePath)
    {
        if (browserType?.Trim().Equals("webview", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!OperatingSystem.IsMacOSVersionAtLeast(14))
            {
                throw new SamlgateException("The native WebView requires macOS 14 or later.");
            }

            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                throw new SamlgateException("--browser-path is not used with --browser webview.");
            }

            return new MacWebViewSamlCapture();
        }

        return new ChromiumSamlCapture(BrowserLocator.Locate(browserType, executablePath));
    }
}
