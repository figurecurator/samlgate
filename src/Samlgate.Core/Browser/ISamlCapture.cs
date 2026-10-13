namespace Samlgate.Browser;

/// <summary>A browser-independent request to capture an IdP's AWS SAML POST.</summary>
public sealed record SamlCaptureRequest
{
    public required Uri IdpUrl { get; init; }
    public required string ProfileDirectory { get; init; }
    public string? TargetUrl { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public string? Language { get; init; }
    public BrowserCaptureMode Mode { get; init; }
    public TimeSpan SilentTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

public interface ISamlCapture
{
    string ProfileName { get; }
    string DisplayName { get; }
    Task<string> CaptureAsync(SamlCaptureRequest request, Action<string>? onStatus = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Adapts the existing CDP capture without changing its public test API.</summary>
public sealed class ChromiumSamlCapture : ISamlCapture
{
    private readonly BrowserInfo _browser;
    private readonly Func<CaptureOptions, Action<string>?, CancellationToken, Task<string>> _capture;

    public ChromiumSamlCapture(BrowserInfo browser) : this(browser, BrowserSamlCapture.CaptureAsync) { }

    internal ChromiumSamlCapture(BrowserInfo browser,
        Func<CaptureOptions, Action<string>?, CancellationToken, Task<string>> capture)
    {
        _browser = browser;
        _capture = capture;
    }

    public string ProfileName => _browser.Kind;
    public string DisplayName => _browser.DisplayName;

    public async Task<string> CaptureAsync(SamlCaptureRequest request, Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = BrowserPreferences.ValidateLanguage(request.Language);
        if (!Enum.IsDefined(request.Mode) || request.SilentTimeout <= TimeSpan.Zero || request.Timeout <= TimeSpan.Zero)
        {
            throw new SamlgateException("Browser mode and timeouts must be valid and positive.");
        }

        var options = new CaptureOptions
        {
            IdpUrl = request.IdpUrl,
            Browser = _browser,
            ProfileDirectory = request.ProfileDirectory,
            TargetUrl = request.TargetUrl,
            Timeout = request.Timeout,
            Language = language,
        };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(request.Timeout);
        try
        {
            // A first sign-in always needs a user-controlled window. A profile directory is
            // evidence of a previous browser run, not proof that its IdP session is still valid.
            if (request.Mode == BrowserCaptureMode.Auto && Directory.Exists(request.ProfileDirectory))
            {
                onStatus?.Invoke("Trying the existing browser session without a window...");
                using var silent = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                silent.CancelAfter(request.SilentTimeout);
                try
                {
                    return await _capture(options with { Headless = true }, onStatus, silent.Token);
                }
                catch (OperationCanceledException) when (silent.IsCancellationRequested && !budget.IsCancellationRequested)
                {
                    // CaptureAsync has already closed the headless browser and flushed its profile.
                    // Only the silent attempt's deadline triggers fallback. Browser/ACS errors
                    // and caller cancellation are not silently turned into another sign-in.
                    onStatus?.Invoke("The session needs an interactive browser. Complete the sign-in in the new window.");
                }
            }

            budget.Token.ThrowIfCancellationRequested();
            return await _capture(options, onStatus, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SamlgateException($"Timed out after {request.Timeout.TotalSeconds:0} seconds waiting for the sign-in to finish.");
        }
    }
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
