[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Alpha', 'Beta', 'ReleaseCandidate', 'Development', 'Stable')]
    [string]$Stage = 'Auto',
    [switch]$EnforceBranch,
    [switch]$SkipInstaller,
    [switch]$SkipTests,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

function Read-TextFile {
    param([Parameter(Mandatory)] [string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file was not found: $Path"
    }
    return (Get-Content -LiteralPath $Path -Raw).Trim()
}

function Assert-File {
    param([Parameter(Mandatory)] [string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release input was not found: $Path"
    }
}

function Read-VersionMetadata {
    $numericVersion = Read-TextFile (Join-Path $repoRoot 'VERSION')
    if ($numericVersion -notmatch '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$') {
        throw "VERSION must be a numeric semantic version such as 4.0.0; found '$numericVersion'."
    }
    $major = [int]$Matches.major
    $minor = [int]$Matches.minor
    $patch = [int]$Matches.patch

    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    Assert-File $propsPath
    $props = [xml](Get-Content -LiteralPath $propsPath -Raw)
    $propertyGroup = $props.Project.PropertyGroup | Select-Object -First 1
    $versionPrefix = [string]$propertyGroup.VersionPrefix
    $assemblyVersion = [string]$propertyGroup.AssemblyVersion
    $fileVersion = [string]$propertyGroup.FileVersion
    $informationalVersion = [string]$propertyGroup.InformationalVersion

    if ($versionPrefix -ne $numericVersion) {
        throw "Directory.Build.props VersionPrefix '$versionPrefix' does not match VERSION '$numericVersion'."
    }
    if ($assemblyVersion -ne "$numericVersion.0" -or $fileVersion -ne "$numericVersion.0") {
        throw "AssemblyVersion and FileVersion must both be '$numericVersion.0'."
    }
    if ($informationalVersion -notmatch '^(?<base>\d+\.\d+\.\d+)(?:-(?<kind>alpha|beta|rc|dev)\.(?<number>[1-9]\d*))?$') {
        throw "InformationalVersion must be stable or a numbered alpha, beta, rc, or dev version; found '$informationalVersion'."
    }
    if ($Matches.base -ne $numericVersion) {
        throw "InformationalVersion '$informationalVersion' does not use VERSION '$numericVersion'."
    }

    $kind = if ($Matches.kind) { $Matches.kind.ToLowerInvariant() } else { 'stable' }
    $number = if ($Matches.number) { [int]$Matches.number } else { 0 }
    [pscustomobject]@{
        Numeric = $numericVersion
        Informational = $informationalVersion
        Major = $major
        Minor = $minor
        Patch = $patch
        Kind = $kind
        Number = $number
    }
}

function Resolve-Stage {
    param([Parameter(Mandatory)] $Metadata)

    $metadataStage = switch ($Metadata.Kind) {
        'alpha' { 'Alpha' }
        'beta' { 'Beta' }
        'rc' { 'ReleaseCandidate' }
        'dev' { 'Development' }
        default { 'Stable' }
    }
    if ($Stage -ne 'Auto' -and $Stage -ne $metadataStage) {
        throw "Requested stage '$Stage' does not match InformationalVersion '$($Metadata.Informational)' (stage '$metadataStage')."
    }
    if ($Stage -eq 'Auto') { return $metadataStage }
    return $Stage
}

function Get-CurrentBranch {
    try {
        $branch = (& git -C $repoRoot branch --show-current 2>$null).Trim()
        if ($LASTEXITCODE -eq 0) { return $branch }
    } catch {
        # Git is optional for detached archive builds. Metadata checks still run.
    }
    return ''
}

function Assert-BranchPolicy {
    param(
        [Parameter(Mandatory)] $Metadata,
        [Parameter(Mandatory)] [string]$ResolvedStage
    )

    $branch = Get-CurrentBranch
    if ([string]::IsNullOrWhiteSpace($branch)) {
        throw 'Branch enforcement was requested, but this checkout is detached or Git is unavailable.'
    }

    $major = $Metadata.Major
    $pattern = switch ($ResolvedStage) {
        'Alpha' { "^v$major/alpha(?:[/-].*)?$" }
        'Beta' { "^v$major/beta(?:[/-].*)?$" }
        'ReleaseCandidate' { "^v$major/beta(?:[/-].*)?$" }
        'Development' { "^v$major/(?:alpha|beta|dev)(?:[/-].*)?$" }
        'Stable' { "^(?:main|v$major/stable)$" }
        default { throw "Unsupported release stage '$ResolvedStage'." }
    }
    if ($branch -notmatch $pattern) {
        throw "Version '$($Metadata.Informational)' is on branch '$branch'. Expected a '$ResolvedStage' branch matching '$pattern'."
    }
    Write-Host "Branch policy: $branch -> $ResolvedStage"
}

function Assert-ReleaseInputs {
    Assert-File (Join-Path $repoRoot 'src\DualLink\DualLink.csproj')
    Assert-File (Join-Path $repoRoot 'src\DualLink.Service\DualLink.Service.csproj')
    Assert-File (Join-Path $repoRoot 'tests\DualLink.Tests\DualLink.Tests.csproj')
    Assert-File (Join-Path $repoRoot 'src\DualLink.Watchdog\DualLink.Watchdog.csproj')
    Assert-File (Join-Path $repoRoot 'assets\DualLink.ico')
    Assert-File (Join-Path $repoRoot 'tools\IconMaker\IconMaker.csproj')
    Assert-File (Join-Path $repoRoot 'tools\Generate-Sbom.ps1')
    Assert-File (Join-Path $repoRoot 'installer\DualLink.iss')
    Assert-File (Join-Path $repoRoot 'INSTALL-NOTICE.txt')
    Assert-File (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md')
    Assert-File (Join-Path $repoRoot 'LICENSE')
    Assert-File (Join-Path $repoRoot 'PRIVACY.md')
    Assert-File (Join-Path $repoRoot 'SECURITY.md')
    Assert-File (Join-Path $repoRoot 'installer\prereqs\VC_redist.x64.exe')
    Assert-File (Join-Path $repoRoot 'installer\prereqs\Windows.Packet.Filter.3.6.2.1.x64.msi')
    Assert-File (Join-Path $repoRoot 'installer\prereqs\ProxiFyre-2.5.0-win-x64.msi')
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$FailureMessage
    )
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw $FailureMessage }
}

$metadata = Read-VersionMetadata
$resolvedStage = Resolve-Stage $metadata
Assert-ReleaseInputs
if ($EnforceBranch) { Assert-BranchPolicy $metadata $resolvedStage }
if ($resolvedStage -eq 'Stable' -and $SkipTests) {
    throw 'Stable packaging cannot skip integration tests.'
}
if ($resolvedStage -eq 'Stable' -and $SkipInstaller) {
    throw 'Stable packaging must produce the complete offline installer.'
}

Write-Host "DualLink $($metadata.Informational) ($resolvedStage)"
if ($ValidateOnly) {
    Write-Host 'Metadata and release inputs are valid. No build, installer, or network operation was started.'
    exit 0
}

$appProject = Join-Path $repoRoot 'src\DualLink\DualLink.csproj'
$testProject = Join-Path $repoRoot 'tests\DualLink.Tests\DualLink.Tests.csproj'
$publishDirectory = Join-Path $repoRoot 'dist\publish'
$releaseDirectory = Join-Path $repoRoot 'dist\release'
$watchdogProject = Join-Path $repoRoot 'src\DualLink.Watchdog\DualLink.Watchdog.csproj'
$watchdogPublishDirectory = Join-Path $repoRoot 'dist\watchdog'
$serviceProject = Join-Path $repoRoot 'src\DualLink.Service\DualLink.Service.csproj'
$servicePublishDirectory = Join-Path $repoRoot 'dist\service'
$iconPath = Join-Path $repoRoot 'assets\DualLink.ico'
$iconPreviewPath = Join-Path $repoRoot 'docs\images\icon-preview.png'
$sbomPath = Join-Path $repoRoot 'dist\DualLink.spdx.json'

if (-not $SkipInstaller) {
    $distRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist')) + [IO.Path]::DirectorySeparatorChar
    $resolvedReleaseDirectory = [IO.Path]::GetFullPath($releaseDirectory)
    if (-not $resolvedReleaseDirectory.StartsWith($distRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a release directory outside '$distRoot'."
    }
    if (Test-Path -LiteralPath $releaseDirectory) {
        Get-ChildItem -LiteralPath $releaseDirectory -Force | ForEach-Object {
            if ($_.PSIsContainer) {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
            } else {
                Remove-Item -LiteralPath $_.FullName -Force
            }
        }
    } else {
        New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    }
}

Invoke-DotNet @('restore', $appProject, '--configfile', (Join-Path $repoRoot 'NuGet.Config')) 'App restore failed.'
Invoke-DotNet @('run', '--project', (Join-Path $repoRoot 'tools\IconMaker\IconMaker.csproj'), '-c', 'Release', '--', $iconPath, $iconPreviewPath) 'Icon generation failed.'
if (-not $SkipTests) {
    Invoke-DotNet @('run', '--project', $testProject, '-c', 'Release') 'Integration tests failed.'
} else {
    Write-Warning 'Integration tests were skipped. This output is not eligible for a stable GitHub Release.'
}

Invoke-DotNet @(
    'publish', $appProject, '-c', 'Release', '-p:PublishProfile=win-x64',
    "-p:Version=$($metadata.Numeric)", "-p:InformationalVersion=$($metadata.Informational)",
    '-p:IncludeSourceRevisionInInformationalVersion=false',
    '--output', $publishDirectory
) 'Self-contained publish failed.'
Invoke-DotNet @(
    'publish', $watchdogProject, '-c', 'Release',
    "-p:Version=$($metadata.Numeric)", "-p:InformationalVersion=$($metadata.Informational)",
    '-p:IncludeSourceRevisionInInformationalVersion=false',
    '--output', $watchdogPublishDirectory
) 'Watchdog publish failed.'
Invoke-DotNet @(
    'publish', $serviceProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:PublishTrimmed=false', '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:Version=$($metadata.Numeric)", "-p:InformationalVersion=$($metadata.Informational)",
    '-p:IncludeSourceRevisionInInformationalVersion=false',
    '--output', $servicePublishDirectory
) 'Privileged service publish failed.'

$watchdogPath = Join-Path $watchdogPublishDirectory 'DualLink.Watchdog.exe'
Assert-File $watchdogPath
Copy-Item -LiteralPath $watchdogPath -Destination (Join-Path $publishDirectory 'DualLink.Watchdog.exe') -Force
$servicePath = Join-Path $servicePublishDirectory 'DualLink.Service.exe'
Assert-File $servicePath
Copy-Item -LiteralPath $servicePath -Destination (Join-Path $publishDirectory 'DualLink.Service.exe') -Force

& (Join-Path $repoRoot 'tools\Generate-Sbom.ps1') -Version $metadata.Informational -ApplicationPath (Join-Path $publishDirectory 'DualLink.exe') -AdditionalApplicationPath $servicePath -OutputPath $sbomPath
if ($LASTEXITCODE -ne 0) { throw 'SBOM generation failed.' }

if (-not $SkipInstaller) {
    $compiler = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $compiler) { throw 'Inno Setup 6 was not found.' }

    & $compiler "/DAppVersion=$($metadata.Informational)" "/DNumericVersion=$($metadata.Numeric)" "/DReleaseStage=$resolvedStage" (Join-Path $repoRoot 'installer\DualLink.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

    $installerPath = Join-Path $repoRoot "dist\DualLink-$($metadata.Informational)-Setup-x64.exe"
    Assert-File $installerPath
    Assert-File $sbomPath
    $checksumsPath = Join-Path $repoRoot 'dist\SHA256SUMS.txt'
    $checksumLines = foreach ($file in @($installerPath, $sbomPath)) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path -Leaf $file)"
    }
    $checksumLines | Set-Content -LiteralPath $checksumsPath -Encoding ascii

    Copy-Item -LiteralPath $installerPath -Destination $releaseDirectory -Force
    Copy-Item -LiteralPath $sbomPath -Destination $releaseDirectory -Force
    Copy-Item -LiteralPath $checksumsPath -Destination $releaseDirectory -Force

    & (Join-Path $repoRoot 'tools\Test-Release.ps1') -ReleaseDirectory $releaseDirectory -Version $metadata.Informational -InstallerScriptPath (Join-Path $repoRoot 'installer\DualLink.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Public release asset validation failed.' }
} else {
    Write-Warning 'Installer creation was skipped. dist\release was not changed and this output is for internal evaluation only.'
}

Write-Host "DualLink $($metadata.Informational) build complete: $repoRoot\dist"
if (-not $SkipInstaller) {
    Write-Host "Public release files (exactly 3): $releaseDirectory"
}
