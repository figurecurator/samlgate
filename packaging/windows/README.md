# Windows packaging drafts

These manifests describe the existing **v0.2.0** release. They have not been submitted to WinGet or published as a Scoop bucket. Package-manager install commands should not be advertised as available yet.

## Contents

- `winget/`: multi-file WinGet manifest for `figurecurator.samlgate`, ZIP with a portable executable, x64 and ARM64.
- `scoop/samlgate.json`: candidate for a self-hosted bucket, x64 and ARM64. No custom installation scripts or application-state deletion hooks.
- `Test-Release.ps1`: checksum, archive layout and PE architecture checks, plus native-architecture CLI smoke tests with synthetic configuration. No login or STS calls.

## Validation

Download the two official Windows ZIPs and `SHA256SUMS` from the release into a separate directory. From the repository root, using PowerShell 7:

```powershell
# Standalone regression checks; no release artifacts or external modules needed.
.\packaging\windows\Test-WinGetManifest.ps1

winget validate --manifest .\packaging\windows\winget
Get-Content .\packaging\windows\scoop\samlgate.json -Raw |
    Test-Json -SchemaFile "$env:USERPROFILE\scoop\apps\scoop\current\schema.json"
.\packaging\windows\Test-Release.ps1 -ArtifactsDirectory ..\release-artifacts
```

The Scoop schema command requires an existing Scoop installation; adjust the path for other locations. It checks JSON schema, not installation or update behavior.

The installer check consumes every entry in the generated manifest's flat `Installers` list, supports reordered scalar fields such as `InstallerType`, and rejects extra or duplicate architectures and unsupported YAML layouts.

The smoke script leaves extracted binaries and synthetic configuration in a new `smoke-<guid>` subdirectory of the artifact directory. It restores the configuration environment variables it changes and does not modify PATH or real AWS/saml2aws settings. On x64, only the x64 executable runs; ARM64 checks remain static.

Verified on Windows x64: WinGet schema validation, installed Scoop schema validation, both release checksums and archive layouts, PE machine types, and x64 version/help/configure/status. ARM64 execution, package-manager install/uninstall, PATH/shim behavior and Scoop autoupdate are still unverified.

## Before publishing

1. Use `Rainbowsoft` as the publisher display name, matching the source author metadata. Confirm package ID availability and the target self-hosted Scoop bucket.
2. Test package-manager install/uninstall and update behavior. Schema validation alone does not exercise installation. Keep credentials and browser profiles on uninstall.
3. Test on an actual Windows ARM64 machine before claiming runtime or login validation there.
4. For a new release, update every version, URL and hash. These manifests reference unmodified v0.2.0 artifacts; the Edge compatibility-layer relaunch fix in current source needs a new release before distribution. Never substitute local build hashes under the old release version.
5. Use a patched .NET NativeAOT toolchain for new builds. Official v0.2.0 Windows executables contain the patched runtime 10.0.14; an older local SDK can still emit NU1903 for ILCompiler 10.0.10.
6. For WinGet, place the three YAMLs under `manifests/f/figurecurator/samlgate/0.2.0/` in `microsoft/winget-pkgs` for review. For Scoop, put `samlgate.json` at `bucket/samlgate.json` in the chosen bucket and validate its checkver/autoupdate behavior.

References: [WinGet manifests](https://learn.microsoft.com/en-us/windows/package-manager/package/manifest), [Scoop manifests](https://github.com/ScoopInstaller/Scoop/wiki/App-Manifests), [v0.2.0 release build](https://github.com/figurecurator/samlgate/actions/runs/41794784907), [specific .NET advisory](https://github.com/advisories/GHSA-p3jp-9v83-76w9).
