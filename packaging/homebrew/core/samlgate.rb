# DRAFT for a future homebrew-core submission — not submitted. See packaging/homebrew/README.md.
# Builds from source with Homebrew's dotnet (framework-dependent, no NativeAOT), like other
# .NET formulae in homebrew-core (cyclonedx-cli, dug, gitversion, kiota).
class Samlgate < Formula
  desc "Browser-based SAML sign-in that writes saml2aws-compatible AWS credentials"
  homepage "https://github.com/figurecurator/samlgate"
  # Needs a release that contains the universal samlgate-webview build fix (after 0.2.0).
  url "https://github.com/figurecurator/samlgate/archive/refs/tags/v0.3.0.tar.gz"
  sha256 "0000000000000000000000000000000000000000000000000000000000000000"
  license "MIT"
  head "https://github.com/figurecurator/samlgate.git", branch: "main"

  depends_on xcode: ["15.0", :build] # swiftc and the macOS 14 SDK for the WebView helper
  depends_on "dotnet"
  depends_on :macos # Linux support is not finished upstream

  deny_network_access!

  def dotnet = deps.find { |d| d.name.match?(/^dotnet(@\d+)?$/) }.to_formula

  def fetch
    system "dotnet", "restore", "src/Samlgate/Samlgate.csproj", "--use-current-runtime", "-p:PublishAot=false"
  end

  def install
    args = %W[
      --configuration Release
      --no-restore
      --no-self-contained
      --use-current-runtime
      --output #{libexec}
      -p:AppHostRelativeDotNet=#{dotnet.opt_libexec.relative_path_from(libexec)}
      -p:DebugType=None
      -p:PublishAot=false
      -p:PublishSingleFile=true
      -p:Version=#{version}
    ]
    system "dotnet", "publish", "src/Samlgate/Samlgate.csproj", *args
    # samlgate looks for samlgate-webview beside its real path, so link rather than move it.
    bin.install_symlink libexec/"samlgate"
  end

  test do
    # Keep everything inside testpath: config, data directory and credentials file.
    ENV["SAMLGATE_CONFIG"] = testpath/"samlgate.ini"
    ENV["SAMLGATE_DATA_DIR"] = testpath/"data"
    ENV["AWS_SHARED_CREDENTIALS_FILE"] = testpath/"credentials"

    assert_equal version.to_s, shell_output("#{bin}/samlgate --version").strip

    system bin/"samlgate", "configure", "--url", "https://login.example.com/sso"
    assert_match "url = https://login.example.com/sso", (testpath/"samlgate.ini").read

    (testpath/"credentials").write <<~INI
      [saml]
      aws_access_key_id = AKIAIOSFODNN7EXAMPLE
      aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
      aws_session_token = example
      x_security_token_expires = 2020-01-01T00:00:00Z
    INI
    assert_match "expired", shell_output("#{bin}/samlgate status", 1)

    # Never run `login` or `credential-process` here: both open a real browser.
    assert_predicate libexec/"samlgate-webview", :executable?
  end
end
