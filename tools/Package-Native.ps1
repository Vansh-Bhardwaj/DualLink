[CmdletBinding()]
param(
    # Skip the lint and test run (local evaluation only).
    [switch]$SkipTests
)

# Builds DualLink 5 in release mode and packages the offline installer, with checksums, in
# dist\release. It never tags, pushes or publishes anything.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $cargoToml = Get-Content -LiteralPath (Join-Path $repoRoot 'Cargo.toml') -Raw
    if ($cargoToml -notmatch '(?m)^\[workspace\.package\]\r?\nversion = "(?<version>[^"]+)"') {
        throw 'Cargo.toml has no [workspace.package] version.'
    }
    $version = $Matches.version
    $numeric = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
    if (-not $version.StartsWith($numeric)) {
        throw "Cargo.toml says $version but VERSION says $numeric. Run tools\Set-Version.ps1."
    }

    if (-not $SkipTests) {
        cargo clippy --workspace --all-targets --locked -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw 'Lint failed.' }
        cargo test --workspace --locked
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    cargo build --workspace --release --locked
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    $iscc = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found.' }

    $release = Join-Path $repoRoot 'dist\release'
    New-Item -ItemType Directory -Force -Path $release | Out-Null
    Get-ChildItem -LiteralPath $release -File | Remove-Item -Force
    # The notices are installed with DualLink, so they are brought up to date first.
    & (Join-Path $PSScriptRoot 'Write-NativeNotices.ps1') -SbomPath (Join-Path $release 'DualLink.spdx.json') -Version $version
    & $iscc "/DAppVersion=$version" "/DNumericVersion=$numeric" (Join-Path $repoRoot 'installer\DualLink.iss')
    if ($LASTEXITCODE -ne 0) { throw 'The installer could not be built.' }

    $installer = Get-ChildItem -LiteralPath $release -Filter 'DualLink-*-Setup-x64.exe' | Select-Object -First 1
    $sbom = Join-Path $release 'DualLink.spdx.json'
    $sums = foreach ($file in @($installer.FullName, $sbom)) {
        "$((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path -Leaf $file)"
    }
    $sums | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "DualLink $version installer: $($installer.FullName)"
}
finally {
    Pop-Location
}
