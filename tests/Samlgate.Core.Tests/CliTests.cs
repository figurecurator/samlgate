using System.CommandLine;
using Samlgate.Config;

namespace Samlgate.Tests;

public class CliTests
{
    [Fact]
    public void Parse_CommandAndOptions()
    {
        var cli = Parse("login", "-a", "work", "--profile=dev", "--role", "arn:x", "--session-duration", "7200", "--force");

        Assert.Equal("work", cli.Account);
        Assert.Equal("dev", cli.Profile);
        Assert.Equal("arn:x", cli.Role);
        Assert.Equal(7200, cli.SessionDuration);
        Assert.True(cli.Force);
        Assert.Equal(TimeSpan.FromMinutes(5), cli.Timeout);
    }

    [Fact]
    public void Parse_ExecCollectsEverythingAfterDoubleDash()
    {
        var cli = Parse("exec", "-p", "saml", "--", "aws", "s3", "ls", "--profile", "x");

        Assert.Equal("saml", cli.Profile);
        Assert.Equal(["aws", "s3", "ls", "--profile", "x"], cli.ExecArgs);
    }

    [Theory]
    [InlineData("login", "--bogus")]
    [InlineData("frobnicate")]
    [InlineData("login", "--profile")]
    [InlineData("login", "--timeout", "abc")]
    [InlineData("login", "--timeout", "0")]
    [InlineData("status", "--force")]
    [InlineData("exec")]
    public void Parse_InvalidInput_HasErrors(params string[] args)
    {
        Assert.NotEmpty(Cli.Build().Parse(args).Errors);
    }

    [Fact]
    public async Task Exec_WithoutDoubleDash_IsUsageError()
    {
        var result = Cli.Build().Parse(["exec", "aws", "s3", "ls", "--profile", "x"]);

        await Assert.ThrowsAsync<UsageException>(() => result.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false }));
    }

    [Fact]
    public void ApplyTo_FlagsOverrideConfig()
    {
        var config = new AccountConfig { Name = "default", Url = "https://a", Profile = "saml", Region = "us-east-1" };

        var merged = Parse("login", "--url", "https://b", "-p", "other").ApplyTo(config);

        Assert.Equal("https://b", merged.Url);
        Assert.Equal("other", merged.Profile);
        Assert.Equal("us-east-1", merged.Region);
    }

    [Fact]
    public void BrowserPreferences_OverrideStoredValuesWithoutChangingDefaults()
    {
        var config = new AccountConfig { Name = "default", BrowserLanguage = "ko-KR", BrowserMode = "interactive", SilentTimeoutSeconds = 20 };
        var merged = Parse("login", "--browser-language", "en-US", "--browser-mode", "auto", "--silent-timeout", "5").ApplyTo(config);
        Assert.Equal("en-US", merged.BrowserLanguage);
        Assert.Equal("auto", merged.BrowserMode);
        Assert.Equal(5, merged.SilentTimeoutSeconds);
        Assert.Equal(config, Parse("login").ApplyTo(config));
        Assert.NotEmpty(Cli.Build().Parse("login --silent-timeout 0").Errors);
    }

    private static CliOptions Parse(params string[] args)
    {
        var result = Cli.Build().Parse(args);
        Assert.Empty(result.Errors);
        return CliOptions.From(result);
    }
}
