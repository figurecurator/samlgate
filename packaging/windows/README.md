# Windows packaging

These manifests describe the **v0.2.1** release. Package IDs follow the publisher, Rainbowsoft (contact@rainbowsoft.co.kr), not the GitHub organization name: WinGet `Rainbowsoft.samlgate`, Chocolatey `samlgate`. Download URLs still point at `github.com/figurecurator/samlgate`.

Until a submission is accepted, do not advertise its install command as available.

## Contents

- `winget/`: multi-file WinGet manifest for `Rainbowsoft.samlgate`, ZIP with a portable executable, x64 and ARM64.
- `chocolatey/`: Chocolatey package `samlgate`. The install script downloads the official release ZIP (ARM64 on ARM64 Windows, x64 otherwise) and verifies its SHA256; Chocolatey shims `samlgate.exe` and removes the shim on uninstall. No uninstall script and no application-state deletion.
- `scoop/samlgate.json`: candidate for a self-hosted bucket, x64 and ARM64. No custom installation scripts or application-state deletion hooks.
- `Test-WinGetManifest.ps1`: standalone regression checks that the WinGet and Chocolatey files reference the same URLs, hashes and version as the Scoop manifest.
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

### Local install tests

```powershell
# WinGet (enabling local manifests needs an elevated shell once)
winget settings --enable LocalManifestFiles
winget install --manifest .\packaging\windows\winget
samlgate --version
winget uninstall Rainbowsoft.samlgate

# Chocolatey (elevated shell)
choco pack .\packaging\windows\chocolatey\samlgate.nuspec --outputdirectory ..\choco-out
choco install samlgate --source ..\choco-out -y
samlgate --version
choco uninstall samlgate -y
```

## Publishing

1. Release first: tag `vX.Y.Z` on `main`; the release workflow publishes the ZIPs and `SHA256SUMS`.
2. Update every version, URL and hash in `winget/`, `chocolatey/` and `scoop/` from that release's `SHA256SUMS`. Never substitute local build hashes.
3. Run the validation and local install tests above.
4. **WinGet:** submit the three YAMLs as `manifests/r/Rainbowsoft/samlgate/<version>/` in `microsoft/winget-pkgs`, for example with `wingetcreate submit --token <PAT> .\packaging\windows\winget`. Later versions: `wingetcreate update Rainbowsoft.samlgate --version <v> --urls <x64-zip> <arm64-zip> --submit`.
5. **Chocolatey:** `choco push samlgate.<version>.nupkg --source https://push.chocolatey.org/ --api-key <key>` from the Chocolatey account that owns the package. The first version goes through human moderation.
6. Test on an actual Windows ARM64 machine before claiming runtime or login validation there.

Use a patched .NET NativeAOT toolchain for builds (runtime 10.0.14 or later); an older local SDK can emit NU1903 for ILCompiler 10.0.10.

References: [WinGet manifests](https://learn.microsoft.com/en-us/windows/package-manager/package/manifest), [Chocolatey package creation](https://docs.chocolatey.org/en-us/create/create-packages/), [Scoop manifests](https://github.com/ScoopInstaller/Scoop/wiki/App-Manifests), [specific .NET advisory](https://github.com/advisories/GHSA-p3jp-9v83-76w9).
