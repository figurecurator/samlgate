[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArtifactsDirectory
)

$ErrorActionPreference = 'Stop'
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$manifest = Get-Content (Join-Path $PSScriptRoot 'scoop/samlgate.json') -Raw | ConvertFrom-Json
$checksums = Get-Content (Join-Path $artifactRoot 'SHA256SUMS')
$runRoot = Join-Path $artifactRoot ('smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null

foreach ($item in @(
    @{ Rid = 'win-x64'; ScoopArchitecture = '64bit'; Machine = 0x8664 },
    @{ Rid = 'win-arm64'; ScoopArchitecture = 'arm64'; Machine = 0xAA64 }
)) {
    $entry = $manifest.architecture.($item.ScoopArchitecture)
    $fileName = [IO.Path]::GetFileName(([uri] $entry.url).AbsolutePath)
    $zipPath = Join-Path $artifactRoot $fileName
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $entry.hash) { throw "Manifest checksum mismatch: $fileName" }
    if ($checksums -notcontains "$hash  $fileName") { throw "Release checksum mismatch: $fileName" }
    $installer = Get-Content (Join-Path $PSScriptRoot 'winget/figurecurator.samlgate.installer.yaml') -Raw
    if (-not $installer.Contains($entry.url) -or -not $installer.Contains($hash.ToUpperInvariant())) {
        throw "WinGet URL/checksum mismatch: $fileName"
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = @($archive.Entries | ForEach-Object FullName | Sort-Object)
        if (($names -join '|') -ne 'LICENSE|README.md|samlgate.exe') {
            throw "Unexpected ZIP layout: $fileName"
        }
    } finally { $archive.Dispose() }

    $destination = Join-Path $runRoot $item.Rid
    Expand-Archive -LiteralPath $zipPath -DestinationPath $destination
    $exe = Join-Path $destination 'samlgate.exe'
    $bytes = [IO.File]::ReadAllBytes($exe)
    if ([BitConverter]::ToUInt16($bytes, 0) -ne 0x5A4D) { throw 'Missing MZ header' }
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550) { throw 'Missing PE signature' }
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
    if ($machine -ne $item.Machine) { throw "Wrong executable architecture: $fileName" }
    Write-Output ("PASS {0}: SHA256, ZIP layout, PE machine 0x{1:X4}, executable {2:N0} bytes" -f $item.Rid, $machine, $bytes.Length)
}

# Execute only the native architecture. No login, STS, credential_process, or exec.
$rid = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
    'X64' { 'win-x64' }
    'Arm64' { 'win-arm64' }
    default { throw 'Runtime smoke test requires Windows x64 or ARM64' }
}
$exe = Join-Path $runRoot "$rid/samlgate.exe"
$variables = @('SAMLGATE_CONFIG', 'SAML2AWS_CONFIGFILE', 'SAMLGATE_DATA_DIR', 'AWS_SHARED_CREDENTIALS_FILE')
$previous = @{}
foreach ($name in $variables) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:SAMLGATE_CONFIG = Join-Path $runRoot 'synthetic-config.ini'
    $env:SAML2AWS_CONFIGFILE = Join-Path $runRoot 'synthetic-saml2aws.ini'
    $env:SAMLGATE_DATA_DIR = Join-Path $runRoot 'synthetic-data'
    $env:AWS_SHARED_CREDENTIALS_FILE = Join-Path $runRoot 'synthetic-credentials'
    $version = & $exe --version
    if ($LASTEXITCODE -ne 0 -or $version.Trim() -ne $manifest.version) { throw 'Version smoke test failed' }
    $helpText = & $exe login --help
    if ($LASTEXITCODE -ne 0 -or ($helpText -join "`n") -notmatch '--browser-mode') { throw 'Help smoke test failed' }
    & $exe configure --idp-account packaging-smoke --url https://example.invalid/saml --browser edge --browser-mode auto
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic configure smoke test failed' }
    & $exe status --idp-account packaging-smoke
    if ($LASTEXITCODE -ne 1) { throw 'Missing credentials should make status exit 1' }
    if (Test-Path -LiteralPath $env:AWS_SHARED_CREDENTIALS_FILE) { throw 'Smoke test unexpectedly wrote credentials' }
    Write-Output "PASS $rid runtime: version, login help, isolated configure, unsigned-in status (expected exit 1)"
} finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
Write-Output "Evidence and synthetic configuration retained at $runRoot"
