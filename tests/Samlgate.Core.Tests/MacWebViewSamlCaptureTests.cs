using System.Net;
using System.Diagnostics;
using Samlgate.Browser;

namespace Samlgate.Tests;

public class MacWebViewSamlCaptureTests
{
    [Theory]
    [InlineData("en-US", BrowserCaptureMode.Interactive)]
    [InlineData(null, BrowserCaptureMode.Auto)]
    public async Task ChromiumPreferences_AreRejectedBeforeOpeningNativeWindow(string? language, BrowserCaptureMode mode)
    {
        using var dir = new TempDir();
        var error = await Assert.ThrowsAsync<SamlgateException>(() => new MacWebViewSamlCapture().CaptureAsync(
            new SamlCaptureRequest
            {
                IdpUrl = new Uri("https://idp.example.test"), ProfileDirectory = dir.File("profile"),
                Language = language, Mode = mode,
            }));
        Assert.Contains("require a Chromium browser", error.Message);
        Assert.False(Directory.Exists(dir.File("profile")));
    }

    [Fact]
    public void ProfileId_IsStableAndSeparateFromOtherProfiles()
    {
        using var dir = new TempDir();
        var first = MacWebViewSamlCapture.GetProfileId(dir.File("first"));
        Assert.Equal(first, MacWebViewSamlCapture.GetProfileId(dir.File("first")));
        Assert.NotEqual(first, MacWebViewSamlCapture.GetProfileId(dir.File("second")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(dir.File("first/webkit-profile-id")));
        }
    }

    [Fact]
    public void ProfileId_RejectsCorruptStateInsteadOfSilentlyReplacingSession()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("webkit-profile-id"), "invalid");
        Assert.Throws<SamlgateException>(() => MacWebViewSamlCapture.GetProfileId(dir.Path));
    }

    [MacWebViewTheory]
    [Trait("Category", "NativeBrowser")]
    [InlineData("https://signin.aws.amazon.com/saml", "", 1)]
    [InlineData("https://us-east-1.signin.aws.amazon.com/saml", "_blank", 300)]
    [InlineData("https://signin.amazonaws.cn/saml", "", 1)]
    [InlineData("https://signin.amazonaws-us-gov.com/saml", "", 1)]
    [InlineData("https://acs.example.test/custom", "", 1)]
    public async Task Capture_CancelsAcsAndReturnsOnlySubmittingFormsAssertion(string acs, string target, int roleCount)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            return;
        }

        Assert.True(File.Exists(MacWebViewSamlCapture.HelperPath), "Build the native helper before running macOS tests.");
        using var dir = new TempDir();
        var saml = TestSaml.Response(Enumerable.Range(0, roleCount)
            .Select(i => $"arn:aws:iam::123456789012:role/Role{i},{TestSaml.Provider}"));
        var page = dir.File("idp.html");
        await File.WriteAllTextAsync(page, $$"""
            <!doctype html><html><body onload="document.forms[1].submit()">
            <form action="https://unrelated.example.test/"><input name="SAMLResponse" value="wrong"></form>
            <form method="POST" action="{{acs}}" target="{{target}}">
              <input type="hidden" name="SAMLResponse" value="{{WebUtility.HtmlEncode(saml)}}">
            </form></body></html>
            """);
        var captured = await new MacWebViewSamlCapture().CaptureAsync(new SamlCaptureRequest
        {
            IdpUrl = new Uri(page),
            ProfileDirectory = dir.File("profile"),
            TargetUrl = acs.Contains("example.test", StringComparison.Ordinal) ? acs : null,
            Timeout = TimeSpan.FromSeconds(30),
        });
        Assert.Equal(saml, captured);
        await RemoveProfileAsync(dir.File("profile"));
    }

    [MacWebViewFact]
    [Trait("Category", "NativeBrowser")]
    public async Task Capture_MissingAssertionFailsWithoutNavigatingToAws()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            return;
        }

        using var dir = new TempDir();
        var page = dir.File("idp.html");
        await File.WriteAllTextAsync(page, """
            <html><body onload="document.forms[0].submit()">
            <form method="POST" action="https://signin.aws.amazon.com/saml">
            <input name="RelayState" value=""></form></body></html>
            """);
        var error = await Assert.ThrowsAsync<SamlgateException>(() => new MacWebViewSamlCapture().CaptureAsync(
            new SamlCaptureRequest
            {
                IdpUrl = new Uri(page), ProfileDirectory = dir.File("profile"), Timeout = TimeSpan.FromSeconds(30),
            }));
        Assert.Contains("could not read", error.Message);
        await RemoveProfileAsync(dir.File("profile"));
    }

    [MacWebViewFact]
    [Trait("Category", "NativeBrowser")]
    public async Task Capture_TimeoutAndCancellationCloseHelper()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            return;
        }

        using var dir = new TempDir();
        var page = dir.File("blank.html");
        await File.WriteAllTextAsync(page, "<html><body>Waiting for a synthetic sign-in.</body></html>");
        var request = new SamlCaptureRequest
        {
            IdpUrl = new Uri(page), ProfileDirectory = dir.File("profile"), Timeout = TimeSpan.FromSeconds(2),
        };
        var error = await Assert.ThrowsAsync<SamlgateException>(() => new MacWebViewSamlCapture().CaptureAsync(request));
        Assert.Contains("Timed out", error.Message);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MacWebViewSamlCapture().CaptureAsync(
            request with { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken: cancel.Token));
        await RemoveProfileAsync(dir.File("profile"));
    }

    [MacWebViewFact]
    [Trait("Category", "NativeBrowser")]
    public async Task Capture_PersistsWebStorageOnlyInTheSameNativeProfile()
    {
        using var dir = new TempDir();
        var page = dir.File("idp.html");
        await File.WriteAllTextAsync(page, """
            <html><body><form method="POST" action="https://signin.aws.amazon.com/saml">
            <input name="SAMLResponse"></form><script>
            const value = localStorage.getItem('synthetic-session') ? 'cmV1c2Vk' : 'Zmlyc3Q=';
            localStorage.setItem('synthetic-session', 'true');
            document.forms[0].elements.SAMLResponse.value = value;
            document.forms[0].submit();
            </script></body></html>
            """);
        var request = new SamlCaptureRequest
        {
            IdpUrl = new Uri(page), ProfileDirectory = dir.File("profile"), Timeout = TimeSpan.FromSeconds(30),
        };
        var capture = new MacWebViewSamlCapture();
        try
        {
            Assert.Equal("Zmlyc3Q=", await capture.CaptureAsync(request));
            Assert.Equal("cmV1c2Vk", await capture.CaptureAsync(request));
            Assert.Equal("Zmlyc3Q=", await capture.CaptureAsync(request with { ProfileDirectory = dir.File("other") }));
        }
        finally
        {
            await RemoveProfileAsync(dir.File("profile"));
            await RemoveProfileAsync(dir.File("other"));
        }
    }

    private static async Task RemoveProfileAsync(string directory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(MacWebViewSamlCapture.HelperPath)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "--remove-profile", MacWebViewSamlCapture.GetProfileId(directory).ToString() },
            },
        };
        process.Start();
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}

internal sealed class MacWebViewFactAttribute : FactAttribute
{
    public MacWebViewFactAttribute()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            Skip = "Requires macOS 14+ and an interactive desktop.";
        }
    }
}

internal sealed class MacWebViewTheoryAttribute : TheoryAttribute
{
    public MacWebViewTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            Skip = "Requires macOS 14+ and an interactive desktop.";
        }
    }
}
