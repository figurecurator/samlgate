function Assert-WinGetManifest {
    param(
        [Parameter(Mandatory)][string] $InstallerManifest,
        [Parameter(Mandatory)] $ScoopManifest
    )

    # Parse the generated manifest's flat installer mappings, consuming every list item.
    # Reject unsupported YAML layouts instead of silently skipping entries. No YAML module required.
    $entries = [Collections.Generic.List[hashtable]]::new()
    $inInstallers = $false
    $sections = 0
    $current = $null
    foreach ($line in ($InstallerManifest -split '\r?\n')) {
        if ($line -cmatch '^Installers:\s*$') {
            $sections++
            $inInstallers = $true
            $current = $null
            continue
        }
        if (-not $inInstallers -or $line -match '^\s*(#.*)?$') { continue }
        if ($line -cmatch '^[A-Za-z][A-Za-z0-9]*:') {
            $inInstallers = $false
            continue
        }
        if ($line -cmatch '^  - (?<key>[A-Za-z][A-Za-z0-9]*):[ \t]+(?<value>\S+)[ \t]*$') {
            $current = @{}
            $entries.Add($current)
        } elseif ($line -cmatch '^    (?<key>[A-Za-z][A-Za-z0-9]*):[ \t]+(?<value>\S+)[ \t]*$' -and $null -ne $current) {
            # Additional scalar fields (e.g. InstallerType) and field reordering are supported.
        } else {
            throw "Unsupported WinGet installer layout: $line"
        }
        $key = $Matches['key']
        $value = $Matches['value']
        if ($current.ContainsKey($key)) { throw "Duplicate WinGet installer field: $key" }
        $current[$key] = $value
    }

    if ($sections -ne 1 -or $entries.Count -ne 2) {
        throw 'WinGet manifest must contain exactly one Installers list with two entries'
    }
    $architectures = @{ x64 = '64bit'; arm64 = 'arm64' }
    $seen = @{}
    foreach ($entry in $entries) {
        $architecture = $entry['Architecture']
        if ($architecture -cnotin @('x64', 'arm64') -or $seen.ContainsKey($architecture)) {
            throw "Unexpected or duplicate WinGet architecture: $architecture"
        }
        $seen[$architecture] = $true
        $expected = $ScoopManifest.architecture.($architectures[$architecture])
        if ($null -eq $expected -or
            $entry['InstallerUrl'] -cne $expected.url -or
            $entry['InstallerSha256'] -notmatch '^[A-Fa-f0-9]{64}$' -or
            $entry['InstallerSha256'] -ine $expected.hash) {
            throw "WinGet URL/checksum mismatch: $architecture"
        }
    }
}

function Assert-ChocolateyPackage {
    param(
        [Parameter(Mandatory)][string] $InstallScript,
        [Parameter(Mandatory)][string] $Nuspec,
        [Parameter(Mandatory)] $ScoopManifest
    )

    # The Chocolatey install script must download the same official archives as the other manifests.
    $architectures = @{ x64 = '64bit'; arm64 = 'arm64' }
    foreach ($architecture in $architectures.Keys) {
        $expected = $ScoopManifest.architecture.($architectures[$architecture])
        $url = [regex]::Matches($InstallScript, "(?m)^\`$${architecture}Url = '([^']+)'\s*$")
        $hash = [regex]::Matches($InstallScript, "(?m)^\`$${architecture}Checksum = '([^']+)'\s*$")
        if ($url.Count -ne 1 -or $hash.Count -ne 1 -or
            $url[0].Groups[1].Value -cne $expected.url -or
            $hash[0].Groups[1].Value -notmatch '^[A-Fa-f0-9]{64}$' -or
            $hash[0].Groups[1].Value -ine $expected.hash) {
            throw "Chocolatey URL/checksum mismatch: $architecture"
        }
    }
    $version = ([xml] $Nuspec).package.metadata.version
    if ($version -cne $ScoopManifest.version) { throw "Chocolatey version mismatch: $version" }
}
