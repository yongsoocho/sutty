param(
    [string]$CandidateScript = (Resolve-Path (Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-AlphaCandidate.ps1')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tag = 'v1.2.3-alpha.4'
$commit = '0123456789abcdef0123456789abcdef01234567'
$repository = 'example/sutty'
$runId = '123456789'
$runAttempt = 2
$artifactName = "sutty-$tag-candidate-$runId-attempt-$runAttempt"
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = Join-Path $temporaryBase "sutty-alpha-candidate-tests-$([guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($scratch) | Out-Null

function Set-Utf8Text {
    param([string]$Path, [string]$Content)

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function New-CandidateFixture {
    param([string]$Name)

    $root = Join-Path $scratch $Name
    $packages = Join-Path $root 'packages'
    [System.IO.Directory]::CreateDirectory($packages) | Out-Null

    $archives = @(
        "Sutty-$tag-win-x64.zip"
        "Sutty-$tag-win-arm64.zip"
    ) | Sort-Object
    foreach ($archive in $archives) {
        Set-Utf8Text -Path (Join-Path $packages $archive) -Content "fixture:$archive"
    }

    $checksumLines = @($archives | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath (Join-Path $packages $_) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $_"
    })
    Set-Utf8Text `
        -Path (Join-Path $packages 'SHA256SUMS.txt') `
        -Content (($checksumLines -join [Environment]::NewLine) + [Environment]::NewLine)

    $manifest = Join-Path $root 'CANDIDATE-MANIFEST.json'
    & $CandidateScript `
        -PackageDirectory $packages `
        -ManifestPath $manifest `
        -Repository $repository `
        -Tag $tag `
        -Commit $commit `
        -CandidateRunId $runId `
        -CandidateRunAttempt $runAttempt `
        -ArtifactName $artifactName `
        -WriteManifest *> $null

    return [pscustomobject]@{
        Root = $root
        Packages = $packages
        Manifest = $manifest
    }
}

function Invoke-CandidateValidation {
    param(
        [Parameter(Mandatory)]$Fixture,
        [string]$ExpectedRepository = $repository,
        [string]$ExpectedTag = $tag,
        [string]$ExpectedCommit = $commit,
        [string]$ExpectedRunId = $runId,
        [int]$ExpectedRunAttempt = $runAttempt,
        [string]$ExpectedArtifactName = $artifactName
    )

    & $CandidateScript `
        -PackageDirectory $Fixture.Packages `
        -ManifestPath $Fixture.Manifest `
        -Repository $ExpectedRepository `
        -Tag $ExpectedTag `
        -Commit $ExpectedCommit `
        -CandidateRunId $ExpectedRunId `
        -CandidateRunAttempt $ExpectedRunAttempt `
        -ArtifactName $ExpectedArtifactName *> $null
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Name
    )

    $rejected = $false
    try {
        & $Action
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Alpha-candidate self-test failed: $Name was accepted."
    }
}

try {
    $valid = New-CandidateFixture 'valid'
    Invoke-CandidateValidation -Fixture $valid

    $tamperedArchive = New-CandidateFixture 'tampered-archive'
    Add-Content -LiteralPath (Join-Path $tamperedArchive.Packages "Sutty-$tag-win-x64.zip") -Value 'tampered'
    Assert-Rejected { Invoke-CandidateValidation -Fixture $tamperedArchive } 'tampered archive bytes'

    $tamperedChecksums = New-CandidateFixture 'tampered-checksums'
    $checksumPath = Join-Path $tamperedChecksums.Packages 'SHA256SUMS.txt'
    (Get-Content -LiteralPath $checksumPath -Raw).Replace('a', 'b') |
        Set-Content -LiteralPath $checksumPath -Encoding utf8NoBOM
    Assert-Rejected { Invoke-CandidateValidation -Fixture $tamperedChecksums } 'tampered checksum file'

    $unexpectedFile = New-CandidateFixture 'unexpected-file'
    Set-Utf8Text -Path (Join-Path $unexpectedFile.Packages 'extra.txt') -Content 'unexpected'
    Assert-Rejected { Invoke-CandidateValidation -Fixture $unexpectedFile } 'unexpected package file'

    $unexpectedRootFile = New-CandidateFixture 'unexpected-root-file'
    Set-Utf8Text -Path (Join-Path $unexpectedRootFile.Root 'extra.txt') -Content 'unexpected'
    Assert-Rejected { Invoke-CandidateValidation -Fixture $unexpectedRootFile } 'unexpected candidate-root file'

    $missingFile = New-CandidateFixture 'missing-file'
    Remove-Item -LiteralPath (Join-Path $missingFile.Packages "Sutty-$tag-win-arm64.zip") -Force
    Assert-Rejected { Invoke-CandidateValidation -Fixture $missingFile } 'missing package file'

    $extraProperty = New-CandidateFixture 'extra-property'
    $extraObject = Get-Content -LiteralPath $extraProperty.Manifest -Raw | ConvertFrom-Json
    $extraObject | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
    Set-Utf8Text -Path $extraProperty.Manifest -Content (($extraObject | ConvertTo-Json -Depth 5) + [Environment]::NewLine)
    Assert-Rejected { Invoke-CandidateValidation -Fixture $extraProperty } 'extra manifest property'

    $duplicateProperty = New-CandidateFixture 'duplicate-property'
    $duplicateText = Get-Content -LiteralPath $duplicateProperty.Manifest -Raw
    $duplicateText = $duplicateText.Replace(
        '"schema_version": 1,',
        '"schema_version": 1, "schema_version": 1,')
    Set-Utf8Text -Path $duplicateProperty.Manifest -Content $duplicateText
    Assert-Rejected { Invoke-CandidateValidation -Fixture $duplicateProperty } 'duplicate manifest property'

    $wrongCommit = New-CandidateFixture 'wrong-commit'
    Assert-Rejected {
        Invoke-CandidateValidation `
            -Fixture $wrongCommit `
            -ExpectedCommit 'fedcba9876543210fedcba9876543210fedcba98'
    } 'wrong expected commit'

    $wrongAttempt = New-CandidateFixture 'wrong-attempt'
    Assert-Rejected {
        Invoke-CandidateValidation -Fixture $wrongAttempt -ExpectedRunAttempt 3
    } 'wrong workflow run attempt'

    $wrongRepository = New-CandidateFixture 'wrong-repository'
    Assert-Rejected {
        Invoke-CandidateValidation -Fixture $wrongRepository -ExpectedRepository 'other/sutty'
    } 'wrong repository'

    $manifestHash = New-CandidateFixture 'manifest-hash'
    $manifestObject = Get-Content -LiteralPath $manifestHash.Manifest -Raw | ConvertFrom-Json
    $manifestObject.files[0].sha256 = '0' * 64
    Set-Utf8Text -Path $manifestHash.Manifest -Content (($manifestObject | ConvertTo-Json -Depth 5) + [Environment]::NewLine)
    Assert-Rejected { Invoke-CandidateValidation -Fixture $manifestHash } 'manifest file hash mismatch'

    $manifestSize = New-CandidateFixture 'manifest-size'
    $manifestObject = Get-Content -LiteralPath $manifestSize.Manifest -Raw | ConvertFrom-Json
    $manifestObject.files[0].size_bytes++
    Set-Utf8Text -Path $manifestSize.Manifest -Content (($manifestObject | ConvertTo-Json -Depth 5) + [Environment]::NewLine)
    Assert-Rejected { Invoke-CandidateValidation -Fixture $manifestSize } 'manifest file size mismatch'

    $manifestOrder = New-CandidateFixture 'manifest-order'
    $manifestObject = Get-Content -LiteralPath $manifestOrder.Manifest -Raw | ConvertFrom-Json
    $first = $manifestObject.files[0]
    $manifestObject.files[0] = $manifestObject.files[1]
    $manifestObject.files[1] = $first
    Set-Utf8Text -Path $manifestOrder.Manifest -Content (($manifestObject | ConvertTo-Json -Depth 5) + [Environment]::NewLine)
    Assert-Rejected { Invoke-CandidateValidation -Fixture $manifestOrder } 'manifest file order mismatch'

    Write-Host 'Alpha-candidate guard self-tests passed (13 rejection cases plus the valid fixture).'
}
finally {
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ((Test-Path -LiteralPath $resolvedScratch) -and
        $resolvedScratch.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith(
            'sutty-alpha-candidate-tests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
