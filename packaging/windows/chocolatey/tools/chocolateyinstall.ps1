$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Official GitHub release archives. Each ZIP contains samlgate.exe, LICENSE and README.md.
# Chocolatey shims samlgate.exe from the package folder and removes the shim on uninstall.
$x64Url = 'https://github.com/figurecurator/samlgate/releases/download/v0.2.1/samlgate-v0.2.1-win-x64.zip'
$x64Checksum = '39f08a62a4871d5c6abb14d91ee78535383d281d8acd2167425f9c681ea4cef7'
$arm64Url = 'https://github.com/figurecurator/samlgate/releases/download/v0.2.1/samlgate-v0.2.1-win-arm64.zip'
$arm64Checksum = '64bbc9d45b0e7a97dde471eaf406734a4c9da96c2493fffd2d4546e17c3ff66e'

if ($env:chocolateyForceX86 -and $env:chocolateyForceX86 -ne 'false') {
  throw 'samlgate has no 32-bit x86 build; install without --forcex86.'
}

# Native machine type from IsWow64Process2 (Windows 10 1709+). An x64-emulated host on
# ARM64 reports PROCESSOR_ARCHITECTURE=AMD64 without PROCESSOR_ARCHITEW6432, so the
# environment variables alone are not enough there.
function Get-NativeMachine {
  try {
    if (-not ('Samlgate.NativeMachine' -as [type])) {
      Add-Type -Namespace Samlgate -Name NativeMachine -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
[DllImport("kernel32.dll")]
public static extern IntPtr GetCurrentProcess();
'@
    }
    [uint16] $processMachine = 0
    [uint16] $nativeMachine = 0
    if ([Samlgate.NativeMachine]::IsWow64Process2([Samlgate.NativeMachine]::GetCurrentProcess(), [ref] $processMachine, [ref] $nativeMachine)) {
      return $nativeMachine
    }
  } catch {
    Write-Debug "IsWow64Process2 unavailable: $_"
  }
  return $null
}

$nativeMachine = Get-NativeMachine
$isArm64 = $nativeMachine -eq 0xAA64 -or
  $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64'
$isX64 = $nativeMachine -eq 0x8664 -or ($null -eq $nativeMachine -and [Environment]::Is64BitOperatingSystem)
if (-not $isArm64 -and -not $isX64) {
  throw 'samlgate requires 64-bit Windows (x64 or ARM64).'
}

# Pass the selected archive as Url, not Url64bit: Chocolatey reports ARM64 as 32-bit
# (Get-OSArchitectureWidth), so it would ignore Url64bit there. With no Url64bit, the
# same Url is used on x64 as well.
$packageArgs = @{
  PackageName   = $env:ChocolateyPackageName
  UnzipLocation = $toolsDir
  Url           = $(if ($isArm64) { $arm64Url } else { $x64Url })
  Checksum      = $(if ($isArm64) { $arm64Checksum } else { $x64Checksum })
  ChecksumType  = 'sha256'
}

Install-ChocolateyZipPackage @packageArgs
