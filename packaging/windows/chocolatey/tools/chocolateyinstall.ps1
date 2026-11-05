$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Official GitHub release archives. Each ZIP contains samlgate.exe, LICENSE and README.md.
# Chocolatey shims samlgate.exe from the package folder and removes the shim on uninstall.
$x64Url = 'https://github.com/figurecurator/samlgate/releases/download/v0.2.1/samlgate-v0.2.1-win-x64.zip'
$x64Checksum = '39f08a62a4871d5c6abb14d91ee78535383d281d8acd2167425f9c681ea4cef7'
$arm64Url = 'https://github.com/figurecurator/samlgate/releases/download/v0.2.1/samlgate-v0.2.1-win-arm64.zip'
$arm64Checksum = '64bbc9d45b0e7a97dde471eaf406734a4c9da96c2493fffd2d4546e17c3ff66e'

$isArm64 = $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64'

$packageArgs = @{
  PackageName    = $env:ChocolateyPackageName
  UnzipLocation  = $toolsDir
  Url64bit       = $(if ($isArm64) { $arm64Url } else { $x64Url })
  Checksum64     = $(if ($isArm64) { $arm64Checksum } else { $x64Checksum })
  ChecksumType64 = 'sha256'
}

Install-ChocolateyZipPackage @packageArgs
