[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ReleaseDirectory,
    [Parameter(Mandatory)] [string]$Version,
    [string]$InstallerScriptPath,
    [switch]$CheckInstalled,
    [switch]$RequireInstalled
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw "Release validation failed: $Message"
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-(?:alpha|beta|rc|dev)\.[1-9]\d*)?$') {
    Fail "'$Version' is not a supported DualLink semantic version."
}

if (-not (Test-Path -LiteralPath $ReleaseDirectory -PathType Container)) {
    Fail "release directory does not exist: $ReleaseDirectory"
}

$expectedInstaller = "DualLink-$Version-Setup-x64.exe"
$expectedNames = @($expectedInstaller, 'SHA256SUMS.txt', 'DualLink.spdx.json')
$entries = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Force)
if ($entries.Count -ne 3) {
    Fail "dist\release must contain exactly three project-owned files; found $($entries.Count)."
}
if ($entries | Where-Object { $_.PSIsContainer }) {
    Fail 'dist\release must not contain subdirectories.'
}
$actualNames = @($entries | ForEach-Object Name | Sort-Object)
$sortedExpected = @($expectedNames | Sort-Object)
if (($actualNames -join "`n") -cne ($sortedExpected -join "`n")) {
    Fail "expected assets [$($sortedExpected -join ', ')], found [$($actualNames -join ', ')]."
}

$checksumsPath = Join-Path $ReleaseDirectory 'SHA256SUMS.txt'
$checksumEntries = @{}
foreach ($line in Get-Content -LiteralPath $checksumsPath) {
    if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64})\s{2}(?<name>.+)$') {
        Fail "checksum manifest contains an invalid line."
    }
    $name = $Matches.name.Trim().TrimStart('*')
    if ($checksumEntries.ContainsKey($name)) {
        Fail "checksum manifest contains duplicate entry '$name'."
    }
    $checksumEntries[$name] = $Matches.hash.ToLowerInvariant()
}
if ($checksumEntries.Count -ne 2 -or
    -not $checksumEntries.ContainsKey($expectedInstaller) -or
    -not $checksumEntries.ContainsKey('DualLink.spdx.json')) {
    Fail 'checksum manifest must contain only the installer and SBOM entries.'
}

foreach ($name in @($expectedInstaller, 'DualLink.spdx.json')) {
    $path = Join-Path $ReleaseDirectory $name
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $checksumEntries[$name]) {
        Fail "SHA-256 mismatch for '$name'."
    }
}

$sbomPath = Join-Path $ReleaseDirectory 'DualLink.spdx.json'
$sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
if ($sbom.spdxVersion -ne 'SPDX-2.3' -or $sbom.name -ne "DualLink-$Version") {
    Fail 'SBOM identity does not match the packaged version.'
}
if (-not @($sbom.packages | Where-Object { $_.name -eq 'DualLink.Service' })) {
    Fail 'SBOM does not include the privileged DualLink.Service payload.'
}

if ($InstallerScriptPath) {
    if (-not (Test-Path -LiteralPath $InstallerScriptPath -PathType Leaf)) {
        Fail "installer script does not exist: $InstallerScriptPath"
    }
    $iss = Get-Content -LiteralPath $InstallerScriptPath -Raw
    $requiredSnippets = @(
        'AppId={{85739A0C-EE5E-4BA4-AB4D-126921C1B31E}',
        'UsePreviousAppDir=yes',
        'UsePreviousGroup=yes',
        'UsePreviousTasks=yes',
        "MaintenancePage.Add('Update or reinstall",
        "MaintenancePage.Add('Repair the current installation')",
        "MaintenancePage.Add('Uninstall DualLink')",
        'Source: "..\dist\publish\DualLink.exe"',
        'Source: "..\dist\publish\DualLink.Service.exe"',
        'Source: "..\dist\publish\DualLink.Watchdog.exe"',
        'Description: "Open DualLink now',
        'runascurrentuser'
    )
    foreach ($snippet in $requiredSnippets) {
        if (-not $iss.Contains($snippet, [StringComparison]::Ordinal)) {
            Fail "installer script is missing the required maintenance or payload entry: $snippet"
        }
    }
    Write-Host 'Installer script: update, repair, uninstall, path preservation, and payload checks passed.'
}

Write-Host "Public asset set: exactly 3 files for DualLink $Version."
Write-Host 'Checksum set: installer and SBOM verified.'

if ($CheckInstalled -or $RequireInstalled) {
    $uninstallSubkey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{85739A0C-EE5E-4BA4-AB4D-126921C1B31E}_is1'
    $registry = $null
    $registryPath = $null
    foreach ($candidate in @(
        "HKLM:\$uninstallSubkey",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{85739A0C-EE5E-4BA4-AB4D-126921C1B31E}_is1"
    )) {
        $registry = Get-ItemProperty -LiteralPath $candidate -ErrorAction SilentlyContinue
        if ($registry) {
            $registryPath = $candidate
            break
        }
    }

    if (-not $registry) {
        if ($RequireInstalled) { Fail 'the DualLink uninstall registration was not found.' }
        Write-Warning 'Installed-copy checks skipped: DualLink is not registered on this machine.'
    } else {
        $installRoot = [string]$registry.InstallLocation
        if ([string]::IsNullOrWhiteSpace($installRoot)) { Fail 'installed registration has no InstallLocation.' }
        $requiredInstalledFiles = @('DualLink.exe', 'DualLink.Service.exe', 'DualLink.Watchdog.exe', 'unins000.exe')
        foreach ($file in $requiredInstalledFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $installRoot $file) -PathType Leaf)) {
                Fail "installed copy is missing '$file'."
            }
        }
        if ($registry.DisplayVersion -and $registry.DisplayVersion -ne $Version) {
            Write-Warning "installed version is '$($registry.DisplayVersion)' while the package under test is '$Version'."
        }
        Write-Host "Installed registration: $registryPath"
        Write-Host "Update: ready to reuse $installRoot"
        Write-Host 'Repair: setup can rewrite the controller, privileged service, and watchdog payloads in the registered location.'
        Write-Host 'Uninstall: registered uninstaller is present; no uninstall action was started.'
    }
}

Write-Host 'Release validation passed without starting setup, changing the registry, or requesting elevation.'
