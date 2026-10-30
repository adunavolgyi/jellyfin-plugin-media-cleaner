param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsPath,
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [Parameter(Mandatory = $true)]
    [string]$Repository,
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [string]$Changelog = "See https://github.com/$Repository/releases"
)

$ErrorActionPreference = "Stop"

# Merges this release's per-profile entries into the Jellyfin repository manifest, keeping older versions.
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$entries = @(Get-ChildItem -Path $AssetsPath -Filter "manifest-entry-*.json" | ForEach-Object { Get-Content -Raw $_.FullName | ConvertFrom-Json })
if ($entries.Count -eq 0) {
    throw "No manifest entries found in $AssetsPath."
}

$newVersions = foreach ($entry in $entries) {
    $zip = Join-Path $AssetsPath "MediaCleaner-$($entry.profile).zip"
    [pscustomobject]@{
        version = $entry.version
        changelog = $Changelog
        targetAbi = $entry.targetAbi
        sourceUrl = "https://github.com/$Repository/releases/download/$Tag/MediaCleaner-$($entry.profile).zip"
        checksum = (Get-FileHash -Algorithm MD5 -Path $zip).Hash.ToLowerInvariant()
        timestamp = $timestamp
    }
}

$oldVersions = @()
if (Test-Path $ManifestPath) {
    $oldVersions = @((Get-Content -Raw $ManifestPath | ConvertFrom-Json)[0].versions |
        Where-Object { $newVersions.version -notcontains $_.version })
}

$first = $entries[0]
$manifest = @(
    [pscustomobject]@{
        guid = $first.guid
        name = $first.name
        description = $first.overview
        overview = $first.overview
        owner = $first.owner
        category = $first.category
        versions = @(@($newVersions) + $oldVersions | Sort-Object { [version]$_.version } -Descending)
    }
)

ConvertTo-Json -InputObject $manifest -Depth 5 | Set-Content -Path $ManifestPath -Encoding utf8
Write-Host "Wrote $ManifestPath with $(@($manifest[0].versions).Count) version(s)."
