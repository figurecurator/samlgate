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
samlgate status; echo $?    # 0 = a valid session exists for the default account
```

Also note whether `~/.samlgate` or `~/.saml2aws` exists. samlgate reads an account from `~/.saml2aws` when
`~/.samlgate` does not have it, so a saml2aws user usually needs no configuration.

## 2. Install

| Platform | Command |
|---|---|
| macOS (Apple Silicon) | `brew install figurecurator/tap/samlgate` |
| Windows (x64/ARM64) | `choco install samlgate -y` in an **elevated** PowerShell — ask the user to run it if you cannot elevate. Without Chocolatey: download `samlgate-<version>-win-<arch>.zip` from https://github.com/figurecurator/samlgate/releases, check it against `SHA256SUMS`, and put `samlgate.exe` on `PATH`. |
| Intel Mac / Linux | No packaged build. Tell the user and stop unless they want to build from source (`dotnet publish src/Samlgate -c Release -r <rid>`, .NET 10 SDK). |

A Chromium browser is required: Microsoft Edge, Google Chrome, Brave, Chromium or Whale. Verify with
`samlgate --version`.

## 3. Configure the account (skip if step 1 found a usable account)

samlgate needs the IdP-initiated sign-in URL of the AWS app. Ask the user for it. For Microsoft Entra ID it looks like

```text
https://launcher.myapps.microsoft.com/api/signin/<app-id>?tenantId=<tenant-id>
```

and the user can copy it by right-clicking the AWS tile in https://myapps.microsoft.com (or an admin finds it under
Enterprise applications → the AWS app → Properties → User access URL). Then:

```bash
samlgate configure --url "<sign-in URL>"
# optional: --profile <name> (default: saml)  --role <role ARN>  --browser edge|chrome|brave|chromium|whale
```

## 4. Sign in once

Before running it, tell the user: **a browser window will open — sign in, complete MFA, answer "Yes" to "Stay
signed in?", and pick a role in the terminal if asked.** Then run:

```bash
samlgate login
aws sts get-caller-identity --profile saml   # use the configured profile name
```

If several roles exist and your terminal cannot answer the role prompt, ask the user which role ARN to use and run
`samlgate login --force --role <arn>`.

Later logins reuse the stored browser session without a window (about 3 seconds) as long as the IdP allows it;
otherwise the window opens again.

## 5. Remember how to use it

Add the section below to your persistent instructions so future sessions use samlgate. Ask the user first and
show them the text.

- Claude Code: `~/.claude/CLAUDE.md`
- Codex: `~/.codex/AGENTS.md`
- Other agents: their global instructions file.

Replace `saml` with the configured profile and add `-a <account>` if a non-default account was configured. Write it
in the user's language if they prefer.

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
