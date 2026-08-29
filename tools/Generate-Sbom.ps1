[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$ApplicationPath,
    [Parameter(Mandatory)] [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$applicationHash = (Get-FileHash -LiteralPath $ApplicationPath -Algorithm SHA256).Hash.ToLowerInvariant()
$created = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "DualLink-$Version"
    documentNamespace = "https://github.com/Vansh-Bhardwaj/DualLink/sbom/$Version/$applicationHash"
    creationInfo = [ordered]@{
        created = $created
        creators = @('Tool: DualLink-Generate-Sbom.ps1', 'Organization: DualLink contributors')
    }
    packages = @(
        [ordered]@{
            name = 'DualLink'; SPDXID = 'SPDXRef-Package-DualLink'; versionInfo = $Version
            downloadLocation = 'NOASSERTION'; filesAnalyzed = $false
            licenseConcluded = 'AGPL-3.0-only'; licenseDeclared = 'AGPL-3.0-only'
            checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = $applicationHash })
            copyrightText = 'Copyright (c) 2026 DualLink contributors'
        },
        [ordered]@{
            name = 'ProxiFyre'; SPDXID = 'SPDXRef-Package-ProxiFyre'; versionInfo = '2.5.0'
            downloadLocation = 'https://github.com/wiresock/proxifyre/tree/v2.5.0'; filesAnalyzed = $false
            licenseConcluded = 'AGPL-3.0-only'; licenseDeclared = 'AGPL-3.0-only'
            checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = 'f0de33fba4224b441dc851ca4a31c5e2620310bdb2a6f5cd3fb9843606bc4d24' })
            copyrightText = 'Copyright WireSock contributors'
        },
        [ordered]@{
            name = 'Windows Packet Filter driver'; SPDXID = 'SPDXRef-Package-WinpkFilter'; versionInfo = '3.6.2.1'
            downloadLocation = 'https://github.com/wiresock/ndisapi/releases/tag/v3.6.2'; filesAnalyzed = $false
            licenseConcluded = 'LicenseRef-Windows-Packet-Filter-Personal-Use'; licenseDeclared = 'LicenseRef-Windows-Packet-Filter-Personal-Use'
            checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = '9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c' })
            copyrightText = 'Copyright NT Kernel Resources / WireSock'
        },
        [ordered]@{
            name = 'Microsoft .NET Runtime'; SPDXID = 'SPDXRef-Package-DotNetRuntime'; versionInfo = '10.0.11'
            downloadLocation = 'https://github.com/dotnet/runtime/tree/v10.0.11'; filesAnalyzed = $false
            licenseConcluded = 'MIT'; licenseDeclared = 'MIT'; copyrightText = 'Copyright .NET Foundation and contributors'
        },
        [ordered]@{
            name = 'Microsoft Visual C++ Redistributable'; SPDXID = 'SPDXRef-Package-VCRedist'; versionInfo = '14.44.35211.0'
            downloadLocation = 'NOASSERTION'; filesAnalyzed = $false
            licenseConcluded = 'NOASSERTION'; licenseDeclared = 'NOASSERTION'
            checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = 'cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b' })
            copyrightText = 'Copyright Microsoft Corporation'
        },
        [ordered]@{
            name = 'Inter'; SPDXID = 'SPDXRef-Package-Inter'; versionInfo = '4.1'
            downloadLocation = 'https://github.com/rsms/inter/releases/tag/v4.1'; filesAnalyzed = $false
            licenseConcluded = 'OFL-1.1'; licenseDeclared = 'OFL-1.1'
            copyrightText = 'Copyright The Inter Project Authors'
        }
    )
    hasExtractedLicensingInfos = @(
        [ordered]@{
            licenseId = 'LicenseRef-Windows-Packet-Filter-Personal-Use'
            extractedText = 'Free for personal, educational, and nonprofit use. Commercial use requires a separate license. See https://www.ntkernel.com/windows-packet-filter/licensing/.'
            name = 'Windows Packet Filter personal-use license'
        }
    )
    relationships = @(
        [ordered]@{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = 'SPDXRef-Package-DualLink' },
        [ordered]@{ spdxElementId = 'SPDXRef-Package-DualLink'; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = 'SPDXRef-Package-ProxiFyre' },
        [ordered]@{ spdxElementId = 'SPDXRef-Package-DualLink'; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = 'SPDXRef-Package-WinpkFilter' },
        [ordered]@{ spdxElementId = 'SPDXRef-Package-DualLink'; relationshipType = 'CONTAINS'; relatedSpdxElement = 'SPDXRef-Package-DotNetRuntime' },
        [ordered]@{ spdxElementId = 'SPDXRef-Package-DualLink'; relationshipType = 'CONTAINS'; relatedSpdxElement = 'SPDXRef-Package-Inter' },
        [ordered]@{ spdxElementId = 'SPDXRef-Package-ProxiFyre'; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = 'SPDXRef-Package-VCRedist' }
    )
}

$directory = Split-Path -Parent $OutputPath
if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
$document | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
