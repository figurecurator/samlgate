using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Samlgate.Browser;

namespace Samlgate.Tests;

public class BrowserSamlCaptureTests
{
    [Fact]
    public void FormField_DecodesUrlEncodedValue()
    {
        const string body = "RelayState=x&SAMLResponse=PHNhbWw%2BaGk8L3NhbWw%2B+Kw%3D%3D";
        Assert.Equal("PHNhbWw+aGk8L3NhbWw+ Kw==", BrowserSamlCapture.FormField(body, "SAMLResponse"));
        Assert.Null(BrowserSamlCapture.FormField(body, "Missing"));
        Assert.Null(BrowserSamlCapture.FormField(null, "SAMLResponse"));
    }

    [Fact]
    public void ReadPostData_FallsBackToPostDataEntries()
    {
        var request = new JsonObject
        {
            ["postDataEntries"] = new JsonArray(
                (JsonNode)new JsonObject { ["bytes"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("SAMLResponse=")) },
                (JsonNode)new JsonObject { ["bytes"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("abc")) }),
        };

        Assert.Equal("SAMLResponse=abc", BrowserSamlCapture.ReadPostData(request));
        Assert.Equal("inline", BrowserSamlCapture.ReadPostData(new JsonObject { ["postData"] = "inline" }));
    }

    [Theory]
    [InlineData("msedge", "edge")]
    [InlineData("Chrome", "chrome")]
    [InlineData("naver-whale", "whale")]
    [InlineData(null, null)]
    public void NormalizeKind_AcceptsSaml2AwsNames(string? input, string? expected)
    {
        Assert.Equal(expected, BrowserLocator.NormalizeKind(input));
    }

    [Fact]
    public void NormalizeKind_RejectsUnsupportedBrowsers()
    {
        Assert.Throws<SamlgateException>(() => BrowserLocator.NormalizeKind("firefox"));
        Assert.Throws<SamlgateException>(() => BrowserLocator.NormalizeKind("netscape"));
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task Capture_LanguageOptionChangesNavigatorLanguage()
    {
        if (FindBrowser() is not { } browser)
        {
            return;
        }

        using var dir = new TempDir();
        var saml = TestSaml.Response([$"{TestSaml.DeveloperRole},{TestSaml.Provider}"]);
        var page = dir.File("language.html");
        await File.WriteAllTextAsync(page, $$"""
            <html><body><form method="POST" action="https://signin.aws.amazon.com/saml">
            <input name="SAMLResponse"></form><script>
            document.forms[0].elements.SAMLResponse.value = navigator.language === 'fr-CA' ? '{{saml}}' : 'wrong-language';
            document.forms[0].submit();
            </script></body></html>
            """);
        var captured = await BrowserSamlCapture.CaptureAsync(new CaptureOptions
        {
            IdpUrl = new Uri(page), Browser = browser, ProfileDirectory = dir.File("profile"),
            Headless = true, Language = "fr-CA", Timeout = TimeSpan.FromSeconds(30),
        });
        Assert.Equal(saml, captured);
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task AutoMode_ReusesProfileAfterSilentTimeoutInRealBrowser()
    {
        if (FindBrowser() is not { } browser)
        {
            return;
        }

        using var dir = new TempDir();
        var saml = TestSaml.Response([$"{TestSaml.DeveloperRole},{TestSaml.Provider}"]);
        await using var page = new LoopbackPage($$"""
            <html><body><form method="POST" action="https://signin.aws.amazon.com/saml">
            <input name="SAMLResponse" value="{{saml}}"></form><script>
            if (localStorage.getItem('synthetic-session')) document.forms[0].submit();
            else localStorage.setItem('synthetic-session', 'ready');
            </script></body></html>
            """);
        var profile = dir.File("profile");
        Directory.CreateDirectory(profile);
        var attempts = new List<bool>();
        var capture = new ChromiumSamlCapture(browser, (options, status, ct) =>
        {
            attempts.Add(options.Headless);
            return BrowserSamlCapture.CaptureAsync(options, status, ct);
        });
        var request = new SamlCaptureRequest
        {
            IdpUrl = page.Url, ProfileDirectory = profile, Mode = BrowserCaptureMode.Auto,
            Language = "en-US", Timeout = TimeSpan.FromSeconds(120), SilentTimeout = TimeSpan.FromSeconds(30),
        };
        Assert.Equal(saml, await capture.CaptureAsync(request));
        Assert.Equal(new[] { true, false }, attempts);
        attempts.Clear();
        Assert.Equal(saml, await capture.CaptureAsync(request));
        Assert.Equal(new[] { true }, attempts);
    }

    /// <summary>
    /// End to end against a real headless Chromium: a local page auto-POSTs a SAMLResponse to the AWS ACS URL,
    /// exactly like an IdP does, and samlgate must capture it without the request leaving the machine.
    /// 300 roles make a ~100 KB body, to catch Chromium omitting large POST data.
    /// Passes trivially when no Chromium-based browser is installed.
    /// </summary>
    [Theory]
    [Trait("Category", "Browser")]
    [InlineData(1)]
    [InlineData(300)]
    public async Task CaptureAsync_InterceptsAcsPostInRealBrowser(int roleCount)
    {
        if (FindBrowser() is not { } browser)
        {
            return;
        }

        var saml = TestSaml.Response(Enumerable.Range(0, roleCount)
            .Select(i => $"arn:aws:iam::123456789012:role/Role{i},{TestSaml.Provider}"));

        var captured = await CaptureFormPostAsync(browser,
            $"""<input type="hidden" name="SAMLResponse" value="{WebUtility.HtmlEncode(saml)}">""");

        Assert.Equal(saml, captured);
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task CaptureAsync_FailsOnAcsPostWithoutSamlResponse()
    {
        if (FindBrowser() is not { } browser)
        {
            return;
        }

        var error = await Assert.ThrowsAsync<SamlgateException>(
            () => CaptureFormPostAsync(browser, """<input type="hidden" name="RelayState" value="">"""));

        Assert.Contains("SAMLResponse", error.Message);
    }

    private static BrowserInfo? FindBrowser()
    {
        try
        {
            return BrowserLocator.Locate(Environment.GetEnvironmentVariable("SAMLGATE_TEST_BROWSER"), null);
        }
        catch (SamlgateException)
        {
            return null;
        }
    }

    /// <summary>Opens a local page that auto-POSTs the given form fields to the AWS ACS URL.</summary>
    private static async Task<string> CaptureFormPostAsync(BrowserInfo browser, string fields)
    {
        using var dir = new TempDir();
        var page = dir.File("idp.html");
        await File.WriteAllTextAsync(page, $"""
            <!doctype html><html><body onload="document.forms[0].submit()">
            <form method="POST" action="https://signin.aws.amazon.com/saml">{fields}</form></body></html>
            """);

        return await BrowserSamlCapture.CaptureAsync(new CaptureOptions
        {
            IdpUrl = new Uri(page),
            Browser = browser,
            ProfileDirectory = dir.File("profile"),
            Headless = true,
            Timeout = TimeSpan.FromSeconds(60),
        });
    }
}
