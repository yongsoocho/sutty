param(
    [string]$AttestationScript = (Resolve-Path (
        Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-ReleaseAttestation.ps1')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tag = 'v1.2.3-alpha.4'
$repository = 'example/sutty'
$candidateCommit = '0123456789abcdef0123456789abcdef01234567'
$acceptanceCommit = '89abcdef0123456789abcdef0123456789abcdef'
$candidateRunId = '123456789'
$candidateRunAttempt = 2
$artifactId = '987654321'
$artifactName = "sutty-$tag-candidate-$candidateRunId-attempt-$candidateRunAttempt"
$artifactDigest = 'sha256:' + ('a' * 64)
$promotionRunId = '2233445566'
$promotionRunAttempt = 3
$evidenceRepositoryPath = 'docs/evidence/alpha4/ssh-auth/reviewed-fixture/manifest.yml'
$packageEvidenceRepositoryPath = 'docs/evidence/alpha4/package/reviewed-package-fixture/manifest.yml'
$reviewedAtUtc = '2026-08-21T01:02:03.456Z'
$startedAtUtc = '2026-08-21T00:58:00Z'
$packageSha256Placeholder = 'b' * 64
$privacyNotice =
    'Connection identifiers, credentials, filesystem locations, session content, and cryptographic material are excluded.'
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = Join-Path $temporaryBase "sutty-release-attestation-tests-$([guid]::NewGuid().ToString('N'))"
$caseCount = 0

function Set-Utf8Text {
    param([string]$Path, [string]$Content)

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-TextSha256 {
    param([string]$Text)

    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-CanonicalBundleDigest {
    param([object[]]$Records)

    $lines = [System.Text.StringBuilder]::new()
    foreach ($record in $Records) {
        [void]$lines.Append($record.sha256)
        [void]$lines.Append(' ')
        [void]$lines.Append(([long]$record.size_bytes).ToString(
            [Globalization.CultureInfo]::InvariantCulture))
        [void]$lines.Append(' ')
        [void]$lines.Append($record.name)
        [void]$lines.Append("`n")
    }
    return Get-TextSha256 -Text $lines.ToString()
}

function New-Checks {
    foreach ($id in @(
        'package-sha256'
        'package-commit-identity'
        'package-core-identity'
        'authentication-success'
        'command-pty-sftp'
        'remote-local-cleanup'
        'negotiated-reconnect'
        'server-session-audit'
        'authentication-rejection'
        'host-key-rejection'
        'connection-cancellation'
        'transport-timeout'
    )) {
        [ordered]@{ id = $id; result = 'Pass' }
    }
}

function New-Measurements {
    return [ordered]@{
        check_count = 12
        passed_count = 12
        failed_count = 0
        blocked_count = 0
        package_sha256_verified = $true
        package_commit_identity_verified = $true
        package_core_identity_verified = $true
        authentication_success_verified = $true
        sftp_bytes = 64 * 1024
        sftp_checksum_verified = $true
        command_pty_sftp_verified = $true
        remote_cleanup_verified = $true
        local_cleanup_verified = $true
        reconnect_verified = $true
        audit_exec_count = 4
        audit_shell_count = 1
        audit_sftp_count = 2
        audit_other_count = 0
        server_audit_verified = $true
        authentication_rejection_verified = $true
        host_key_rejection_verified = $true
        cancellation_elapsed_milliseconds = 500
        cancellation_verified = $true
        timeout_elapsed_milliseconds = 15000
        timeout_verified = $true
    }
}

function New-PackageChecks {
    foreach ($id in @(
        'package-sha256'
        'package-commit-identity'
        'package-tree-identity'
        'ui-startup'
        'alt-navigation-silent'
        'ui-shutdown'
    )) {
        [ordered]@{ id = $id; result = 'Pass' }
    }
}

function New-PackageMeasurements {
    return [ordered]@{
        check_count = 6
        passed_count = 6
        failed_count = 0
        blocked_count = 0
        package_sha256_verified = $true
        package_commit_identity_verified = $true
        package_tree_identity_verified = $true
        ui_startup_verified = $true
        alt_navigation_silent_verified = $true
        ui_shutdown_verified = $true
        alt_navigation_shortcut_count = 7
    }
}

function New-Candidate {
    param([string]$Root)

    $candidateRoot = Join-Path $Root 'candidate'
    $packages = Join-Path $candidateRoot 'packages'
    [System.IO.Directory]::CreateDirectory($packages) | Out-Null
    $archives = @(
        "Sutty-$tag-win-x64.zip"
        "Sutty-$tag-win-arm64.zip"
    ) | Sort-Object
    foreach ($archive in $archives) {
        Set-Utf8Text -Path (Join-Path $packages $archive) -Content "fixture:$archive"
    }
    $checksumLines = @($archives | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath (Join-Path $packages $_) -Algorithm SHA256).
            Hash.ToLowerInvariant()
        "$hash  $_"
    })
    Set-Utf8Text `
        -Path (Join-Path $packages 'SHA256SUMS.txt') `
        -Content (($checksumLines -join [Environment]::NewLine) + [Environment]::NewLine)

    $manifest = Join-Path $candidateRoot 'CANDIDATE-MANIFEST.json'
    & (Join-Path (Split-Path -Parent $AttestationScript) 'Assert-AlphaCandidate.ps1') `
        -PackageDirectory $packages `
        -ManifestPath $manifest `
        -Repository $repository `
        -Tag $tag `
        -Commit $candidateCommit `
        -CandidateRunId $candidateRunId `
        -CandidateRunAttempt $candidateRunAttempt `
        -ArtifactName $artifactName `
        -WriteManifest *> $null
    return [pscustomobject]@{
        Root = $candidateRoot
        Packages = $packages
        Manifest = $manifest
    }
}

function New-ReviewedEvidence {
    param(
        [string]$RepositoryRoot,
        [string]$PackageSha256,
        [string]$RepositoryPath,
        [ValidateSet('SSH-LIVE-001', 'PKG-001')]
        [string]$GateId
    )

    $manifestRelativePath = $RepositoryPath.Replace(
        '/', [System.IO.Path]::DirectorySeparatorChar)
    $bundle = Join-Path $RepositoryRoot (Split-Path -Parent $manifestRelativePath)
    [System.IO.Directory]::CreateDirectory($bundle) | Out-Null
    $checks = if ($GateId -ceq 'PKG-001') {
        @(New-PackageChecks)
    }
    else {
        @(New-Checks)
    }
    $measurements = if ($GateId -ceq 'PKG-001') {
        New-PackageMeasurements
    }
    else {
        New-Measurements
    }
    $sourceSummaryObject = [ordered]@{
        schema_version = 1
        gate_id = $GateId
        result = 'Pass'
        started_at_utc = $startedAtUtc
        duration_seconds = 12
        checks = $checks
        measurements = $measurements
        redaction_reviewed = $false
        privacy_notice = $privacyNotice
    }
    $sourceSummary = ($sourceSummaryObject | ConvertTo-Json -Depth 8) + [Environment]::NewLine
    $sourceSummaryPattern = [regex]::new(
        '(?m)(^\s*"redaction_reviewed": )false(?=,?\r?$)')
    $summary = $sourceSummaryPattern.Replace($sourceSummary, '${1}true', 1)
    Set-Utf8Text -Path (Join-Path $bundle 'summary.json') -Content $summary

    $manifestTuple = if ($GateId -ceq 'PKG-001') {
        @(
            'server_family: "NotApplicable"'
            'server_version: "NotApplicable"'
            'route: "NotApplicable"'
            'authentication: "NotApplicable"'
            'expected_host_fingerprint: "NotRecorded"'
        )
    }
    else {
        @(
            'server_family: "OpenSSH"'
            'server_version: "9.6p1"'
            'route: "Direct"'
            'authentication: "Password"'
            'expected_host_fingerprint: "SHA256:[redacted]"'
        )
    }
    $sourceManifest = @(
        'schema_version: 1'
        "gate_id: `"$GateId`""
        "commit: `"$candidateCommit`""
        "package_sha256: `"$PackageSha256`""
        'windows_build: "10.0.26100.0"'
        'architecture: "x64"'
    ) + $manifestTuple + @(
        'result: "Pass"'
        "started_at_utc: `"$startedAtUtc`""
        'duration_seconds: 12'
        'evidence_files:'
        '  - "summary.json"'
        'redaction_reviewed: false'
        ''
    ) -join [Environment]::NewLine
    $sourceManifestPattern = [regex]::new('(?m)^redaction_reviewed: false\r?$')
    $manifestReplacement = '  - "review.json"' + [Environment]::NewLine +
        'redaction_reviewed: true'
    $manifest = $sourceManifestPattern.Replace($sourceManifest, $manifestReplacement, 1)
    $manifestPath = Join-Path $bundle 'manifest.yml'
    Set-Utf8Text -Path $manifestPath -Content $manifest

    # Review provenance binds the exact bytes before the two deterministic review-marker
    # transformations above. The validator reverses those transformations byte-for-byte.
    $sourceRecords = @(
        [ordered]@{
            name = 'manifest.yml'
            sha256 = Get-TextSha256 -Text $sourceManifest
            size_bytes = [System.Text.UTF8Encoding]::new($false).GetByteCount($sourceManifest)
        }
        [ordered]@{
            name = 'summary.json'
            sha256 = Get-TextSha256 -Text $sourceSummary
            size_bytes = [System.Text.UTF8Encoding]::new($false).GetByteCount($sourceSummary)
        }
    )
    $reviewObject = [ordered]@{
        schema_version = 1
        reviewer_id = 'github-reviewer1'
        reviewed_at_utc = $reviewedAtUtc
        source_bundle_sha256 = Get-CanonicalBundleDigest -Records $sourceRecords
        source_files = $sourceRecords
        review_scope = @('privacy-redaction', 'bundle-integrity')
    }
    if ($GateId -ceq 'PKG-001') {
        $reviewObject.manual_observation_confirmed = $true
    }
    $review = $reviewObject | ConvertTo-Json -Depth 6
    $reviewPath = Join-Path $bundle 'review.json'
    Set-Utf8Text -Path $reviewPath -Content ($review + [Environment]::NewLine)

    return [pscustomobject]@{
        Bundle = $bundle
        Manifest = $manifestPath
        Review = $reviewPath
    }
}

function New-Fixture {
    param([string]$Name)

    $root = Join-Path $scratch $Name
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    $candidate = New-Candidate -Root $root
    $x64Hash = (Get-FileHash `
        -LiteralPath (Join-Path $candidate.Packages "Sutty-$tag-win-x64.zip") `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $repo = Join-Path $root 'repo'
    [System.IO.Directory]::CreateDirectory($repo) | Out-Null
    $evidence = New-ReviewedEvidence `
        -RepositoryRoot $repo `
        -PackageSha256 $x64Hash `
        -RepositoryPath $evidenceRepositoryPath `
        -GateId SSH-LIVE-001
    $packageEvidence = New-ReviewedEvidence `
        -RepositoryRoot $repo `
        -PackageSha256 $x64Hash `
        -RepositoryPath $packageEvidenceRepositoryPath `
        -GateId PKG-001
    $attestation = Join-Path $root 'release\RELEASE-ATTESTATION.json'

    $fixture = [pscustomobject]@{
        Root = $root
        Candidate = $candidate
        RepositoryRoot = $repo
        Evidence = $evidence
        PackageEvidence = $packageEvidence
        Attestation = $attestation
    }
    Invoke-Attestation -Fixture $fixture -Write
    return $fixture
}

function Invoke-Attestation {
    param(
        [Parameter(Mandatory)]$Fixture,
        [string]$ExpectedRepository = $repository,
        [string]$ExpectedTag = $tag,
        [string]$ExpectedCandidateCommit = $candidateCommit,
        [string]$ExpectedCandidateRunId = $candidateRunId,
        [int]$ExpectedCandidateRunAttempt = $candidateRunAttempt,
        [string]$ExpectedArtifactId = $artifactId,
        [string]$ExpectedArtifactName = $artifactName,
        [string]$ExpectedArtifactDigest = $artifactDigest,
        [string]$ExpectedAcceptanceCommit = $acceptanceCommit,
        [string]$ExpectedEvidencePath = $evidenceRepositoryPath,
        [string]$ExpectedPackageEvidencePath = $packageEvidenceRepositoryPath,
        [string]$ExpectedPromotionRunId = $promotionRunId,
        [int]$ExpectedPromotionRunAttempt = $promotionRunAttempt,
        [switch]$Write
    )

    $arguments = @{
        AttestationPath = $Fixture.Attestation
        CandidateManifestPath = $Fixture.Candidate.Manifest
        RepositoryRoot = $Fixture.RepositoryRoot
        Repository = $ExpectedRepository
        Tag = $ExpectedTag
        CandidateCommit = $ExpectedCandidateCommit
        CandidateRunId = $ExpectedCandidateRunId
        CandidateRunAttempt = $ExpectedCandidateRunAttempt
        CandidateArtifactId = $ExpectedArtifactId
        CandidateArtifactName = $ExpectedArtifactName
        CandidateArtifactDigest = $ExpectedArtifactDigest
        AcceptanceCommit = $ExpectedAcceptanceCommit
        EvidenceManifestRepositoryPath = $ExpectedEvidencePath
        PackageEvidenceManifestRepositoryPath = $ExpectedPackageEvidencePath
        PromotionRunId = $ExpectedPromotionRunId
        PromotionRunAttempt = $ExpectedPromotionRunAttempt
    }
    if ($Write) {
        $arguments.WriteAttestation = $true
    }
    & $AttestationScript @arguments *> $null
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Name
    )

    $script:caseCount++
    $rejected = $false
    try {
        & $Action
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Release-attestation self-test failed: $Name was accepted."
    }
}

function Assert-Accepted {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Name
    )

    $script:caseCount++
    try {
        & $Action
    }
    catch {
        throw "Release-attestation self-test failed: $Name was rejected: $($_.Exception.Message)"
    }
}

function Set-JsonObject {
    param([string]$Path, [object]$Value)

    Set-Utf8Text -Path $Path -Content (($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
}

try {
    [System.IO.Directory]::CreateDirectory($scratch) | Out-Null

    $valid = New-Fixture 'valid'
    Assert-Accepted { Invoke-Attestation -Fixture $valid } 'valid strict release attestation'
    $validBytes = [System.IO.File]::ReadAllBytes($valid.Attestation)
    if ($validBytes.Length -ge 3 -and $validBytes[0] -eq 0xEF -and
        $validBytes[1] -eq 0xBB -and $validBytes[2] -eq 0xBF) {
        throw 'Release-attestation self-test failed: writer emitted a UTF-8 BOM.'
    }
    Assert-Rejected { Invoke-Attestation -Fixture $valid -Write } 'write-once overwrite attempt'
    Assert-Rejected {
        Invoke-Attestation `
            -Fixture $valid `
            -ExpectedPackageEvidencePath $evidenceRepositoryPath
    } 'package evidence cannot reuse the SSH evidence path'

    $wrongAlphaDirectory = New-Fixture 'wrong-alpha-directory'
    Remove-Item -LiteralPath $wrongAlphaDirectory.Attestation -Force
    $wrongAlphaRoot = Join-Path $wrongAlphaDirectory.RepositoryRoot 'docs\evidence\alpha5'
    Move-Item `
        -LiteralPath (Join-Path $wrongAlphaDirectory.RepositoryRoot 'docs\evidence\alpha4') `
        -Destination $wrongAlphaRoot
    Assert-Rejected {
        Invoke-Attestation `
            -Fixture $wrongAlphaDirectory `
            -ExpectedEvidencePath 'docs/evidence/alpha5/ssh-auth/ssh-live-001-reviewed/manifest.yml' `
            -ExpectedPackageEvidencePath 'docs/evidence/alpha5/package/pkg-001-reviewed/manifest.yml' `
            -Write
    } 'evidence directory does not match the Alpha tag suffix'

    $packageManifestMutation = New-Fixture 'post-review-package-evidence-mutation'
    $packageManifestText = Get-Content `
        -LiteralPath $packageManifestMutation.PackageEvidence.Manifest `
        -Raw
    Set-Utf8Text `
        -Path $packageManifestMutation.PackageEvidence.Manifest `
        -Content ($packageManifestText + [Environment]::NewLine)
    Assert-Rejected {
        Invoke-Attestation -Fixture $packageManifestMutation
    } 'post-review package evidence mutation'

    $packageAttestationHash = New-Fixture 'package-attestation-hash'
    $packageAttestationObject = Get-Content -LiteralPath $packageAttestationHash.Attestation -Raw |
        ConvertFrom-Json
    $packageAttestationObject.acceptance.package_evidence_manifest_sha256 = 'f' * 64
    Set-JsonObject `
        -Path $packageAttestationHash.Attestation `
        -Value $packageAttestationObject
    Assert-Rejected {
        Invoke-Attestation -Fixture $packageAttestationHash
    } 'package evidence attestation hash tamper'

    $packageManualConfirmation = New-Fixture 'package-manual-confirmation'
    Remove-Item -LiteralPath $packageManualConfirmation.Attestation -Force
    $packageReviewObject = Get-Content `
        -LiteralPath $packageManualConfirmation.PackageEvidence.Review `
        -Raw | ConvertFrom-Json
    $packageReviewObject.manual_observation_confirmed = $false
    Set-JsonObject `
        -Path $packageManualConfirmation.PackageEvidence.Review `
        -Value $packageReviewObject
    Assert-Rejected {
        Invoke-Attestation -Fixture $packageManualConfirmation -Write
    } 'package review without manual-observation confirmation'

    $postReviewMutation = New-Fixture 'post-review-evidence-mutation'
    Remove-Item -LiteralPath $postReviewMutation.Attestation -Force
    $manifestText = Get-Content -LiteralPath $postReviewMutation.Evidence.Manifest -Raw
    Set-Utf8Text `
        -Path $postReviewMutation.Evidence.Manifest `
        -Content $manifestText.Replace('duration_seconds: 12', 'duration_seconds: 13')
    $summaryObject = Get-Content `
        -LiteralPath (Join-Path $postReviewMutation.Evidence.Bundle 'summary.json') `
        -Raw | ConvertFrom-Json
    $summaryObject.duration_seconds = 13
    Set-JsonObject `
        -Path (Join-Path $postReviewMutation.Evidence.Bundle 'summary.json') `
        -Value $summaryObject
    Assert-Rejected {
        Invoke-Attestation -Fixture $postReviewMutation -Write
    } 'post-review evidence mutation with unchanged review provenance'

    $reviewBeforeRun = New-Fixture 'review-before-run-completion'
    Remove-Item -LiteralPath $reviewBeforeRun.Attestation -Force
    $reviewObject = Get-Content -LiteralPath $reviewBeforeRun.Evidence.Review -Raw |
        ConvertFrom-Json
    $reviewObject.reviewed_at_utc = '2000-01-01T00:00:00Z'
    Set-JsonObject -Path $reviewBeforeRun.Evidence.Review -Value $reviewObject
    Assert-Rejected {
        Invoke-Attestation -Fixture $reviewBeforeRun -Write
    } 'review timestamp before evidence completion'

    $futureReview = New-Fixture 'future-review-time'
    Remove-Item -LiteralPath $futureReview.Attestation -Force
    $futureReviewObject = Get-Content -LiteralPath $futureReview.Evidence.Review -Raw |
        ConvertFrom-Json
    $futureReviewObject.reviewed_at_utc = '9999-12-31T23:59:59Z'
    Set-JsonObject -Path $futureReview.Evidence.Review -Value $futureReviewObject
    Assert-Rejected {
        Invoke-Attestation -Fixture $futureReview -Write
    } 'review timestamp more than five minutes in the future'

    $tamperedArchive = New-Fixture 'tampered-archive'
    Add-Content `
        -LiteralPath (Join-Path $tamperedArchive.Candidate.Packages "Sutty-$tag-win-x64.zip") `
        -Value 'tampered'
    Assert-Rejected { Invoke-Attestation -Fixture $tamperedArchive } 'tampered candidate archive'

    $tamperedCandidateManifest = New-Fixture 'tampered-candidate-manifest'
    Add-Content -LiteralPath $tamperedCandidateManifest.Candidate.Manifest -Value ''
    Assert-Rejected {
        Invoke-Attestation -Fixture $tamperedCandidateManifest
    } 'candidate manifest hash tamper'

    $unknownRoot = New-Fixture 'unknown-root'
    $object = Get-Content -LiteralPath $unknownRoot.Attestation -Raw | ConvertFrom-Json
    $object | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
    Set-JsonObject -Path $unknownRoot.Attestation -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $unknownRoot } 'unknown attestation root property'

    $unknownNested = New-Fixture 'unknown-nested'
    $object = Get-Content -LiteralPath $unknownNested.Attestation -Raw | ConvertFrom-Json
    $object.candidate | Add-Member -NotePropertyName unexpected -NotePropertyValue 'value'
    Set-JsonObject -Path $unknownNested.Attestation -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $unknownNested } 'unknown nested attestation property'

    $duplicateRoot = New-Fixture 'duplicate-root'
    $text = Get-Content -LiteralPath $duplicateRoot.Attestation -Raw
    $text = $text.Replace('"schema_version": 1,', '"schema_version": 1, "schema_version": 1,')
    Set-Utf8Text -Path $duplicateRoot.Attestation -Content $text
    Assert-Rejected { Invoke-Attestation -Fixture $duplicateRoot } 'duplicate attestation root property'

    $duplicateNested = New-Fixture 'duplicate-nested'
    $text = Get-Content -LiteralPath $duplicateNested.Attestation -Raw
    $text = [regex]::Replace(
        $text,
        '"commit":\s*"[0-9a-f]{40}"',
        '$0, "commit": "0123456789abcdef0123456789abcdef01234567"',
        1)
    Set-Utf8Text -Path $duplicateNested.Attestation -Content $text
    Assert-Rejected { Invoke-Attestation -Fixture $duplicateNested } 'recursive duplicate property'

    $hashMismatch = New-Fixture 'hash-mismatch'
    $object = Get-Content -LiteralPath $hashMismatch.Attestation -Raw | ConvertFrom-Json
    $object.files[0].sha256 = 'c' * 64
    Set-JsonObject -Path $hashMismatch.Attestation -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $hashMismatch } 'attested file hash mismatch'

    $sizeMismatch = New-Fixture 'size-mismatch'
    $object = Get-Content -LiteralPath $sizeMismatch.Attestation -Raw | ConvertFrom-Json
    $object.files[1].size_bytes = [long]$object.files[1].size_bytes + 1
    Set-JsonObject -Path $sizeMismatch.Attestation -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $sizeMismatch } 'attested file size mismatch'

    $wrongType = New-Fixture 'wrong-type'
    $object = Get-Content -LiteralPath $wrongType.Attestation -Raw | ConvertFrom-Json
    $object.promotion.run_attempt = '3'
    Set-JsonObject -Path $wrongType.Attestation -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $wrongType } 'numeric identity encoded as string'

    $wrongIdentity = New-Fixture 'wrong-identity'
    Assert-Rejected {
        Invoke-Attestation `
            -Fixture $wrongIdentity `
            -ExpectedAcceptanceCommit 'fedcba9876543210fedcba9876543210fedcba98'
    } 'acceptance commit identity mismatch'
    Assert-Rejected {
        Invoke-Attestation -Fixture $wrongIdentity -ExpectedArtifactId '111222333'
    } 'candidate artifact identity mismatch'
    Assert-Rejected {
        Invoke-Attestation `
            -Fixture $wrongIdentity `
            -ExpectedArtifactDigest ('sha256:' + ('e' * 64))
    } 'candidate artifact digest identity mismatch'
    Assert-Rejected {
        Invoke-Attestation -Fixture $wrongIdentity -ExpectedPromotionRunId '9988776655'
    } 'promotion run identity mismatch'

    $manifestHash = New-Fixture 'evidence-manifest-hash'
    $text = Get-Content -LiteralPath $manifestHash.Evidence.Manifest -Raw
    Set-Utf8Text `
        -Path $manifestHash.Evidence.Manifest `
        -Content ($text + [Environment]::NewLine)
    Assert-Rejected { Invoke-Attestation -Fixture $manifestHash } 'reviewed manifest hash tamper'

    $reviewHash = New-Fixture 'review-hash'
    $text = Get-Content -LiteralPath $reviewHash.Evidence.Review -Raw
    Set-Utf8Text -Path $reviewHash.Evidence.Review -Content ($text + [Environment]::NewLine)
    Assert-Rejected { Invoke-Attestation -Fixture $reviewHash } 'review file hash tamper'

    $reviewUnknown = New-Fixture 'review-unknown'
    $object = Get-Content -LiteralPath $reviewUnknown.Evidence.Review -Raw | ConvertFrom-Json
    $object | Add-Member -NotePropertyName note -NotePropertyValue 'unexpected'
    Set-JsonObject -Path $reviewUnknown.Evidence.Review -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $reviewUnknown } 'unknown review property'

    $reviewDuplicate = New-Fixture 'review-duplicate'
    $text = Get-Content -LiteralPath $reviewDuplicate.Evidence.Review -Raw
    $text = $text.Replace('"name": "manifest.yml",',
        '"name": "manifest.yml", "name": "manifest.yml",')
    Set-Utf8Text -Path $reviewDuplicate.Evidence.Review -Content $text
    Assert-Rejected { Invoke-Attestation -Fixture $reviewDuplicate } 'duplicate nested review property'

    $reviewDigest = New-Fixture 'review-digest'
    $object = Get-Content -LiteralPath $reviewDigest.Evidence.Review -Raw | ConvertFrom-Json
    $object.source_bundle_sha256 = 'd' * 64
    Set-JsonObject -Path $reviewDigest.Evidence.Review -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $reviewDigest } 'review source bundle hash mismatch'

    $reviewScope = New-Fixture 'review-scope'
    $object = Get-Content -LiteralPath $reviewScope.Evidence.Review -Raw | ConvertFrom-Json
    $object.review_scope = @('bundle-integrity', 'privacy-redaction')
    Set-JsonObject -Path $reviewScope.Evidence.Review -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $reviewScope } 'review scope order mismatch'

    $reviewer = New-Fixture 'reviewer'
    $object = Get-Content -LiteralPath $reviewer.Evidence.Review -Raw | ConvertFrom-Json
    $object.reviewer_id = 'github-reviewer-'
    Set-JsonObject -Path $reviewer.Evidence.Review -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $reviewer } 'invalid reviewer identity'

    $reviewTimestamp = New-Fixture 'review-timestamp'
    $object = Get-Content -LiteralPath $reviewTimestamp.Evidence.Review -Raw | ConvertFrom-Json
    $object.reviewed_at_utc = '2026-08-21T10:02:03+09:00'
    Set-JsonObject -Path $reviewTimestamp.Evidence.Review -Value $object
    Assert-Rejected { Invoke-Attestation -Fixture $reviewTimestamp } 'non-UTC review timestamp'

    $wrongGate = New-Fixture 'wrong-gate'
    $manifestText = Get-Content -LiteralPath $wrongGate.Evidence.Manifest -Raw
    $manifestText = $manifestText.Replace('SSH-LIVE-001', 'SSH-INFO-001')
    Set-Utf8Text -Path $wrongGate.Evidence.Manifest -Content $manifestText
    Assert-Rejected { Invoke-Attestation -Fixture $wrongGate } 'wrong reviewed gate identity'

    $unreviewed = New-Fixture 'unreviewed'
    $manifestText = Get-Content -LiteralPath $unreviewed.Evidence.Manifest -Raw
    $manifestText = $manifestText.Replace('redaction_reviewed: true', 'redaction_reviewed: false')
    Set-Utf8Text -Path $unreviewed.Evidence.Manifest -Content $manifestText
    Assert-Rejected { Invoke-Attestation -Fixture $unreviewed } 'unreviewed evidence manifest'

    $attestationBom = New-Fixture 'attestation-bom'
    $text = Get-Content -LiteralPath $attestationBom.Attestation -Raw
    [System.IO.File]::WriteAllText(
        $attestationBom.Attestation,
        $text,
        [System.Text.UTF8Encoding]::new($true))
    Assert-Rejected { Invoke-Attestation -Fixture $attestationBom } 'attestation UTF-8 BOM'

    $reviewBom = New-Fixture 'review-bom'
    $text = Get-Content -LiteralPath $reviewBom.Evidence.Review -Raw
    [System.IO.File]::WriteAllText(
        $reviewBom.Evidence.Review,
        $text,
        [System.Text.UTF8Encoding]::new($true))
    Assert-Rejected { Invoke-Attestation -Fixture $reviewBom } 'review UTF-8 BOM'

    $attestationUtf8 = New-Fixture 'attestation-invalid-utf8'
    [System.IO.File]::WriteAllBytes($attestationUtf8.Attestation, [byte[]](0xff, 0xfe, 0xfd))
    Assert-Rejected { Invoke-Attestation -Fixture $attestationUtf8 } 'invalid attestation UTF-8'

    $reviewUtf8 = New-Fixture 'review-invalid-utf8'
    [System.IO.File]::WriteAllBytes($reviewUtf8.Evidence.Review, [byte[]](0xff, 0xfe, 0xfd))
    Assert-Rejected { Invoke-Attestation -Fixture $reviewUtf8 } 'invalid review UTF-8'

    Write-Host "Release-attestation guard self-tests passed ($caseCount cases)."
}
finally {
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ((Test-Path -LiteralPath $resolvedScratch) -and
        $resolvedScratch.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith(
            'sutty-release-attestation-tests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
