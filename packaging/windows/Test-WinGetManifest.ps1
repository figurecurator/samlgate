$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Assert-WinGetManifest.ps1')
$manifest = Get-Content (Join-Path $PSScriptRoot 'winget/figurecurator.samlgate.installer.yaml') -Raw
$scoop = Get-Content (Join-Path $PSScriptRoot 'scoop/samlgate.json') -Raw | ConvertFrom-Json
$passed = 0

function Assert-ManifestCase([string] $Name, [string] $Text, [bool] $ShouldPass) {
    $accepted = $true
    try { Assert-WinGetManifest -InstallerManifest $Text -ScoopManifest $scoop }
    catch { $accepted = $false }
    if ($accepted -ne $ShouldPass) { throw "Unexpected validation result: $Name (accepted=$accepted)" }
    Write-Output "PASS $Name"
    $script:passed++
}

Assert-ManifestCase 'current manifest' $manifest $true
Assert-ManifestCase 'CRLF' ($manifest -replace '\r?\n', "`r`n") $true
Assert-ManifestCase 'no final newline' $manifest.TrimEnd() $true
Assert-ManifestCase 'intervening InstallerType' ($manifest.Replace('  - Architecture: x64', "  - Architecture: x64`n    InstallerType: zip")) $true
Assert-ManifestCase 'field order' ($manifest.Replace('  - Architecture: x64', "  - InstallerType: zip`n    Architecture: x64")) $true
Assert-ManifestCase 'comments and blank lines' ($manifest.Replace('  - Architecture: x64', "`n  # x64 installer`n  - Architecture: x64")) $true
Assert-ManifestCase 'lowercase hash' ($manifest.Replace($scoop.architecture.'64bit'.hash.ToUpperInvariant(), $scoop.architecture.'64bit'.hash.ToLowerInvariant())) $true

# This third entry was invisible to the old three-consecutive-lines regex.
$extra = @"
  - Architecture: x86
    InstallerType: zip
    InstallerUrl: https://example.invalid/unchecked.zip
    InstallerSha256: $('0' * 64)
"@
Assert-ManifestCase 'extra entry with intervening field' ($manifest.Replace('ManifestType: installer', "$extra`nManifestType: installer")) $false
Assert-ManifestCase 'extra duplicate entry' ($manifest.Replace('ManifestType: installer', "$($extra.Replace('Architecture: x86', 'Architecture: x64'))`nManifestType: installer")) $false
Assert-ManifestCase 'unknown architecture' ($manifest.Replace('Architecture: x64', 'Architecture: x86')) $false
Assert-ManifestCase 'missing architecture' ($manifest.Replace('  - Architecture: x64', '  - InstallerType: zip')) $false
Assert-ManifestCase 'architecture case mismatch' ($manifest.Replace('Architecture: x64', 'Architecture: X64')) $false
Assert-ManifestCase 'duplicate architecture' ($manifest.Replace('Architecture: arm64', 'Architecture: x64')) $false
Assert-ManifestCase 'missing entry' ($manifest -replace '(?m)^  - Architecture: arm64\r?\n    InstallerUrl: [^\r\n]+\r?\n    InstallerSha256: [^\r\n]+\r?\n', '') $false
Assert-ManifestCase 'missing Installers list' ($manifest.Replace('Installers:', 'OtherList:')) $false
Assert-ManifestCase 'duplicate Installers list' ($manifest + "`nInstallers:`n") $false
Assert-ManifestCase 'duplicate field' ($manifest.Replace('  - Architecture: x64', "  - Architecture: x64`n    Architecture: x64")) $false
Assert-ManifestCase 'missing hash' ($manifest -replace '(?m)^    InstallerSha256: [^\r\n]+\r?\n', '') $false
Assert-ManifestCase 'malformed hash' ($manifest.Replace($scoop.architecture.'64bit'.hash.ToUpperInvariant(), 'invalid')) $false
Assert-ManifestCase 'wrong URL' ($manifest.Replace($scoop.architecture.'64bit'.url, 'https://example.invalid/wrong.zip')) $false

$x64Hash = $scoop.architecture.'64bit'.hash.ToUpperInvariant()
$arm64Hash = $scoop.architecture.arm64.hash.ToUpperInvariant()
$swapped = $manifest.Replace($x64Hash, 'HASH_SWAP').Replace($arm64Hash, $x64Hash).Replace('HASH_SWAP', $arm64Hash)
Assert-ManifestCase 'swapped hashes' $swapped $false
$swapped = $manifest.Replace($scoop.architecture.'64bit'.url, 'URL_SWAP').Replace($scoop.architecture.arm64.url, $scoop.architecture.'64bit'.url).Replace('URL_SWAP', $scoop.architecture.arm64.url)
Assert-ManifestCase 'swapped URLs' $swapped $false
Assert-ManifestCase 'unsupported nested field' ($manifest.Replace('  - Architecture: x64', "  - Architecture: x64`n    InstallerSwitches:`n      Silent: /quiet")) $false
Write-Output "$passed manifest validation cases passed"
