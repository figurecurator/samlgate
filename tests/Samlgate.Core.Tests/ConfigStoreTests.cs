using Samlgate.Config;

namespace Samlgate.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Load_PrefersSamlgateConfig_ThenFallsBackToSaml2Aws()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("saml2aws"), """
            [default]
            url = https://launcher.myapps.microsoft.com/api/signin/app?tenantId=t
            aws_profile = legacy
            aws_session_duration = 7200
            browser_type = msedge
            region =

            [work]
            url = https://idp.example/work
            """);
        File.WriteAllText(dir.File("samlgate"), "[work]\nurl = https://idp.example/new\n");
        var store = new ConfigStore(dir.File("samlgate"), dir.File("saml2aws"));

        var fallback = store.Load("default")!;
        Assert.Equal("https://launcher.myapps.microsoft.com/api/signin/app?tenantId=t", fallback.Url);
        Assert.Equal("legacy", fallback.Profile);
        Assert.Equal(7200, fallback.SessionDurationSeconds);
        Assert.Equal("msedge", fallback.BrowserType);
        Assert.Null(fallback.Region);
        Assert.Equal(dir.File("saml2aws"), fallback.Source);

        Assert.Equal("https://idp.example/new", store.Load("work")!.Url);
        Assert.Null(store.Load("missing"));
    }

    [Fact]
    public void Load_Saml2AwsAzureAdProvider_BuildsLauncherUrlFromAppId()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("saml2aws"), """
            [default]
            provider = AzureAD
            url = https://account.activedirectory.windowsazure.com
            app_id = 1234-abcd
            """);
        var store = new ConfigStore(dir.File("samlgate"), dir.File("saml2aws"));

        Assert.Equal("https://launcher.myapps.microsoft.com/api/signin/1234-abcd", store.Load("default")!.Url);
    }

    [Fact]
    public void Save_RoundTrips_AndDefaultsProfile()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("samlgate"), dir.File("saml2aws"));

        store.Save(new AccountConfig { Name = "prod", Url = "https://idp.example", RoleArn = TestSaml.DeveloperRole, BrowserType = "webview" });

        var loaded = store.Load("prod")!;
        Assert.Equal("https://idp.example", loaded.Url);
        Assert.Equal(TestSaml.DeveloperRole, loaded.RoleArn);
        Assert.Equal(AccountConfig.DefaultProfile, loaded.Profile);
        Assert.Equal("webview", loaded.BrowserType);
    }

    [Fact]
    public void StateStore_RemembersRolePerAccount()
    {
        using var dir = new TempDir();
        var state = new StateStore(dir.File("state"));

        state.SetLastRole("a", "arn:1");
        state.SetLastRole("b", "arn:2");
        state.SetLastRole("a", "arn:3");

        Assert.Equal("arn:3", state.GetLastRole("a"));
        Assert.Equal("arn:2", state.GetLastRole("b"));
        Assert.Null(state.GetLastRole("c"));
    }

    [Fact]
    public void BrowserPreferences_RoundTripAndRejectInvalidSilentTimeout()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("config"), dir.File("unused"));
        store.Save(new AccountConfig
        {
            Name = "default", Url = "https://idp.example.test", BrowserLanguage = "en-US",
            BrowserMode = "auto", SilentTimeoutSeconds = 5,
        });
        var loaded = store.Load("default")!;
        Assert.Equal("en-US", loaded.BrowserLanguage);
        Assert.Equal("auto", loaded.BrowserMode);
        Assert.Equal(5, loaded.SilentTimeoutSeconds);
        File.WriteAllText(dir.File("config"), "[default]\nsilent_timeout = 0\n");
        Assert.Throws<SamlgateException>(() => store.Load("default"));
    }
}
