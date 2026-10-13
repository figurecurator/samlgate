using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Samlgate.Aws;
using Samlgate.Config;
using Samlgate.Browser;

namespace Samlgate.Tests;

public class LoginFlowTests
{
    [Fact]
    public async Task RunAsync_UsesInjectedCaptureBeforeStsAndCredentialsWrite()
    {
        using var dir = new TempDir();
        var sts = new FakeSts();
        var capture = new FakeCapture(TestSaml.Response($"{TestSaml.DeveloperRole},{TestSaml.Provider}"));
        var flow = new LoginFlow(sts.Client, new StateStore(dir.File("state")), capture);
        await flow.RunAsync(new AccountConfig
        {
            Name = "default", Url = "https://idp.example.test/signin", CredentialsFile = dir.File("credentials"),
            TargetUrl = "https://acs.example.test/saml",
        }, new LoginOptions { Timeout = TimeSpan.FromSeconds(42) });
        Assert.Equal("https://idp.example.test/signin", capture.Request!.IdpUrl.AbsoluteUri);
        Assert.Equal("https://acs.example.test/saml", capture.Request.TargetUrl);
        Assert.Equal(TimeSpan.FromSeconds(42), capture.Request.Timeout);
        Assert.EndsWith(Path.Combine("browser", "fake"), capture.Request.ProfileDirectory);
        Assert.Single(sts.Requests);
        Assert.NotNull(CredentialsFile.Read(dir.File("credentials"), "saml"));
    }

    private sealed class FakeCapture(string assertion) : ISamlCapture
    {
        public string ProfileName => "fake";
        public string DisplayName => "Synthetic browser";
        public SamlCaptureRequest? Request { get; private set; }
        public Task<string> CaptureAsync(SamlCaptureRequest request, Action<string>? onStatus = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(assertion);
        }
    }

    [Fact]
    public async Task RunAsync_WritesSaml2AwsCompatibleProfile()
    {
        using var dir = new TempDir();
        var sts = new FakeSts();
        var flow = new LoginFlow(sts.Client, new StateStore(dir.File("state")));
        var account = new AccountConfig { Name = "default", CredentialsFile = dir.File("credentials") };

        var credentials = await flow.RunAsync(account, new LoginOptions
        {
            SamlResponseInput = new StringReader(TestSaml.Response($"{TestSaml.DeveloperRole},{TestSaml.Provider}")),
        });

        Assert.Equal("ASIAIOSFODNN7EXAMPLE", credentials.AccessKeyId);
        var section = IniFile.ReadSection(dir.File("credentials"), "saml")!;
        Assert.Equal("ASIAIOSFODNN7EXAMPLE", section["aws_access_key_id"]);
        Assert.Equal("token", section["aws_session_token"]);
        Assert.Equal("token", section["aws_security_token"]);
        Assert.Equal("arn:aws:sts::123456789012:assumed-role/Developer/alice@example.com", section["x_principal_arn"]);
        Assert.True(section.ContainsKey("x_security_token_expires"));

        var stored = CredentialsFile.Read(dir.File("credentials"), "saml")!;
        Assert.Equal(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero), stored.Expiration);

        Assert.Equal(RegionEndpoint.USEast1, sts.Regions.Single());
        Assert.Equal(TestSaml.DeveloperRole, sts.Requests.Single().RoleArn);
    }

    [Fact]
    public async Task RunAsync_RetriesWithDefaultDuration_WhenRoleMaxIsShorter()
    {
        using var dir = new TempDir();
        var sts = new FakeSts(failFirstWith: "ValidationError");
        var flow = new LoginFlow(sts.Client, new StateStore(dir.File("state")));
        var account = new AccountConfig { Name = "default", CredentialsFile = dir.File("credentials") };

        await flow.RunAsync(account, new LoginOptions
        {
            SamlResponseInput = new StringReader(
                TestSaml.Response([$"{TestSaml.DeveloperRole},{TestSaml.Provider}"], sessionDuration: 43200)),
        });

        Assert.Equal(43200, sts.Requests[0].DurationSeconds);
        Assert.Equal(3600, sts.Requests[1].DurationSeconds);
    }

    [Fact]
    public void ChooseRole_UsesConfiguredRole_OrFailsWhenMissing()
    {
        using var dir = new TempDir();
        var flow = new LoginFlow(new StsClient(), new StateStore(dir.File("state")));
        var roles = TwoRoles();

        var configured = new AccountConfig { Name = "default", RoleArn = TestSaml.ReadOnlyRole };
        Assert.Equal(TestSaml.ReadOnlyRole, flow.ChooseRole(configured, roles, null).RoleArn);

        var wrong = new AccountConfig { Name = "default", RoleArn = "arn:aws:iam::1:role/Nope" };
        Assert.Throws<SamlgateException>(() => flow.ChooseRole(wrong, roles, null));
    }

    [Fact]
    public void ChooseRole_NonInteractive_UsesRememberedRole()
    {
        using var dir = new TempDir();
        var state = new StateStore(dir.File("state"));
        var flow = new LoginFlow(new StsClient(), state);
        var account = new AccountConfig { Name = "default" };

        Assert.Throws<SamlgateException>(() => flow.ChooseRole(account, TwoRoles(), null));

        state.SetLastRole("default", TestSaml.ReadOnlyRole);
        Assert.Equal(TestSaml.ReadOnlyRole, flow.ChooseRole(account, TwoRoles(), null).RoleArn);
    }

    [Fact]
    public void ChooseRole_Interactive_PassesRememberedRoleAsDefault()
    {
        using var dir = new TempDir();
        var state = new StateStore(dir.File("state"));
        state.SetLastRole("default", TestSaml.ReadOnlyRole);
        var flow = new LoginFlow(new StsClient(), state);

        AwsSamlRole? offeredDefault = null;
        var picked = flow.ChooseRole(new AccountConfig { Name = "default" }, TwoRoles(), (roles, last) =>
        {
            offeredDefault = last;
            return roles[0];
        });

        Assert.Equal(TestSaml.ReadOnlyRole, offeredDefault?.RoleArn);
        Assert.Equal(TestSaml.DeveloperRole, picked.RoleArn);
    }

    [Theory]
    [InlineData("aws", null, "us-east-1")]
    [InlineData("aws", "ap-northeast-2", "ap-northeast-2")]
    [InlineData("aws-cn", null, "cn-north-1")]
    [InlineData("aws-us-gov", null, "us-gov-west-1")]
    public void RegionFor_PicksPartitionDefault(string partition, string? region, string expected)
    {
        Assert.Equal(expected, StsClient.RegionFor(partition, region));
    }

    [Theory]
    [InlineData("arn:aws:sts::123456789012:assumed-role/Developer/alice", "Developer (123456789012)")]
    [InlineData("garbage", "garbage")]
    public void DescribePrincipal(string arn, string expected)
    {
        Assert.Equal(expected, CredentialsFile.DescribePrincipal(arn));
    }

    private static IReadOnlyList<AwsSamlRole> TwoRoles() =>
    [
        new(TestSaml.DeveloperRole, TestSaml.Provider),
        new(TestSaml.ReadOnlyRole, TestSaml.Provider),
    ];

    /// <summary>Stands in for STS; optionally rejects the first call with the given error code.</summary>
    private sealed class FakeSts(string? failFirstWith = null)
        : AmazonSecurityTokenServiceClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public List<RegionEndpoint> Regions { get; } = [];

        public List<AssumeRoleWithSAMLRequest> Requests { get; } = [];

        public StsClient Client => new(region =>
        {
            Regions.Add(region);
            return this;
        });

        public override Task<AssumeRoleWithSAMLResponse> AssumeRoleWithSAMLAsync(
            AssumeRoleWithSAMLRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (failFirstWith is not null && Requests.Count == 1)
            {
                throw new AmazonSecurityTokenServiceException("rejected") { ErrorCode = failFirstWith };
            }

            return Task.FromResult(new AssumeRoleWithSAMLResponse
            {
                Credentials = new Credentials
                {
                    AccessKeyId = "ASIAIOSFODNN7EXAMPLE",
                    SecretAccessKey = "secret",
                    SessionToken = "token",
                    Expiration = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
                AssumedRoleUser = new AssumedRoleUser
                {
                    Arn = "arn:aws:sts::123456789012:assumed-role/Developer/alice@example.com",
                },
            });
        }
    }
}
