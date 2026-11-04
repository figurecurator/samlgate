# Homebrew packaging

There are two separate tracks. They share nothing but the formula name.

| | Tap (today) | homebrew-core (long term) |
|---|---|---|
| Install | `brew install figurecurator/tap/samlgate` | `brew install samlgate` |
| Formula | [figurecurator/homebrew-tap](https://github.com/figurecurator/homebrew-tap) `Formula/samlgate.rb` | Draft: [`core/samlgate.rb`](core/samlgate.rb) |
| What it installs | The release `osx-arm64` tarball: NativeAOT `samlgate` + `samlgate-webview` | Built from the tag's source tarball with Homebrew's `dotnet` |
| Runtime | None | Homebrew `dotnet` (framework-dependent, single-file apphost) |
| Architectures | Apple Silicon only | Whatever Homebrew bottles (arm64; x86_64 while Homebrew still builds it) |

homebrew-core does not accept prebuilt binaries for open-source projects, so the core formula must build from
source. Other .NET formulae there (cyclonedx-cli, dug, gitversion, kiota) all use framework-dependent publishing
instead of NativeAOT, which is what the draft does.

## Tap: release checklist

1. Tag `vX.Y.Z`; the Release workflow uploads `samlgate-vX.Y.Z-osx-arm64.tar.gz` and `SHA256SUMS`.
2. In the tap, update `url` and `sha256` (from `SHA256SUMS`).
3. `brew audit --strict --online figurecurator/tap/samlgate` and `brew test samlgate` after `brew upgrade`.

The tap's `test` only checks `--version`. The `test` block of the core draft works for the tap as well (it
only needs `libexec` replaced with `bin` for the helper check) and exercises `configure` and `status` offline.

## homebrew-core: status of the draft

The `fetch` method restores NuGet dependencies during Homebrew's network-enabled fetch phase.
Homebrew then runs `install` in the same build directory without network access; the draft uses
`publish --no-restore` there. `deny_network_access!` does not prevent the fetch hook from downloading dependencies.
See the [Formula fetch API](https://docs.brew.sh/rubydoc/Formula.html#fetch-instance_method) and
[Homebrew's cyclonedx-cli formula](https://github.com/Homebrew/homebrew-core/blob/main/Formula/c/cyclonedx-cli.rb)
for this pattern. Full sandboxed installation remains unverified as noted below.

Verified locally (macOS 27.2, Apple M4 Pro, Homebrew 7.0.12, Homebrew `dotnet` 10.0.403):

- `dotnet restore` (the formula's `fetch`) followed by `dotnet publish --no-restore` from a clean copy of the source
  tree: builds in a few seconds; `samlgate` is a 1.6 MB arm64 apphost, `samlgate-webview` is universal.
- The apphost runs through a symlink with `AppHostRelativeDotNet` and no `DOTNET_ROOT`.
- Every command in the `test` block, with config, data directory and credentials file inside a temporary folder.
- `brew style`: no offenses. `brew audit --new --strict --online` (from a throwaway local tap): only problem is the
  placeholder `v0.3.0` URL, which does not exist yet.

Not verified: `brew install --build-from-source` of the draft (it would replace the tap install), building inside
Homebrew's sandbox, Intel bottles.

### Before submitting

- **Notability.** homebrew-core rejects GitHub projects below roughly 30 forks, 30 watchers or 75 stars (higher for
  self-submissions). As of 2026-11-04 the repository has 0 of each. This is the real blocker.
- **A release containing the universal helper fix** (anything after 0.2.0), then fill in `url` and `sha256`.
- **Linux.** Core prefers formulae that build on Linux too. The Chromium backend may work there, but upstream does
  not support Linux yet, so the draft is `depends_on :macos`.
- **Do not run `login` or `credential-process` in `test`.** Both open a real browser (also without a TTY).
- `SAMLGATE_DATA_DIR` must be set in `test`: the default data directory comes from the OS, not `HOME`.

## Notes found while checking this

- A NativeAOT build made with Homebrew's `dotnet` links Homebrew's `openssl@3` and `brotli` dylibs when they are on
  `LIBRARY_PATH` (a local workaround to make that link succeed). The release workflow uses `actions/setup-dotnet`
  and its binaries do not depend on them, so release builds should keep using Microsoft's SDK. The
  framework-dependent build for core does not link them either.
- Before the universal helper change, `-r osx-x64` on Apple Silicon produced an x86_64 `samlgate` next to an
  arm64 `samlgate-webview`: `Samlgate.Core` builds without the RID, and the helper's output path did not include an
  architecture, so incremental builds reused the stale file.
