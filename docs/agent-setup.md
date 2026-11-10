# samlgate setup for coding agents

> **For people:** paste this into Claude Code, Codex or another coding agent running on your own computer:
>
> ```text
> Set up samlgate for AWS sign-in on this computer by following
> https://raw.githubusercontent.com/figurecurator/samlgate/main/docs/agent-setup.md
> ```
>
> The agent installs samlgate, signs you in once (you complete the browser sign-in yourself) and teaches itself to
> use `samlgate login` for AWS from then on.

---

**For the agent:** the user asked you to set up [samlgate](https://github.com/figurecurator/samlgate) on this
computer. samlgate gets temporary AWS credentials from a browser-based SAML sign-in (Microsoft Entra ID, Okta,
Google Workspace, …) and writes them to `~/.aws/credentials` in the same format as saml2aws.

Ground rules:

- Talk to the user in the language they wrote to you in.
- Never ask for, type or store a password, MFA code or AWS secret. The user signs in in the browser window.
- samlgate needs network access and must launch a real browser. If your sandbox blocks that, ask the user to approve
  running the command outside the sandbox.
- Do each step once; skip steps that are already done. Show the user what you changed at the end.

## 1. Check what is already there

```bash
samlgate --version          # already installed?
```

Look at the samlgate and saml2aws config files (INI files; each `[section]` is an account). They are
`~/.samlgate` and `~/.saml2aws` unless the environment variables `SAMLGATE_CONFIG` or `SAML2AWS_CONFIGFILE` point
elsewhere — check those first and read the files they name. samlgate reads an account from
`~/.saml2aws` when `~/.samlgate` does not have it, so a saml2aws user usually needs no configuration. If either file
has an account other than `default`, ask the user which account to use and pass `-a <account>` to every `samlgate`
command below (`status`, `configure`, `login`). Note the account's `aws_profile` (default `saml`) and, if set,
`credentials_file` (default `~/.aws/credentials`, or `AWS_SHARED_CREDENTIALS_FILE` if set), `browser_type` and
`browser_mode`; later steps use them.

```bash
samlgate status [-a <account>]; echo $?    # 0 = that account already has a valid session
```

## 2. Install

| Platform | Command |
|---|---|
| macOS (Apple Silicon) | `brew install figurecurator/tap/samlgate` |
| Windows (x64/ARM64) | `choco install samlgate -y` in an **elevated** PowerShell — ask the user to run it if you cannot elevate. Without Chocolatey: download `samlgate-<version>-win-<arch>.zip` from https://github.com/figurecurator/samlgate/releases, check it against `SHA256SUMS`, and put `samlgate.exe` on `PATH`. |
| Intel Mac / Linux | No packaged build. Tell the user and stop unless they want to build from source (`dotnet publish src/Samlgate -c Release -r <rid>`, .NET 10 SDK). |

Verify the install with `samlgate --version`.

Unless the account uses `browser_type = webview` (macOS native WebView, which needs no other browser), a Chromium
browser is also required: Microsoft Edge, Google Chrome, Brave, Chromium or Whale. `samlgate --version`
does not check for one, so check separately — on macOS look for the app in `/Applications` (for example
`Microsoft Edge.app` or `Google Chrome.app`); on Windows, Edge ships with the OS. If none is installed, ask the user to
install one before continuing.

## 3. Configure the account (skip if step 1 found a usable account)

samlgate needs the IdP-initiated sign-in URL of the AWS app. Ask the user for it. For Microsoft Entra ID it looks like

```text
https://launcher.myapps.microsoft.com/api/signin/<app-id>?tenantId=<tenant-id>
```

and the user can copy it by right-clicking the AWS tile in https://myapps.microsoft.com (or an admin finds it under
Enterprise applications → the AWS app → Properties → User access URL). Then:

```bash
samlgate configure [-a <account>] --url "<sign-in URL>"
# optional: --profile <name> (default: saml)  --role <role ARN>  --browser edge|chrome|brave|chromium|whale
```

## 4. Sign in once

Before running it, tell the user: **a browser window will open — sign in, complete MFA, answer "Yes" to "Stay
signed in?", and pick a role in the terminal if asked.** Then run:

```bash
samlgate login --force [-a <account>]
```

Continue only if this exits with code 0. `--force` makes samlgate visit the IdP even if the profile already holds
valid credentials, so this step really tests the configuration.

- If it fails with "Multiple roles are available and none is selected" (your terminal could not answer the role
  prompt), ask the user which role ARN to use and retry once with
  `samlgate login --force [-a <account>] --role <arn>`. If the user does not know the ARN, ask them to run
  `samlgate login --force [-a <account>]` in their own terminal, pick the role there, and tell you whether it
  succeeded (`echo $?` prints 0 on success).
- For any other failure, a timeout or a cancellation, stop and report the error. Do not run the identity check,
  because the profile may still hold older credentials that would hide the failure.

After the login (or the retry) exits with code 0, run:

```bash
aws sts get-caller-identity --profile <profile>
```

If the account sets `credentials_file`, run the AWS CLI with
`AWS_SHARED_CREDENTIALS_FILE=<that path>` (or use `samlgate exec [-a <account>] -- aws sts get-caller-identity`).
Show the returned account and role to the user and confirm it is the one they expected.

Whether later renewals can skip the window depends on the effective `browser_mode`. An explicit `auto` tries the
stored session without a window; an explicit `interactive`, or `browser_type = webview`, always opens one. When
`browser_mode` is unset, the effective mode is **unknown**: released packages up to 0.2.2 use `interactive`, newer
builds use `auto` for Chromium, and the version number alone does not tell them apart. Do not tell the user renewals
will be windowless in that case. With `auto`, a stored IdP session is reused without a window (about 3 seconds) while
the IdP allows it, and the window opens otherwise. If the user wants windowless renewals and the account uses a
Chromium browser, offer to run `samlgate configure [-a <account>] --browser-mode auto` (after they agree) so the mode
is explicit. Older releases lack this option: check that `samlgate configure --help` lists `--browser-mode` first,
and if it does not, offer to upgrade samlgate (`brew upgrade samlgate` or `choco upgrade samlgate -y`) instead.

## 5. Remember how to use it

Add the section below to your persistent instructions so future sessions use samlgate. Ask the user first and
show them the text.

- Claude Code: `~/.claude/CLAUDE.md`
- Codex: `~/.codex/AGENTS.md`
- Other agents: their global instructions file.

Replace `saml` with the configured profile, add `-a <account>` to every command if a non-default account is used,
and mention `AWS_SHARED_CREDENTIALS_FILE=<path>` if the account sets `credentials_file`. Keep the second bullet only
if `browser_mode = auto` is set explicitly; otherwise say that renewal may open a sign-in window. Write it in the user's language if
they prefer.

```markdown
## AWS sign-in (samlgate)

- Get AWS credentials with `samlgate login`, not `saml2aws login` or `aws sso login`. It writes the `[saml]`
  profile in `~/.aws/credentials`; use `aws --profile saml` (or `samlgate exec -- <command>`).
- A session with more than 5 minutes left is reused. Otherwise samlgate first tries the stored browser session
  without a window and opens a sign-in window only if that fails. Tell the user before running it, because they
  may have to complete the sign-in. `--force` refreshes early; add `--role <arn>` to switch roles.
- `samlgate status` exits 0 when the session is valid. When an AWS call fails with `ExpiredToken` or similar,
  check `samlgate status`, then run `samlgate login`.
```

## 6. Report

Tell the user the installed version, the account and profile, the role from `aws sts get-caller-identity`, and the
file you added the instructions to.
