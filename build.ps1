[CmdletBinding()]
param(
    [switch]$SkipInstaller,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
$props = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)
$displayVersion = [string]$props.Project.PropertyGroup.InformationalVersion
if ([string]::IsNullOrWhiteSpace($displayVersion)) { $displayVersion = $version }
$appProject = Join-Path $repoRoot 'src\DualLink\DualLink.csproj'
$testProject = Join-Path $repoRoot 'tests\DualLink.Tests\DualLink.Tests.csproj'
$publishDirectory = Join-Path $repoRoot 'dist\publish'
$releaseDirectory = Join-Path $repoRoot 'dist\release'
$watchdogProject = Join-Path $repoRoot 'src\DualLink.Watchdog\DualLink.Watchdog.csproj'
$watchdogPublishDirectory = Join-Path $repoRoot 'dist\watchdog'
$iconPath = Join-Path $repoRoot 'assets\DualLink.ico'
$iconPreviewPath = Join-Path $repoRoot 'docs\images\icon-preview.png'

dotnet restore $appProject --configfile (Join-Path $repoRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'App restore failed.' }
dotnet run --project (Join-Path $repoRoot 'tools\IconMaker\IconMaker.csproj') -c Release -- $iconPath $iconPreviewPath
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
if (-not $SkipTests) {
    dotnet run --project $testProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
} else {
    Write-Warning 'Integration tests were skipped. This output is not eligible for a stable GitHub Release.'
}
dotnet publish $appProject -c Release -p:PublishProfile=win-x64 -p:Version=$version --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
dotnet publish $watchdogProject -c Release --output $watchdogPublishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Watchdog publish failed.' }
Copy-Item -LiteralPath (Join-Path $watchdogPublishDirectory 'DualLink.Watchdog.exe') -Destination (Join-Path $publishDirectory 'DualLink.Watchdog.exe') -Force

$sbomPath = Join-Path $repoRoot 'dist\DualLink.spdx.json'
& (Join-Path $repoRoot 'tools\Generate-Sbom.ps1') -Version $displayVersion -ApplicationPath (Join-Path $publishDirectory 'DualLink.exe') -OutputPath $sbomPath

if (-not $SkipInstaller) {
    $compiler = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $compiler) { throw 'Inno Setup 6 was not found.' }
    & $compiler "/DAppVersion=$displayVersion" "/DNumericVersion=$version" (Join-Path $repoRoot 'installer\DualLink.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

    $checksumFiles = @(
        (Join-Path $repoRoot "dist\DualLink-$displayVersion-Setup-x64.exe"),
        $sbomPath
    )
    $checksumLines = foreach ($file in $checksumFiles) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path -Leaf $file)"
    }
    $checksumsPath = Join-Path $repoRoot 'dist\SHA256SUMS.txt'
    $checksumLines | Set-Content -LiteralPath $checksumsPath -Encoding ascii

    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $releaseDirectory -File | Remove-Item -Force
    Copy-Item -LiteralPath $checksumFiles[0] -Destination $releaseDirectory -Force
    Copy-Item -LiteralPath $sbomPath -Destination $releaseDirectory -Force
    Copy-Item -LiteralPath $checksumsPath -Destination $releaseDirectory -Force
}

Write-Host "DualLink $displayVersion build complete: $repoRoot\dist"
if (-not $SkipInstaller) { Write-Host "Public release files (exactly 3): $releaseDirectory" }
