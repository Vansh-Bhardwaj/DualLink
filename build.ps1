[CmdletBinding()]
param([switch]$SkipInstaller)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
$appProject = Join-Path $repoRoot 'src\DualLink\DualLink.csproj'
$testProject = Join-Path $repoRoot 'tests\DualLink.Tests\DualLink.Tests.csproj'
$publishDirectory = Join-Path $repoRoot 'dist\publish'
$iconPath = Join-Path $repoRoot 'assets\DualLink.ico'

dotnet restore $appProject --configfile (Join-Path $repoRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'App restore failed.' }
dotnet run --project (Join-Path $repoRoot 'tools\IconMaker\IconMaker.csproj') -c Release -- $iconPath
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
dotnet run --project $testProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
dotnet publish $appProject -c Release -p:PublishProfile=win-x64 -p:Version=$version --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

if (-not $SkipInstaller) {
    $compiler = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $compiler) { throw 'Inno Setup 6 was not found.' }
    & $compiler "/DAppVersion=$version" (Join-Path $repoRoot 'installer\DualLink.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}

Write-Host "DualLink $version build complete: $repoRoot\dist"
