param(
    [string]$MetadataScript = (Resolve-Path (Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-ReleaseMetadata.ps1')).Path,
    [string]$WorkflowPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\.github\workflows\alpha-release.yml')).Path,
    [string]$CandidateWorkflowPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\.github\workflows\alpha-candidate.yml')).Path
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

    $contentMismatch = New-FixtureRepository 'archive-content-mismatch'
    Add-Payloads -Root $contentMismatch
    Add-Packages -Root $contentMismatch
    $contentMismatchArchivePath = Join-Path $contentMismatch "artifacts\packages\Sutty-$tag-win-x64.zip"
    $contentMismatchArchive = [System.IO.Compression.ZipFile]::Open(
        $contentMismatchArchivePath,
        [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $originalEntry = @($contentMismatchArchive.Entries | Where-Object {
            $_.FullName.Replace('\\', '/') -ceq 'sutty.UI.exe'
        })
        Assert-Result ($originalEntry.Count -eq 1) 'content-mismatch fixture starts with one root executable'
        $originalStream = $originalEntry[0].Open()
        try {
            $memory = [System.IO.MemoryStream]::new()
            try {
                $originalStream.CopyTo($memory)
                $changedBytes = $memory.ToArray()
            }
            finally {
                $memory.Dispose()
            }
        }
        finally {
            $originalStream.Dispose()
        }
        Assert-Result ($changedBytes.Length -gt 0) 'content-mismatch fixture executable is non-empty'
        $changedBytes[0] = $changedBytes[0] -bxor 0x01
        $originalEntry[0].Delete()
        $replacementEntry = $contentMismatchArchive.CreateEntry(
            'sutty.UI.exe',
            [System.IO.Compression.CompressionLevel]::Optimal)
        $replacementStream = $replacementEntry.Open()
        try {
            $replacementStream.Write($changedBytes, 0, $changedBytes.Length)
        }
        finally {
            $replacementStream.Dispose()
        }
    }
    finally {
        $contentMismatchArchive.Dispose()
    }
    Update-Checksums -Root $contentMismatch
    Assert-Result ($null -ne (Get-ValidationFailure -Root $contentMismatch -Artifacts)) 'same-length ZIP entry content mismatch is rejected after checksum refresh'

    $extraPackage = New-FixtureRepository 'extra-package-file'
    Add-Payloads -Root $extraPackage
    Add-Packages -Root $extraPackage
    Set-Utf8Text -Path (Join-Path $extraPackage 'artifacts\packages\unexpected.txt') -Content 'unexpected'
    Assert-Result ($null -ne (Get-ValidationFailure -Root $extraPackage -Artifacts)) 'unexpected package file is rejected'

    $workflow = [System.IO.File]::ReadAllText($WorkflowPath)
    $candidateWorkflow = [System.IO.File]::ReadAllText($CandidateWorkflowPath)

    Assert-Result ($candidateWorkflow -match '(?m)^\s*workflow_dispatch:\s*$') 'candidate workflow is manual-only'
    Assert-Result ($candidateWorkflow -notmatch '(?m)^\s*push:\s*$') 'candidate workflow has no push trigger'
    Assert-Result ($candidateWorkflow -notmatch '(?i)gh\s+release\s+(?:create|edit|upload)') 'candidate workflow cannot publish or mutate a release'
    Assert-Result (@([regex]::Matches($candidateWorkflow, 'actions/upload-artifact@v4')).Count -eq 1) 'candidate workflow uploads one sealed artifact'
    Assert-Result ($candidateWorkflow -match 'compression-level:\s*0') 'candidate workflow preserves exact package bytes in the workflow artifact'
    Assert-Result ($candidateWorkflow -match 'CANDIDATE-MANIFEST\.json') 'candidate workflow uploads a strict candidate manifest'
    Assert-Result ($candidateWorkflow -match 'Assert-AlphaCandidate\.ps1') 'candidate workflow seals the candidate manifest'
    Assert-Result ($candidateWorkflow -match '-WriteManifest') 'candidate workflow creates the manifest only after packaging'
    Assert-Result (@([regex]::Matches($candidateWorkflow, 'Assert-ReleaseMetadata\.ps1')).Count -eq 2) 'candidate workflow has pre-package and post-package metadata gates'
    Assert-Result ($candidateWorkflow -match 'tests\\live-evidence\\Assert-LiveEvidence\.Tests\.ps1') 'candidate workflow runs live-evidence fixtures'
    Assert-Result ($candidateWorkflow -match 'tests\\release-candidate\\Assert-AlphaCandidate\.Tests\.ps1') 'candidate workflow runs candidate-manifest fixtures'
    Assert-Result ($candidateWorkflow -match "GITHUB_REF\s+-cne\s+'refs/heads/main'") 'candidate workflow accepts source only from main'
    Assert-Result ($candidateWorkflow -match 'Tag already exists') 'candidate workflow rejects an existing tag before building'
    Assert-Result ($candidateWorkflow -match 'Release already exists') 'candidate workflow rejects an existing release before building'
    Assert-Result ($candidateWorkflow.IndexOf('Tag already exists', [StringComparison]::Ordinal) -lt
        $candidateWorkflow.IndexOf('Restore locked x64 dependencies', [StringComparison]::Ordinal)) 'candidate publication preflight runs before build work'

    Assert-Result ($workflow -match '(?m)^\s*workflow_dispatch:\s*$') 'promotion workflow is manual-only'
    Assert-Result ($workflow -notmatch '(?m)^\s*push:\s*$') 'promotion workflow cannot publish from a tag push'
    Assert-Result ($workflow -match "GITHUB_REF\s+-cne\s+'refs/heads/main'") 'promotion workflow itself must be dispatched from main'
    Assert-Result ($workflow -notmatch '(?i)dotnet\s+(?:restore|build|publish)') 'promotion workflow never rebuilds candidate bytes'
    Assert-Result ($workflow -notmatch '(?i)Compress-Archive') 'promotion workflow never repackages candidate bytes'
    Assert-Result ($workflow -notmatch '(?i)--clobber') 'promotion workflow never uses asset clobbering'
    Assert-Result ($workflow -notmatch '(?i)gh\s+release\s+edit') 'promotion workflow never edits an existing release'
    Assert-Result ($workflow -notmatch '(?i)gh\s+release\s+upload') 'promotion workflow never uploads assets to an existing release'
    Assert-Result (@([regex]::Matches($workflow, '(?i)gh\s+release\s+create')).Count -eq 1) 'promotion workflow has one create-only publication command'
    Assert-Result ($workflow -match 'actions/download-artifact@v4') 'promotion workflow downloads the sealed candidate from its exact run'
    Assert-Result ($workflow -match 'run-id:\s*\$\{\{ inputs\.candidate_run_id \}\}') 'promotion download is pinned to the candidate run ID'
    Assert-Result ($workflow -match 'Assert-AlphaCandidate\.ps1') 'promotion workflow revalidates the strict candidate manifest and checksums'
    Assert-Result ($workflow -match 'Assert-ReleaseMetadata\.ps1') 'promotion workflow revalidates source and packaged metadata'
    Assert-Result ($workflow -match 'Assert-LiveEvidence\.ps1') 'promotion workflow validates reviewed live evidence'
    Assert-Result ($workflow -match '-RequiredResult Pass') 'promotion requires accepted Pass evidence'
    Assert-Result (@([regex]::Matches($workflow, '-RequiredGateId SSH-LIVE-001')).Count -eq 1) 'promotion requires the exact SSH-LIVE-001 release gate'
    Assert-Result ($workflow -match 'merge-base --is-ancestor') 'promotion proves acceptance ancestry on main'
    Assert-Result ($workflow -match 'actions/runs/\$env:CANDIDATE_RUN_ID') 'promotion verifies the candidate workflow run through the API'
    Assert-Result ($workflow -match 'status\s+-cne\s+''completed''') 'promotion requires a completed candidate run'
    Assert-Result ($workflow -match 'conclusion\s+-cne\s+''success''') 'promotion requires a successful candidate run'
    Assert-Result ($workflow -match 'immutable-releases') 'promotion checks the immutable releases repository setting'
    Assert-Result ($workflow -match 'X-GitHub-Api-Version: 2026-03-10') 'promotion pins the immutable releases API version'
    Assert-Result ($workflow -match 'immutable\.enabled\s+-ne\s+\$true') 'promotion fails unless immutable releases are enabled'
    Assert-Result ($workflow -match 'ls-remote origin') 'promotion rechecks the remote tag target immediately before publication'
    Assert-Result ($workflow -match 'Release already exists') 'promotion explicitly rejects an existing release'
    Assert-Result ($workflow -match '\\?\(HTTP 404\\?\)') 'promotion accepts only an HTTP 404 as a missing release'
    Assert-Result ($workflow -match 'Could not verify release absence') 'promotion fails closed on release lookup errors other than HTTP 404'
    Assert-Result ($workflow.IndexOf('Release already exists', [StringComparison]::Ordinal) -lt
        $workflow.IndexOf('gh release create', [StringComparison]::OrdinalIgnoreCase)) 'release absence is checked before publication'
    Assert-Result ($workflow -match 'CANDIDATE-MANIFEST\.json') 'promotion publishes the provenance manifest with the exact packages'
    Assert-Result ($workflow -match 'gh\s+release\s+download') 'promotion downloads published assets for byte verification'
    Assert-Result ($workflow -match '''release'', ''verify'', \$env:CANDIDATE_TAG') 'promotion verifies the signed immutable release attestation'
    Assert-Result ($workflow -match '''release'', ''verify-asset'', \$env:CANDIDATE_TAG') 'promotion verifies every published asset attestation'
    Assert-Result ($workflow -match 'for \(\$attempt = 1; \$attempt -le 6; \$attempt\+\+\)') 'attestation verification uses bounded propagation retries'
    Assert-Result ($workflow -match 'release\.immutable\s+-ne\s+\$true') 'promotion requires the published release itself to be immutable'
    Assert-Result (@([regex]::Matches($workflow, 'ls-remote origin')).Count -ge 2) 'promotion verifies the exact tag target before and after publication'

    Write-Host 'Release-metadata guard self-tests passed (16 fixture cases plus two-phase pipeline contract).'
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
