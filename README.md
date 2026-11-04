# samlgate

**AWS credentials from a browser-based SAML sign-in — a drop-in replacement for `saml2aws login` with the Browser provider.**

[한국어](README.ko.md)

samlgate opens your IdP sign-in (Microsoft Entra ID, Okta, Google Workspace, …) in a real browser, catches the
SAML assertion on its way to AWS, exchanges it for temporary credentials with STS, and writes them to
`~/.aws/credentials` in the same format saml2aws uses. The Windows release is a single ~13 MB native binary with no runtime,
no Playwright and no browser-driver downloads.

It is built for teams that:

- work in **one or a few AWS accounts handed to them by a parent organisation or partner**, so
  **IAM Identity Center is not an option**, and
- manage people in **Microsoft Entra ID** using the *AWS Single-Account Access* enterprise app (SAML),
  where sign-in means a browser, MFA and Conditional Access — not a username/password prompt in a terminal.

## How it works

SAML has no `localhost` callback. After sign-in the IdP makes the browser `POST` the `SAMLResponse` to the fixed AWS
endpoint `https://signin.aws.amazon.com/saml`. samlgate:

1. starts Edge/Chrome with a **dedicated profile** and the DevTools Protocol enabled,
2. intercepts exactly that ACS request (aws, aws-cn and aws-us-gov partitions) — it never reaches AWS,
3. answers it with a local "you can close this window" page and closes the browser,
4. calls STS `AssumeRoleWithSAML` and writes the profile.

Because the dedicated profile keeps the IdP's cookies, later renewals can reuse your session while the IdP permits it.
Session lifetime, MFA and organization policies can require another interactive sign-in. (Answer **"Yes" to "Stay signed in?"** on the first
Entra sign-in — samlgate closes the browser every time, so a session-only cookie would be lost.)

## Install

> **Windows and macOS.** Login has been verified on Windows 11 x64 and on Apple Silicon Macs (M1 and M4 Pro)
> with Microsoft Edge (details in [Tested environments](#tested-environments)).
> Packaged releases cover Windows x64/ARM64 and Apple Silicon macOS (via Homebrew). The macOS 14+ native WebView backend remains experimental, as does Linux support (see [Roadmap](#roadmap)).

### Windows

Download `samlgate-<version>-win-x64.zip` (or `win-arm64`) from
[Releases](https://github.com/figurecurator/samlgate/releases) and extract `samlgate.exe` into a folder on your `PATH`.

Before adding the folder to `PATH`, run these commands inside it:

```powershell
.\samlgate.exe --version
.\samlgate.exe login --help
```

Compare the downloaded ZIP's SHA256 (`Get-FileHash <zip-path> -Algorithm SHA256`) with the release's `SHA256SUMS`.
Windows ARM64 is packaged; runtime and login validation on ARM64 hardware are still pending.
WinGet and Scoop manifests are being prepared in [`packaging/windows`](packaging/windows); they are not published yet.

To build from source: `dotnet publish src/Samlgate -c Release -r win-x64` (.NET 10 SDK and the Visual Studio
"Desktop development with C++" workload for NativeAOT).

**Requirements:** Microsoft Edge, which ships with Windows — or Google Chrome, Brave, Chromium or Whale.

### macOS

**Apple Silicon** — install with [Homebrew](https://brew.sh):

```bash
brew install figurecurator/tap/samlgate     # installs samlgate and its samlgate-webview helper
samlgate --version
```

`brew upgrade samlgate` updates it and `brew uninstall samlgate` removes the binaries. Uninstalling leaves
`~/.samlgate`, `~/.aws/credentials` and the browser profiles in `~/Library/Application Support/samlgate` in place;
delete the dedicated Chromium profile directory to clear that browser's stored session. Native WebView data lives
outside that directory; use the named-store removal command described below before deleting its profile identifier.

**Requirements:** Microsoft Edge or Google Chrome (or Brave, Chromium, Whale) for the default backend.

**Intel Macs** — there is no packaged build. The source builds for `osx-x64` and the result starts under Rosetta on
Apple Silicon, but no one has run it on Intel hardware yet. Reports are welcome.

**From source** (either architecture) — needs the .NET 10 SDK and Xcode command-line tools:

```bash
dotnet publish src/Samlgate -c Release -r osx-arm64 -o out    # osx-x64 for Intel
./out/samlgate --version
```

Keep `samlgate` and `samlgate-webview` in the same folder. The helper is built universal (arm64 + x86_64), so
cross-building `osx-x64` on Apple Silicon also produces a working helper.

### Experimental: native macOS WebView

`samlgate login --browser webview` signs in without Chromium. It is experimental because:

- **It has less real-world coverage than Chromium.** A Microsoft Entra ID sign-in (including "Stay signed in?") has
  completed in it on an Apple Silicon MacBook Pro, but other IdPs, MFA methods and long-running session reuse have
  not been tested. Automated tests (CI and local) use local synthetic pages only.
- The helper is a plain, ad-hoc-signed command-line program, not a signed and notarized app bundle. WebKit features
  that depend on app entitlements — notably passkeys and other platform authenticators — may be unavailable, and
  device-based Conditional Access has not been tested.
- It is interactive only: no `--browser-mode auto` silent refresh and no `--browser-language`.

`--browser webview` opens an AppKit window backed by WKWebView; no Chromium installation or DevTools port is needed. It requires macOS 14+ and a user-controlled graphical desktop. Sign in directly in the window. Save the backend with `samlgate configure --browser webview --url "<IdP sign-in URL>"`. Chromium remains the default; `--browser-path` applies only to Chromium.

The companion `samlgate-webview` executable cancels the AWS ACS POST before reading its submitting form. The assertion travels to the CLI over a private child-process pipe and then to STS; it is not written to disk or printed by `login`. The backend supports AWS partition/regional ACS URLs and `target_url`, and fails closed if the form cannot be read. It does not add headless login or automatic browser fallback.

The native browser uses a separate persistent WebKit data store, identified by `SAMLGATE_DATA_DIR/browser/webview/webkit-profile-id`. WebKit manages the website data outside `SAMLGATE_DATA_DIR`; it is separate from Safari and Chromium. Keep that identifier to reuse the same session. Deleting it selects a new store but does **not** erase the old store. To erase this backend's store, close its windows and run `samlgate-webview --remove-profile <UUID-from-webkit-profile-id>` before deleting the identifier. This touches only that named store.

Session reuse and passkey availability depend on the OS, IdP and its policies. Local native login does not establish that a cloud environment can perform user-controlled authentication or use the same passkey provider.

## Quick start

```bash
# 1. Save your IdP-initiated sign-in URL (see "Finding the sign-in URL" below)
samlgate configure --url "https://launcher.myapps.microsoft.com/api/signin/<app-id>?tenantId=<tenant-id>"

# 2. Sign in — writes the [saml] profile to ~/.aws/credentials
samlgate login

# 3. Use it
aws sts get-caller-identity --profile saml
samlgate status
```

Already using saml2aws? Skip step 1 — samlgate reads the same account from `~/.saml2aws` when `~/.samlgate` does
not have it.

### Moving from saml2aws

Keep your existing configuration and replace `saml2aws login -a work` with `samlgate login -a work`.
The fallback reads the matching account section without modifying `~/.saml2aws`. Confirm the IdP-initiated SAML
URL and `aws_profile`; unknown keys are ignored, and provider-specific authentication behavior is not reproduced.
The dedicated browser profile is separate from your everyday browser, so the first sign-in is interactive.

### First sign-in and later renewals

1. **First `samlgate login`** opens a browser window on a dedicated profile. Sign in, complete MFA, and answer
   **"Yes" to "Stay signed in?"**. If your IdP app has several AWS roles, pick one in the terminal; the choice is
   remembered per account. The window closes by itself and the session is printed.
2. **While the credentials are valid** (more than 5 minutes left), `samlgate login` only prints the session and
   opens nothing. Measured: 0.12 s on an M4 Pro MacBook Pro (macOS 27.2, Homebrew 0.2.0 build).
3. **When renewal is needed**, `samlgate login` opens the browser and attempts to reuse the stored IdP session.
   With `browser_mode = auto` (Chromium only), an existing profile is tried headlessly first; if the silent attempt
   times out, an interactive window opens for sign-in or MFA. Passwordless reuse depends on the IdP's policies.
4. Use `--force` to sign in again early, for example to switch roles. `samlgate status` (exit code `0` if valid) is
   the check for scripts.

Unattended renewal after the AWS credentials actually expire has not been verified. These measurements force a new
IdP visit using `--force`; they do not measure the existing-credential shortcut. One measurement: `samlgate login --force` in `auto` mode
with a stored Entra session finished headless, without a window, in 2.96 s (single run, M4 Pro MacBook Pro, Edge,
source build of `main`, 2026-11-04) and 3.87 s with the Homebrew 0.2.0 build on the same Mac. Expect a visible
window and longer when the IdP asks for MFA.

### Finding the sign-in URL (Microsoft Entra ID)

Entra admin center → **Enterprise applications** → your AWS app → **Properties** → **User access URL**. Users can
also right-click the app tile in [My Apps](https://myapps.microsoft.com) and copy the link.

## Commands

| Command | |
|---|---|
| `samlgate login` | Sign in if the stored credentials expire within 5 minutes (or `--force`), then print the session. |
| `samlgate status` | Print the session; exit code `0` if valid, `1` otherwise. Handy in scripts. |
| `samlgate configure --url …` | Create/update an account in `~/.samlgate`. |
| `samlgate credential-process` | Print credentials as [`credential_process`](https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-sourcing-external.html) JSON, signing in if needed. |
| `samlgate exec -- <cmd> …` | Run a command with `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`/`AWS_SESSION_TOKEN` set. |

Common options: `-a/--idp-account <name>` (config section, default `default`), `-p/--profile <name>` (credentials
profile, default `saml`), `--role <arn>`, `--region`, `--session-duration <seconds>`, `--browser edge|chrome|brave|chromium|whale|webview`,
`--browser-path <exe>`, `--timeout <seconds>`. Run `samlgate <command> --help` for the full list.

### Chromium language and session refresh

Chrome, Edge, Brave, Chromium and Whale use the same CDP capture backend. Use `--browser whale` to select Whale, or `--browser-path` for an executable outside the usual installation locations. Browser/vendor policies still need to permit CDP.

```bash
# Explicit language selection can help when an IdP's localized assets fail to load.
samlgate login --browser chrome --browser-language en-US

# Revisit the IdP even if current AWS credentials are valid; try the stored session first.
samlgate login --browser chrome --browser-mode auto --browser-language en-US --force

# Save preferences for future logins.
samlgate configure --url "<IdP sign-in URL>" --browser chrome --browser-language en-US --browser-mode auto
```

The default remains an interactive window with no language override. No language is automatically forced for a particular IdP. `--browser-language` accepts one language tag; remove `browser_language` from the account configuration to return to the browser default.

In `auto` mode, a profile that already exists is tried with `--headless=new`. If no SAML arrives before `--silent-timeout` (default 20 seconds), that browser is closed before an interactive browser opens on the same profile. A new profile goes directly to an interactive window. The silent attempt and interactive sign-in share `--timeout`; caller cancellation, browser launch failures and malformed ACS responses do not trigger fallback. Valid AWS credentials still skip browser login unless `--force` is used.

Silent refresh does not enter passwords, approve passkeys or bypass IdP policies. Reauthentication requires a user-controlled graphical browser; this option does not provide SSH/cloud user handoff. The native `webview` backend rejects Chromium language/auto preferences instead of silently ignoring them. Native and Chromium sessions are separate.

### Roles

The SAML response lists every AWS IAM role your user is assigned in the IdP app. With one role samlgate uses it;
with several it asks (and remembers your choice per account). Pin one with `role_arn` / `--role`.

### Using credential_process

Point a **different** profile at samlgate (a profile that also has static keys in `~/.aws/credentials` would use
those instead):

```ini
# ~/.aws/config
[profile work]
credential_process = samlgate credential-process -a default
region = ap-northeast-2
```

When no terminal is attached, samlgate cannot ask for a role — set `role_arn`, or run `samlgate login` once so it
remembers your choice.

## Configuration

`~/.samlgate` (override with `SAMLGATE_CONFIG`) is an INI file with one section per account. Key names match
saml2aws, so you can copy `~/.saml2aws` as-is; unknown keys are ignored.

| Key | Default | |
|---|---|---|
| `url` | — | IdP-initiated sign-in URL. saml2aws `provider = AzureAD` + `app_id` is converted automatically. |
| `aws_profile` | `saml` | Credentials profile to write. |
| `role_arn` | — | Role to assume without asking. |
| `region` | partition default (`us-east-1`) | STS region. |
| `aws_session_duration` | assertion's `SessionDuration`, else 3600 | Falls back to 3600 if the role's maximum is lower. |
| `credentials_file` | `~/.aws/credentials` | Also honours `AWS_SHARED_CREDENTIALS_FILE`. |
| `browser_type` | auto-detected Chromium | `edge`/`msedge`, `chrome`, `brave`, `chromium`, `whale`, or experimental `webview` (macOS 14+). |
| `browser_executable_path` | auto-detected | |
| `browser_language` | browser default | Optional single language tag, such as `en-US`; Chromium only. |
| `browser_mode` | `interactive` | `auto` attempts a short headless refresh of an existing Chromium profile, then opens a window if needed. |
| `silent_timeout` | `20` | Silent attempt limit in seconds, within the overall sign-in timeout. |
| `target_url` | AWS ACS URLs | Additional URL to intercept (custom ACS). |

The credentials profile contains `aws_access_key_id`, `aws_secret_access_key`, `aws_session_token`,
`aws_security_token`, `x_principal_arn` and `x_security_token_expires`, exactly like saml2aws.

`SAMLGATE_DATA_DIR` moves the browser profiles and remembered roles (default `%LOCALAPPDATA%\samlgate` on Windows,
`~/Library/Application Support/samlgate` on macOS).

## Tested environments

| Platform | Hardware | Install | Backend | IdP | Verified |
|---|---|---|---|---|---|
| Windows 11 x64 | — | — | Edge | Microsoft Entra ID (SAML) | Login |
| macOS 27.2 (26B123) | MacBook Pro, Apple M4 Pro | Source build of `main` (Oct–Nov 2026) | Edge; `browser_mode = auto` from Nov 2026 | Microsoft Entra ID (SAML, My Apps link) | Repeated day-to-day logins; headless refresh with `--force` |
| macOS 27.2 (26B123) | MacBook Pro, Apple M4 Pro | Homebrew tap 0.2.0 | Edge, `browser_mode = auto` | Microsoft Entra ID (SAML, My Apps link) | Install, uninstall and reinstall; `brew test`; reuse of valid credentials; headless refresh with `--force` |
| macOS 26.5.1 | MacBook Air, Apple M1 | Homebrew tap 0.2.0 | Edge, interactive | Microsoft Entra ID (SAML, My Apps link) | Login; two accounts in `~/.samlgate` |
| macOS, Apple Silicon | MacBook Pro, Apple M4 Pro | Source build | Native WebView | Microsoft Entra ID (SAML, My Apps link) | Sign-in completed (2026-10); automated tests with local synthetic pages |
| macOS, Intel | — | `osx-x64` source build | — | — | Builds; starts under Rosetta only. **Not run on Intel hardware.** |

CI builds and tests on GitHub's `macos-15` (Apple Silicon) and `windows-latest` runners.

## Security notes

- The SAML assertion is only sent to AWS STS. The intercepted ACS request is answered locally.
- The Chromium browser profile is separate from your everyday profile and lives in the data directory; delete it to sign out. Native WebKit data uses the named store described in the macOS section.
- The credentials file is written atomically.
- Chromium DevTools listens on a random port bound to `127.0.0.1` only while samlgate is running. The native WebView does not open a DevTools port.
- No telemetry.

## Roadmap

- Package and harden the experimental macOS WKWebView backend; optional fallback when Chromium is unavailable
- Linux
- Firefox via WebDriver BiDi
- Harden optional Chromium auto refresh across more IdPs and platforms

## Development

```bash
dotnet test samlgate.slnx          # unit tests + a headless-browser capture test if Edge/Chrome is installed
dotnet run --project src/Samlgate -- help
```

`src/Samlgate.Core` holds everything testable (config, SAML parsing, STS, credentials file, DevTools capture);
`src/Samlgate` is the thin CLI (System.CommandLine). STS is called through AWSSDK.SecurityToken.

`ISamlCapture` separates the browser backend from `LoginFlow`. On macOS, the Core build compiles `native/macos/SamlgateWebView.swift` with the system Swift compiler and copies the helper to CLI/test/publish output. Other platforms do not build it. Native tests use only local synthetic pages, require a macOS 14+ desktop, and are skipped on other platforms. Use `dotnet test --filter 'Category!=NativeBrowser'` on a Mac without a graphical session.

## License

[MIT](LICENSE)
