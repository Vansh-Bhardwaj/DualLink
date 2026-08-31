[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string]$Version,
    [ValidateSet('Alpha', 'Beta', 'ReleaseCandidate', 'Development', 'Stable')]
    [string]$Stage = 'Alpha',
    [ValidateRange(1, 9999)] [int]$Number = 1,
    [switch]$EnforceBranch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ($Version -notmatch '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$') {
    throw "Version must be a numeric semantic version such as 4.0.0; found '$Version'."
}
$major = [int]$Matches.major
if ($Stage -eq 'Stable' -and $Number -ne 1) {
    throw 'Stable versions do not have a prerelease number.'
}

function Get-CurrentBranch {
    try {
        $branch = (& git -C $repoRoot branch --show-current 2>$null).Trim()
        if ($LASTEXITCODE -eq 0) { return $branch }
    } catch {
        # Branch checks are optional for source archives.
    }
    return ''
}

if ($EnforceBranch) {
    $branch = Get-CurrentBranch
    if ([string]::IsNullOrWhiteSpace($branch)) {
        throw 'Branch enforcement was requested, but this checkout is detached or Git is unavailable.'
    }
    $branchPattern = switch ($Stage) {
        'Alpha' { "^v$major/alpha(?:[/-].*)?$" }
        'Beta' { "^v$major/beta(?:[/-].*)?$" }
        'ReleaseCandidate' { "^v$major/beta(?:[/-].*)?$" }
        'Development' { "^v$major/(?:alpha|beta|dev)(?:[/-].*)?$" }
        'Stable' { "^(?:main|v$major/stable)$" }
    }
    if ($branch -notmatch $branchPattern) {
        throw "Version '$Version' is on branch '$branch'. Expected a '$Stage' branch matching '$branchPattern'."
    }
}

$informational = switch ($Stage) {
    'Alpha' { "$Version-alpha.$Number" }
    'Beta' { "$Version-beta.$Number" }
    'ReleaseCandidate' { "$Version-rc.$Number" }
    'Development' { "$Version-dev.$Number" }
    'Stable' { $Version }
}

$versionPath = Join-Path $repoRoot 'VERSION'
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw "VERSION was not found: $versionPath" }
if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { throw "Directory.Build.props was not found: $propsPath" }

$propsContent = Get-Content -LiteralPath $propsPath -Raw
$replacements = [ordered]@{
    '(?<=<VersionPrefix>)[^<]+' = $Version
    '(?<=<AssemblyVersion>)[^<]+' = "$Version.0"
    '(?<=<FileVersion>)[^<]+' = "$Version.0"
    '(?<=<InformationalVersion>)[^<]+' = $informational
}
foreach ($entry in $replacements.GetEnumerator()) {
    if (-not [regex]::IsMatch($propsContent, $entry.Key)) {
        throw "Directory.Build.props is missing the expected version element for '$($entry.Key)'."
    }
    $updated = [regex]::Replace($propsContent, $entry.Key, [string]$entry.Value, 1)
    $propsContent = $updated
}

if ($PSCmdlet.ShouldProcess("$versionPath and $propsPath", "set DualLink version to $informational")) {
    "$Version`n" | Set-Content -LiteralPath $versionPath -Encoding utf8
    $propsContent | Set-Content -LiteralPath $propsPath -Encoding utf8
}

Write-Host "DualLink version: $informational"
Write-Host 'Run build.ps1 with the matching -Stage value before packaging.'
