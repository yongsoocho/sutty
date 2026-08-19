param(
    [string]$MetadataScript = (Resolve-Path (Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-ReleaseMetadata.ps1')).Path,
    [string]$WorkflowPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\.github\workflows\alpha-release.yml')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tag = 'v1.2.3-alpha.4'
$commit = '0123456789abcdef0123456789abcdef01234567'
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = Join-Path $temporaryBase "sutty-release-metadata-tests-$([Guid]::NewGuid().ToString('N'))"

function Set-Utf8Text {
    param(
        [string]$Path,
        [string]$Content
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function New-FixtureRepository {
    param([string]$Name)

    $root = Join-Path $scratch $Name
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    Set-Utf8Text -Path (Join-Path $root 'Directory.Build.props') -Content @'
<Project>
  <PropertyGroup>
    <VersionPrefix>1.2.3</VersionPrefix>
    <VersionSuffix>alpha.4</VersionSuffix>
    <Version>$(VersionPrefix)-$(VersionSuffix)</Version>
    <InformationalVersion>$(Version)</InformationalVersion>
  </PropertyGroup>
</Project>
'@
    Set-Utf8Text -Path (Join-Path $root 'README.md') -Content @'
# Sutty

> **Current / 현재:** [`v1.2.3-alpha.4`](https://github.com/yongsoocho/sutty/releases/tag/v1.2.3-alpha.4) · [Download](https://github.com/yongsoocho/sutty/releases)
'@
    Set-Utf8Text -Path (Join-Path $root 'docs\ALPHA_INSTALL.md') -Content @'
# Sutty Alpha installation

Open the official releases page and download `Sutty-*-win-x64.zip` or `Sutty-*-win-arm64.zip`.
Verify the selected `Sutty-*-win-*.zip` against `SHA256SUMS.txt`.
'@
    Set-Utf8Text -Path (Join-Path $root "docs\releases\$tag.md") -Content @"
# Sutty 1.2.3 Alpha 4

## Downloads

- ``Sutty-$tag-win-x64.zip``
- ``Sutty-$tag-win-arm64.zip``
- ``SHA256SUMS.txt``
"@

    return $root
}

function Add-Payloads {
    param([string]$Root)

    $sourceGuide = Join-Path $Root 'docs\ALPHA_INSTALL.md'
    foreach ($architecture in @('x64', 'arm64')) {
        $payloadPath = Join-Path $Root "artifacts\alpha-win-$architecture"
        New-Item -ItemType Directory -Path $payloadPath -Force | Out-Null
        Set-Utf8Text -Path (Join-Path $payloadPath 'BUILDINFO.txt') -Content @"
Sutty $tag
Commit: $commit
Channel: Alpha
Signing: unsigned ZIP evaluation build
Minimum OS: Windows 11 24H2
Architecture: $architecture
"@
        Copy-Item -LiteralPath $sourceGuide -Destination (Join-Path $payloadPath 'ALPHA_INSTALL.md')
        Set-Utf8Text -Path (Join-Path $payloadPath 'sutty.UI.exe') -Content "fixture-$architecture"
        Set-Utf8Text `
            -Path (Join-Path $payloadPath "runtimes\win-$architecture\native\fixture.dll") `
            -Content "nested-fixture-$architecture"
    }
}

function Update-Checksums {
    param([string]$Root)

    $packageDirectory = Join-Path $Root 'artifacts\packages'
    $hashLines = @(
        Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*.zip' |
            Sort-Object Name |
            ForEach-Object {
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$hash  $($_.Name)"
            }
    )
    Set-Utf8Text -Path (Join-Path $packageDirectory 'SHA256SUMS.txt') -Content (
        [string]::Join([Environment]::NewLine, $hashLines) + [Environment]::NewLine)
}

function Add-Packages {
    param([string]$Root)

    $packageDirectory = Join-Path $Root 'artifacts\packages'
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    foreach ($architecture in @('x64', 'arm64')) {
        Compress-Archive `
            -Path (Join-Path $Root "artifacts\alpha-win-$architecture\*") `
            -DestinationPath (Join-Path $packageDirectory "Sutty-$tag-win-$architecture.zip") `
            -CompressionLevel Optimal
    }
    Update-Checksums -Root $Root
}

function Get-ValidationFailure {
    param(
        [string]$Root,
        [switch]$Artifacts
    )

    try {
        if ($Artifacts) {
            & $MetadataScript `
                -Tag $tag `
                -RepositoryRoot $Root `
                -Commit $commit `
                -X64PayloadPath (Join-Path $Root 'artifacts\alpha-win-x64') `
                -Arm64PayloadPath (Join-Path $Root 'artifacts\alpha-win-arm64') `
                -PackageDirectory (Join-Path $Root 'artifacts\packages') *> $null
        }
        else {
            & $MetadataScript -Tag $tag -RepositoryRoot $Root *> $null
        }
        return $null
    }
    catch {
        return $_.Exception.Message
    }
}

function Assert-Result {
    param(
        [bool]$Condition,
        [string]$Name
    )

    if (-not $Condition) {
        throw "Release-metadata self-test failed: $Name."
    }
}

try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    $sourcePass = New-FixtureRepository 'source-pass'
    Assert-Result ($null -eq (Get-ValidationFailure -Root $sourcePass)) 'consistent source metadata passes'

    $artifactPass = New-FixtureRepository 'artifact-pass'
    Add-Payloads -Root $artifactPass
    Add-Packages -Root $artifactPass
    Assert-Result ($null -eq (Get-ValidationFailure -Root $artifactPass -Artifacts)) 'consistent payloads, ZIPs, and checksums pass'

    $sourceMismatch = New-FixtureRepository 'source-version-mismatch'
    (Get-Content -LiteralPath (Join-Path $sourceMismatch 'Directory.Build.props') -Raw).
        Replace('<VersionSuffix>alpha.4</VersionSuffix>', '<VersionSuffix>alpha.3</VersionSuffix>') |
        Set-Content -LiteralPath (Join-Path $sourceMismatch 'Directory.Build.props') -Encoding utf8NoBOM
    Assert-Result ($null -ne (Get-ValidationFailure -Root $sourceMismatch)) 'source version mismatch is rejected'

    $versionOverride = New-FixtureRepository 'version-override'
    (Get-Content -LiteralPath (Join-Path $versionOverride 'Directory.Build.props') -Raw).
        Replace('<Version>$(VersionPrefix)-$(VersionSuffix)</Version>', '<Version>9.9.9</Version>') |
        Set-Content -LiteralPath (Join-Path $versionOverride 'Directory.Build.props') -Encoding utf8NoBOM
    Assert-Result ($null -ne (Get-ValidationFailure -Root $versionOverride)) 'explicit Version override is rejected'

    $informationalVersionOverride = New-FixtureRepository 'informational-version-override'
    (Get-Content -LiteralPath (Join-Path $informationalVersionOverride 'Directory.Build.props') -Raw).
        Replace('<InformationalVersion>$(Version)</InformationalVersion>', '<InformationalVersion>9.9.9</InformationalVersion>') |
        Set-Content -LiteralPath (Join-Path $informationalVersionOverride 'Directory.Build.props') -Encoding utf8NoBOM
    Assert-Result ($null -ne (Get-ValidationFailure -Root $informationalVersionOverride)) 'explicit InformationalVersion override is rejected'

    $readmeMismatch = New-FixtureRepository 'readme-mismatch'
    (Get-Content -LiteralPath (Join-Path $readmeMismatch 'README.md') -Raw).
        Replace('v1.2.3-alpha.4', 'v1.2.3-alpha.3') |
        Set-Content -LiteralPath (Join-Path $readmeMismatch 'README.md') -Encoding utf8NoBOM
    Assert-Result ($null -ne (Get-ValidationFailure -Root $readmeMismatch)) 'stale README Current release is rejected'

    $notesMismatch = New-FixtureRepository 'notes-mismatch'
    (Get-Content -LiteralPath (Join-Path $notesMismatch "docs\releases\$tag.md") -Raw).
        Replace("Sutty-$tag-win-arm64.zip", 'Sutty-v1.2.3-alpha.3-win-arm64.zip') |
        Set-Content -LiteralPath (Join-Path $notesMismatch "docs\releases\$tag.md") -Encoding utf8NoBOM
    Assert-Result ($null -ne (Get-ValidationFailure -Root $notesMismatch)) 'stale release-note asset name is rejected'

    $installMismatch = New-FixtureRepository 'install-mismatch'
    Add-Content `
        -LiteralPath (Join-Path $installMismatch 'docs\ALPHA_INSTALL.md') `
        -Value 'Historical command: Sutty-v1.2.3-alpha.3-win-x64.zip' `
        -Encoding utf8NoBOM
    Assert-Result ($null -ne (Get-ValidationFailure -Root $installMismatch)) 'versioned installation guide is rejected'

    $buildInfoMismatch = New-FixtureRepository 'buildinfo-mismatch'
    Add-Payloads -Root $buildInfoMismatch
    (Get-Content -LiteralPath (Join-Path $buildInfoMismatch 'artifacts\alpha-win-x64\BUILDINFO.txt') -Raw).
        Replace($tag, 'v1.2.3-alpha.3') |
        Set-Content -LiteralPath (Join-Path $buildInfoMismatch 'artifacts\alpha-win-x64\BUILDINFO.txt') -Encoding utf8NoBOM
    Add-Packages -Root $buildInfoMismatch
    Assert-Result ($null -ne (Get-ValidationFailure -Root $buildInfoMismatch -Artifacts)) 'stale payload and ZIP BUILDINFO are rejected'

    $buildInfoConflict = New-FixtureRepository 'buildinfo-conflict'
    Add-Payloads -Root $buildInfoConflict
    Add-Content `
        -LiteralPath (Join-Path $buildInfoConflict 'artifacts\alpha-win-x64\BUILDINFO.txt') `
        -Value 'Commit: ffffffffffffffffffffffffffffffffffffffff' `
        -Encoding utf8NoBOM
    Add-Packages -Root $buildInfoConflict
    Assert-Result ($null -ne (Get-ValidationFailure -Root $buildInfoConflict -Artifacts)) 'extra conflicting BUILDINFO identity line is rejected'

    $guideMismatch = New-FixtureRepository 'packaged-guide-mismatch'
    Add-Payloads -Root $guideMismatch
    Add-Content `
        -LiteralPath (Join-Path $guideMismatch 'artifacts\alpha-win-arm64\ALPHA_INSTALL.md') `
        -Value 'changed after source validation' `
        -Encoding utf8NoBOM
    Add-Packages -Root $guideMismatch
    Assert-Result ($null -ne (Get-ValidationFailure -Root $guideMismatch -Artifacts)) 'changed packaged installation guide is rejected'

    $checksumMismatch = New-FixtureRepository 'checksum-mismatch'
    Add-Payloads -Root $checksumMismatch
    Add-Packages -Root $checksumMismatch
    $checksumPath = Join-Path $checksumMismatch 'artifacts\packages\SHA256SUMS.txt'
    $checksumLines = @(Get-Content -LiteralPath $checksumPath)
    $checksumLines[0] = ('0' * 64) + $checksumLines[0].Substring(64)
    Set-Utf8Text -Path $checksumPath -Content ([string]::Join([Environment]::NewLine, $checksumLines) + [Environment]::NewLine)
    Assert-Result ($null -ne (Get-ValidationFailure -Root $checksumMismatch -Artifacts)) 'incorrect archive checksum is rejected'

    $architectureMismatch = New-FixtureRepository 'archive-architecture-mismatch'
    Add-Payloads -Root $architectureMismatch
    Add-Packages -Root $architectureMismatch
    Copy-Item `
        -LiteralPath (Join-Path $architectureMismatch "artifacts\packages\Sutty-$tag-win-arm64.zip") `
        -Destination (Join-Path $architectureMismatch "artifacts\packages\Sutty-$tag-win-x64.zip") `
        -Force
    Update-Checksums -Root $architectureMismatch
    Assert-Result ($null -ne (Get-ValidationFailure -Root $architectureMismatch -Artifacts)) 'ZIP BUILDINFO architecture mismatch is rejected'

    $missingExecutable = New-FixtureRepository 'archive-missing-executable'
    Add-Payloads -Root $missingExecutable
    Add-Packages -Root $missingExecutable
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $missingExecutableArchivePath = Join-Path $missingExecutable "artifacts\packages\Sutty-$tag-win-x64.zip"
    $missingExecutableArchive = [System.IO.Compression.ZipFile]::Open(
        $missingExecutableArchivePath,
        [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $executableEntries = @($missingExecutableArchive.Entries | Where-Object {
            $_.FullName.Replace('\', '/') -ceq 'sutty.UI.exe'
        })
        Assert-Result ($executableEntries.Count -eq 1) 'missing-executable fixture starts with one root executable'
        $executableEntries[0].Delete()
    }
    finally {
        $missingExecutableArchive.Dispose()
    }
    Update-Checksums -Root $missingExecutable
    Assert-Result ($null -ne (Get-ValidationFailure -Root $missingExecutable -Artifacts)) 'archive missing the root executable is rejected after checksum refresh'

    $extraPackage = New-FixtureRepository 'extra-package-file'
    Add-Payloads -Root $extraPackage
    Add-Packages -Root $extraPackage
    Set-Utf8Text -Path (Join-Path $extraPackage 'artifacts\packages\unexpected.txt') -Content 'unexpected'
    Assert-Result ($null -ne (Get-ValidationFailure -Root $extraPackage -Artifacts)) 'unexpected package file is rejected'

    $workflow = [System.IO.File]::ReadAllText($WorkflowPath)
    Assert-Result ($workflow -notmatch '(?i)--clobber') 'Alpha workflow never uses asset clobbering'
    Assert-Result ($workflow -notmatch '(?i)gh\s+release\s+edit') 'Alpha workflow never edits an existing release'
    Assert-Result ($workflow -notmatch '(?i)gh\s+release\s+upload') 'Alpha workflow never uploads assets to an existing release'
    Assert-Result (@([regex]::Matches($workflow, '(?i)gh\s+release\s+create')).Count -eq 1) 'Alpha workflow has one create-only publication command'
    Assert-Result ($workflow -match 'Release already exists') 'Alpha workflow explicitly rejects an existing release'
    Assert-Result ($workflow -match 'gh\s+api\s+"repos/\$repository/releases/tags/\$tag"') 'Alpha workflow uses the release-by-tag API for the preflight lookup'
    Assert-Result ($workflow -match '\$PSNativeCommandUseErrorActionPreference\s*=\s*\$false') 'Alpha workflow handles the expected non-zero native lookup without premature termination'
    Assert-Result ($workflow -match '\$lookupExitCode\s*=\s*\$LASTEXITCODE') 'Alpha workflow captures the preflight native exit code'
    Assert-Result ($workflow -match '\$global:LASTEXITCODE\s*=\s*0') 'Alpha workflow clears the expected missing-release native exit code'
    Assert-Result ($workflow -match '\\?\(HTTP 404\\?\)') 'Alpha workflow accepts only an HTTP 404 as a missing release'
    Assert-Result ($workflow -match 'Could not verify release absence') 'Alpha workflow fails closed on lookup errors other than HTTP 404'
    Assert-Result ($workflow.IndexOf('Release already exists', [StringComparison]::Ordinal) -lt
        $workflow.IndexOf('Restore locked x64 dependencies', [StringComparison]::Ordinal)) 'existing release rejection runs before build work'
    Assert-Result ($workflow -match 'tests\\product-scope\\Assert-ProductScope\.Tests\.ps1') 'Alpha workflow runs product-scope fixture tests'
    Assert-Result (@([regex]::Matches($workflow, 'Assert-ReleaseMetadata\.ps1')).Count -eq 2) 'Alpha workflow has pre-package and post-package metadata gates'

    Write-Host 'Release-metadata guard self-tests passed (15 fixture cases plus workflow immutability contract).'
}
finally {
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ((Test-Path -LiteralPath $resolvedScratch) -and
        $resolvedScratch.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith(
            'sutty-release-metadata-tests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
