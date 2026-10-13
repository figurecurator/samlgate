using Samlgate.Browser;

namespace Samlgate.Tests;

public class ChromiumSessionTests
{
    private static readonly BrowserInfo Browser = new("chrome", "synthetic-browser");

    [Theory]
    [InlineData(null, null)]
    [InlineData(" en-US ", "en-US")]
    [InlineData("ko-KR", "ko-KR")]
    [InlineData("zh-Hant", "zh-Hant")]
    public void Language_AcceptsSingleLanguageTags(string? input, string? expected) =>
        Assert.Equal(expected, BrowserPreferences.ValidateLanguage(input));

    [Theory]
    [InlineData("--no-sandbox")]
    [InlineData("en_US")]
    [InlineData("en-US,en")]
    [InlineData("en\r\nInjected: value")]
    public void Language_RejectsFlagsHeadersAndLists(string input) =>
        Assert.Throws<SamlgateException>(() => BrowserPreferences.ValidateLanguage(input));

    [Fact]
    public void Mode_DefaultsToInteractiveAndRejectsUnsupportedModes()
    {
        Assert.Equal(BrowserCaptureMode.Interactive, BrowserPreferences.ParseMode(null));
        Assert.Equal(BrowserCaptureMode.Auto, BrowserPreferences.ParseMode("AUTO"));
        Assert.Throws<SamlgateException>(() => BrowserPreferences.ParseMode("headless"));
    }

    [Theory]
    [InlineData(false, BrowserCaptureMode.Auto)]
    [InlineData(true, BrowserCaptureMode.Interactive)]
    public async Task FirstSignInAndDefaultMode_AlwaysOpenInteractive(bool existing, BrowserCaptureMode mode)
    {
        using var dir = new TempDir();
        var request = Request(dir.File("profile")) with { Mode = mode, Language = "fr-CA" };
        if (existing)
        {
            Directory.CreateDirectory(request.ProfileDirectory);
        }

        var attempts = new List<CaptureOptions>();
        var capture = new ChromiumSamlCapture(Browser, (options, _, _) =>
        {
            attempts.Add(options);
            return Task.FromResult("synthetic assertion");
        });
        await capture.CaptureAsync(request);
        Assert.Single(attempts);
        Assert.False(attempts[0].Headless);
        Assert.Equal("fr-CA", attempts[0].Language);
    }

    [Fact]
    public async Task ExistingSession_CapturesHeadlesslyWithoutOpeningInteractive()
    {
        using var dir = new TempDir();
        var request = Request(dir.Path);
        var attempts = new List<CaptureOptions>();
        var capture = new ChromiumSamlCapture(Browser, (options, _, _) =>
        {
            attempts.Add(options);
            return Task.FromResult("synthetic assertion");
        });
        Assert.Equal("synthetic assertion", await capture.CaptureAsync(request));
        Assert.Single(attempts);
        Assert.True(attempts[0].Headless);
    }

    [Fact]
    public async Task SilentDeadline_ClosesAttemptBeforeInteractiveOnSameProfile()
    {
        using var dir = new TempDir();
        var attempts = new List<CaptureOptions>();
        var silentClosed = false;
        var capture = new ChromiumSamlCapture(Browser, async (options, _, ct) =>
        {
            attempts.Add(options);
            if (options.Headless)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    silentClosed = true;
                }
            }

            Assert.True(silentClosed);
            return "synthetic assertion";
        });
        var request = Request(dir.Path) with { Language = "en-US", SilentTimeout = TimeSpan.FromMilliseconds(50) };
        Assert.Equal("synthetic assertion", await capture.CaptureAsync(request));
        Assert.Equal(2, attempts.Count);
        Assert.True(attempts[0].Headless);
        Assert.False(attempts[1].Headless);
        Assert.All(attempts, a => Assert.Equal(request.ProfileDirectory, a.ProfileDirectory));
        Assert.All(attempts, a => Assert.Equal("en-US", a.Language));
    }

    [Fact]
    public async Task CallerCancellation_DoesNotOpenInteractive()
    {
        using var dir = new TempDir();
        var attempts = 0;
        var capture = new ChromiumSamlCapture(Browser, async (_, _, ct) =>
        {
            attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.CaptureAsync(
            Request(dir.Path), cancellationToken: cancel.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AlreadyCanceledRequest_DoesNotLaunchAnyBrowser()
    {
        using var dir = new TempDir();
        var attempts = 0;
        var capture = new ChromiumSamlCapture(Browser, (_, _, _) =>
        {
            attempts++;
            return Task.FromResult("unexpected");
        });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.CaptureAsync(
            Request(dir.Path), cancellationToken: cancel.Token));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task OverallDeadline_DoesNotOpenInteractiveOrResetTimeout()
    {
        using var dir = new TempDir();
        var attempts = 0;
        var capture = new ChromiumSamlCapture(Browser, async (_, _, ct) =>
        {
            attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        });
        var error = await Assert.ThrowsAsync<SamlgateException>(() => capture.CaptureAsync(
            Request(dir.Path) with { Timeout = TimeSpan.FromMilliseconds(50) }));
        Assert.Contains("Timed out", error.Message);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task BrowserFailure_IsNotRetriedAsAuthentication()
    {
        using var dir = new TempDir();
        var attempts = 0;
        var capture = new ChromiumSamlCapture(Browser, (_, _, _) =>
        {
            attempts++;
            throw new SamlgateException("synthetic browser failure");
        });
        var error = await Assert.ThrowsAsync<SamlgateException>(() => capture.CaptureAsync(Request(dir.Path)));
        Assert.Contains("browser failure", error.Message);
        Assert.Equal(1, attempts);
    }

    private static SamlCaptureRequest Request(string profile) => new()
    {
        IdpUrl = new Uri("https://idp.example.test/signin"), ProfileDirectory = profile,
        Mode = BrowserCaptureMode.Auto, Timeout = TimeSpan.FromSeconds(5), SilentTimeout = TimeSpan.FromSeconds(2),
    };
}
