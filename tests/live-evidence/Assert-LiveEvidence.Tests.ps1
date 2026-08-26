param(
    [string]$Validator = (Resolve-Path (Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-LiveEvidence.ps1')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = Join-Path $temporaryBase "sutty-live-evidence-tests-$([Guid]::NewGuid().ToString('N'))"
$commit = '0123456789abcdef0123456789abcdef01234567'
$packageSha256 = 'abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789'
$privacyNotice =
    'Connection identifiers, credentials, filesystem locations, session content, and cryptographic material are excluded.'
$packageWriter = (Resolve-Path (
    Join-Path $PSScriptRoot '..\..\.github\scripts\Write-PackageEvidence.ps1')).Path
$evidenceReviewer = (Resolve-Path (
    Join-Path $PSScriptRoot '..\..\.github\scripts\Review-LiveEvidence.ps1')).Path
$caseCount = 0

function Set-Utf8Text {
    param(
        [string]$Path,
        [string]$Content
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Copy-FixtureTree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($directory in [IO.Directory]::EnumerateDirectories(
            $Source,
            '*',
            [IO.SearchOption]::AllDirectories)) {
        $relativePath = [IO.Path]::GetRelativePath($Source, $directory)
        [IO.Directory]::CreateDirectory((Join-Path $Destination $relativePath)) | Out-Null
    }
    foreach ($file in [IO.Directory]::EnumerateFiles(
            $Source,
            '*',
            [IO.SearchOption]::AllDirectories)) {
        $relativePath = [IO.Path]::GetRelativePath($Source, $file)
        $destinationPath = Join-Path $Destination $relativePath
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destinationPath)) | Out-Null
        [IO.File]::Copy($file, $destinationPath, $false)
    }
}

function ConvertTo-QuotedScalar {
    param([string]$Value)

    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function Add-PngChunkBeforeIend {
    param(
        [byte[]]$Png,
        [string]$Type,
        [byte[]]$Data,
        [switch]$AfterIhdr
    )

    if ($Type.Length -ne 4 -or $Png.Length -lt 12 -or
        [System.Text.Encoding]::ASCII.GetString($Png, $Png.Length - 8, 4) -cne 'IEND') {
        throw 'PNG fixture does not end in a canonical IEND chunk.'
    }
    $chunk = [byte[]]::new(12 + $Data.Length)
    $chunk[0] = [byte](($Data.Length -shr 24) -band 0xff)
    $chunk[1] = [byte](($Data.Length -shr 16) -band 0xff)
    $chunk[2] = [byte](($Data.Length -shr 8) -band 0xff)
    $chunk[3] = [byte]($Data.Length -band 0xff)
    $typeBytes = [System.Text.Encoding]::ASCII.GetBytes($Type)
    [Array]::Copy($typeBytes, 0, $chunk, 4, 4)
    [Array]::Copy($Data, 0, $chunk, 8, $Data.Length)
    $crc = [SuttyLiveEvidencePngCrc]::Compute($chunk, 4, 4 + $Data.Length)
    $crcOffset = 8 + $Data.Length
    $chunk[$crcOffset] = [byte](($crc -shr 24) -band 0xff)
    $chunk[$crcOffset + 1] = [byte](($crc -shr 16) -band 0xff)
    $chunk[$crcOffset + 2] = [byte](($crc -shr 8) -band 0xff)
    $chunk[$crcOffset + 3] = [byte]($crc -band 0xff)

    $iendOffset = if ($AfterIhdr) { 33 } else { $Png.Length - 12 }
    $result = [byte[]]::new($Png.Length + $chunk.Length)
    [Array]::Copy($Png, 0, $result, 0, $iendOffset)
    [Array]::Copy($chunk, 0, $result, $iendOffset, $chunk.Length)
    [Array]::Copy(
        $Png,
        $iendOffset,
        $result,
        $iendOffset + $chunk.Length,
        $Png.Length - $iendOffset)
    return $result
}

function New-CanonicalSummary {
    param(
        [string]$GateId = 'SSH-PRIMARY-NOEXEC',
        [string]$Result = 'Pass',
        [string]$StartedAtUtc = '2026-08-20T01:02:03.123Z',
        [long]$DurationSeconds = 12,
        [bool]$RedactionReviewed = $true,
        [object[]]$Checks,
        [hashtable]$AdditionalProperties = @{}
    )

    if ($null -eq $Checks) {
        $Checks = @([ordered]@{ id = 'smoke'; result = $Result })
    }
    $summary = [ordered]@{
        schema_version = 1
        gate_id = $GateId
        result = $Result
        started_at_utc = $StartedAtUtc
        duration_seconds = $DurationSeconds
        checks = $Checks
        redaction_reviewed = $RedactionReviewed
        privacy_notice = $privacyNotice
    }
    foreach ($key in $AdditionalProperties.Keys) {
        $summary[$key] = $AdditionalProperties[$key]
    }
    return $summary | ConvertTo-Json -Depth 10
}

function New-SshLive001Checks {
    foreach ($id in @(
        'package-sha256',
        'package-commit-identity',
        'package-core-identity',
        'authentication-success',
        'command-pty-sftp',
        'remote-local-cleanup',
        'negotiated-reconnect',
        'server-session-audit',
        'authentication-rejection',
        'host-key-rejection',
        'connection-cancellation',
        'transport-timeout')) {
        [ordered]@{ id = $id; result = 'Pass' }
    }
}

function New-SshLive001Measurements {
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

function New-SshLive001Summary {
    param([object[]]$Checks = @(New-SshLive001Checks))

    return New-CanonicalSummary `
        -GateId 'SSH-LIVE-001' `
        -Checks $Checks `
        -AdditionalProperties @{ measurements = (New-SshLive001Measurements) }
}

function New-Pkg001Checks {
    foreach ($id in @(
        'package-sha256',
        'package-commit-identity',
        'package-tree-identity',
        'ui-startup',
        'alt-navigation-silent',
        'ui-shutdown')) {
        [ordered]@{ id = $id; result = 'Pass' }
    }
}

function New-Pkg001Measurements {
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

function New-Pkg001Summary {
    param(
        [object[]]$Checks = @(New-Pkg001Checks),
        [object]$Measurements = (New-Pkg001Measurements)
    )

    return New-CanonicalSummary `
        -GateId 'PKG-001' `
        -Checks $Checks `
        -AdditionalProperties @{ measurements = $Measurements }
}

function New-CanonicalReview {
    param(
        [string[]]$SourceNames,
        [string]$Bundle,
        [string]$SourceManifestText,
        [string]$SourceSummaryText,
        [string]$GateId
    )

    $sortedNames = @($SourceNames)
    [Array]::Sort($sortedNames, [StringComparer]::Ordinal)
    $records = [System.Collections.Generic.List[object]]::new()
    $canonicalRecords = [Text.StringBuilder]::new()
    foreach ($name in $sortedNames) {
        $sourceBytes = switch ($name) {
            'manifest.yml' {
                [System.Text.UTF8Encoding]::new($false).GetBytes($SourceManifestText)
                break
            }
            'summary.json' {
                [System.Text.UTF8Encoding]::new($false).GetBytes($SourceSummaryText)
                break
            }
            default {
                $sourcePath = Join-Path $Bundle $name.Replace(
                    '/',
                    [System.IO.Path]::DirectorySeparatorChar)
                if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
                    [System.IO.File]::ReadAllBytes($sourcePath)
                }
                else {
                    [System.Text.UTF8Encoding]::new($false).GetBytes("missing source fixture $name")
                }
                break
            }
        }
        $sha256 = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData($sourceBytes)).ToLowerInvariant()
        $sizeBytes = [long]$sourceBytes.Length
        $records.Add([ordered]@{
            name = $name
            sha256 = $sha256
            size_bytes = $sizeBytes
        })
        $canonicalRecords.Append("$sha256 $sizeBytes $name`n") | Out-Null
    }
    $bundleDigestHex = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.UTF8Encoding]::new($false).GetBytes($canonicalRecords.ToString())))
    $bundleDigest = $bundleDigestHex.ToLowerInvariant()

    $review = [ordered]@{
        schema_version = 1
        reviewer_id = 'github-sutty-reviewer'
        reviewed_at_utc = '2026-08-20T02:03:04Z'
        source_bundle_sha256 = $bundleDigest
        source_files = $records
        review_scope = @('privacy-redaction', 'bundle-integrity')
    }
    if ($GateId -ceq 'PKG-001') {
        $review.manual_observation_confirmed = $true
    }
    return $review | ConvertTo-Json -Depth 10 -Compress
}

function Set-CanonicalFixtureReview {
    param([Parameter(Mandatory)][hashtable]$Fixture)

    Set-Utf8Text `
        -Path (Join-Path $Fixture.Bundle 'review.json') `
        -Content (New-CanonicalReview `
            -SourceNames $Fixture.SourceNames `
            -Bundle $Fixture.Bundle `
            -SourceManifestText $Fixture.SourceManifestText `
            -SourceSummaryText $Fixture.SourceSummaryText `
            -GateId $Fixture.GateId)
}

function New-EvidenceBundle {
    param(
        [string]$Name,
        [hashtable]$Overrides = @{},
        [string[]]$Omit = @(),
        [string[]]$EvidenceFiles = @('summary.json'),
        [AllowNull()]
        [string]$Summary,
        [string]$RelativeBundle,
        [switch]$SkipSummary,
        [switch]$SkipReview
    )

    $root = Join-Path $scratch $Name
    if ([string]::IsNullOrWhiteSpace($RelativeBundle)) {
        $RelativeBundle = "alpha4/ssh-auth/$Name"
    }
    Set-Utf8Text -Path (Join-Path $root 'EVIDENCE_SCHEMA.md') -Content '# Fixture evidence schema.'
    Set-Utf8Text -Path (Join-Path $root 'alpha4/README.md') -Content '# Fixture Alpha 4 index.'
    $relativeSegments = @($RelativeBundle.Split('/'))
    if ($relativeSegments.Count -ne 3 -or $relativeSegments[0] -cne 'alpha4') {
        $scopeName = 'ssh-auth'
    }
    else {
        $scopeName = $relativeSegments[1]
    }
    Set-Utf8Text `
        -Path (Join-Path $root "alpha4/$scopeName/README.md") `
        -Content '# Fixture scope index.'
    $bundle = Join-Path $root $RelativeBundle.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path $bundle -Force | Out-Null

    $fields = [ordered]@{
        schema_version = '1'
        gate_id = 'SSH-PRIMARY-NOEXEC'
        commit = $commit
        package_sha256 = $packageSha256
        windows_build = '10.0.26100.0'
        architecture = 'x64'
        server_family = 'OpenSSH'
        server_version = '9.6p1'
        route = 'Direct'
        authentication = 'PublicKey'
        expected_host_fingerprint = 'SHA256:[redacted]'
        result = 'Pass'
        started_at_utc = '2026-08-20T01:02:03.123Z'
        duration_seconds = '12'
        redaction_reviewed = 'true'
    }
    foreach ($key in $Overrides.Keys) {
        $fields[$key] = [string]$Overrides[$key]
    }
    $declaredEvidenceFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($evidenceFile in $EvidenceFiles) {
        $declaredEvidenceFiles.Add($evidenceFile)
    }
    if (-not $SkipReview -and -not $declaredEvidenceFiles.Contains('review.json')) {
        $declaredEvidenceFiles.Add('review.json')
    }
    if (-not $PSBoundParameters.ContainsKey('Summary')) {
        $summaryDuration = [long]0
        [long]::TryParse($fields.duration_seconds, [ref]$summaryDuration) | Out-Null
        $Summary = New-CanonicalSummary `
            -GateId $fields.gate_id `
            -Result $fields.result `
            -StartedAtUtc $fields.started_at_utc `
            -DurationSeconds $summaryDuration `
            -RedactionReviewed:($fields.redaction_reviewed -ceq 'true')
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $fields.GetEnumerator()) {
        if ($entry.Key -cin $Omit -or $entry.Key -ceq 'redaction_reviewed') {
            continue
        }
        if ($entry.Key -cin @('schema_version', 'duration_seconds')) {
            $lines.Add("$($entry.Key): $($entry.Value)")
        }
        else {
            $lines.Add("$($entry.Key): $(ConvertTo-QuotedScalar $entry.Value)")
        }
    }
    if ('evidence_files' -cnotin $Omit) {
        $lines.Add('evidence_files:')
        foreach ($evidenceFile in $declaredEvidenceFiles) {
            $lines.Add("  - $(ConvertTo-QuotedScalar $evidenceFile)")
        }
    }
    if ('redaction_reviewed' -cnotin $Omit) {
        $lines.Add("redaction_reviewed: $($fields.redaction_reviewed)")
    }

    $sourceNames = @('manifest.yml') + @(
        $declaredEvidenceFiles | Where-Object { $_ -cne 'review.json' })
    $canonicalPromotionShape = -not $SkipReview -and
        $fields.redaction_reviewed -ceq 'true' -and
        @($declaredEvidenceFiles | Where-Object { $_ -ceq 'review.json' }).Count -eq 1 -and
        $declaredEvidenceFiles[$declaredEvidenceFiles.Count - 1] -ceq 'review.json' -and
        'redaction_reviewed' -cnotin $Omit -and
        'evidence_files' -cnotin $Omit

    $sourceManifestText = $null
    if ($canonicalPromotionShape) {
        $sourceLines = [System.Collections.Generic.List[string]]::new()
        foreach ($entry in $fields.GetEnumerator()) {
            if ($entry.Key -cin $Omit -or $entry.Key -ceq 'redaction_reviewed') {
                continue
            }
            if ($entry.Key -cin @('schema_version', 'duration_seconds')) {
                $sourceLines.Add("$($entry.Key): $($entry.Value)")
            }
            else {
                $sourceLines.Add("$($entry.Key): $(ConvertTo-QuotedScalar $entry.Value)")
            }
        }
        $sourceLines.Add('evidence_files:')
        foreach ($evidenceFile in $declaredEvidenceFiles) {
            if ($evidenceFile -cne 'review.json') {
                $sourceLines.Add("  - $(ConvertTo-QuotedScalar $evidenceFile)")
            }
        }
        $sourceLines.Add('redaction_reviewed: false')
        $sourceManifestText = [string]::Join([Environment]::NewLine, $sourceLines) +
            [Environment]::NewLine
        $sourcePattern = [regex]::new('(?m)^redaction_reviewed: false\r?$')
        $replacement = '  - "review.json"' + [Environment]::NewLine +
            'redaction_reviewed: true'
        $manifestText = $sourcePattern.Replace($sourceManifestText, $replacement, 1)
    }
    else {
        $manifestText = [string]::Join([Environment]::NewLine, $lines) +
            [Environment]::NewLine
        $sourceManifestText = $manifestText
    }
    $manifestPath = Join-Path $bundle 'manifest.yml'
    Set-Utf8Text -Path $manifestPath -Content $manifestText

    $sourceSummaryText = $Summary
    $reviewedSummaryPattern = [regex]::new(
        '(?m)(^\s*"redaction_reviewed": )true(?=,?\r?$)')
    if (@($reviewedSummaryPattern.Matches($Summary)).Count -eq 1) {
        $sourceSummaryText = $reviewedSummaryPattern.Replace($Summary, '${1}false', 1)
    }
    if (-not $SkipSummary) {
        Set-Utf8Text -Path (Join-Path $bundle 'summary.json') -Content $Summary
    }
    if (-not $SkipReview) {
        Set-Utf8Text `
            -Path (Join-Path $bundle 'review.json') `
            -Content (New-CanonicalReview `
                -SourceNames $sourceNames `
                -Bundle $bundle `
                -SourceManifestText $sourceManifestText `
                -SourceSummaryText $sourceSummaryText `
                -GateId $fields.gate_id)
    }

    return @{
        Root = $root
        Bundle = $bundle
        Manifest = $manifestPath
        SourceNames = $sourceNames
        SourceManifestText = $sourceManifestText
        SourceSummaryText = $sourceSummaryText
        GateId = $fields.gate_id
    }
}

function Get-ValidationFailure {
    param(
        [string]$Root,
        [string]$Manifest,
        [string]$ExpectedCommit,
        [string]$ExpectedPackageSha256,
        [string]$RequiredGateId,
        [string]$RequiredResult
    )

    try {
        if (-not [string]::IsNullOrWhiteSpace($Manifest)) {
            $arguments = @{ ManifestPath = $Manifest }
            if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
                $arguments.ExpectedCommit = $ExpectedCommit
            }
            if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageSha256)) {
                $arguments.ExpectedPackageSha256 = $ExpectedPackageSha256
            }
            if (-not [string]::IsNullOrWhiteSpace($RequiredGateId)) {
                $arguments.RequiredGateId = $RequiredGateId
            }
            if (-not [string]::IsNullOrWhiteSpace($RequiredResult)) {
                $arguments.RequiredResult = $RequiredResult
            }
            & $Validator @arguments *> $null
        }
        else {
            & $Validator -EvidenceRoot $Root *> $null
        }
        return $null
    }
    catch {
        return $_.Exception.Message
    }
}

function Assert-Accepted {
    param(
        [hashtable]$Fixture,
        [string]$Name,
        [switch]$Direct,
        [string]$RequiredGateId,
        [string]$RequiredResult
    )

    $script:caseCount++
    if ($Direct) {
        $failure = Get-ValidationFailure `
            -Manifest $Fixture.Manifest `
            -ExpectedCommit $commit `
            -ExpectedPackageSha256 $packageSha256 `
            -RequiredGateId $RequiredGateId `
            -RequiredResult $RequiredResult
    }
    else {
        $failure = Get-ValidationFailure -Root $Fixture.Root
    }
    if ($null -ne $failure) {
        throw "Live-evidence fixture should pass ($Name): $failure"
    }
}

function Assert-Rejected {
    param(
        [hashtable]$Fixture,
        [string]$Name,
        [switch]$Direct,
        [string]$ExpectedCommit,
        [string]$ExpectedPackageSha256,
        [string]$RequiredGateId,
        [string]$RequiredResult
    )

    $script:caseCount++
    if ($Direct) {
        $failure = Get-ValidationFailure `
            -Manifest $Fixture.Manifest `
            -ExpectedCommit $ExpectedCommit `
            -ExpectedPackageSha256 $ExpectedPackageSha256 `
            -RequiredGateId $RequiredGateId `
            -RequiredResult $RequiredResult
    }
    else {
        $failure = Get-ValidationFailure -Root $Fixture.Root
    }
    if ($null -eq $failure) {
        throw "Live-evidence fixture should be rejected: $Name"
    }
}

function Assert-ActionRejected {
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
        throw "Live-evidence action should be rejected: $Name"
    }
}

try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    $emptyRoot = Join-Path $scratch 'empty-root'
    New-Item -ItemType Directory -Path $emptyRoot -Force | Out-Null
    Set-Utf8Text -Path (Join-Path $emptyRoot 'EVIDENCE_SCHEMA.md') -Content '# Schema only; not evidence.'
    $caseCount++
    if ($null -ne (Get-ValidationFailure -Root $emptyRoot)) {
        throw 'Schema documentation without committed manifests must pass.'
    }

    $canonical = New-EvidenceBundle 'canonical'
    Assert-Accepted -Fixture $canonical -Name 'canonical quoted YAML and JSON bundle'

    $postReviewMutation = New-EvidenceBundle 'post-review-mutation'
    $mutatedManifest = Get-Content -LiteralPath $postReviewMutation.Manifest -Raw
    Set-Utf8Text `
        -Path $postReviewMutation.Manifest `
        -Content $mutatedManifest.Replace('duration_seconds: 12', 'duration_seconds: 13')
    $mutatedSummary = Get-Content `
        -LiteralPath (Join-Path $postReviewMutation.Bundle 'summary.json') `
        -Raw | ConvertFrom-Json
    $mutatedSummary.duration_seconds = 13
    Set-Utf8Text `
        -Path (Join-Path $postReviewMutation.Bundle 'summary.json') `
        -Content ($mutatedSummary | ConvertTo-Json -Depth 10)
    Assert-Rejected `
        -Fixture $postReviewMutation `
        -Name 'post-review manifest and summary mutation with stale source hashes'

    $reviewBeforeRun = New-EvidenceBundle 'review-before-run'
    $reviewBeforeRunObject = Get-Content `
        -LiteralPath (Join-Path $reviewBeforeRun.Bundle 'review.json') `
        -Raw | ConvertFrom-Json
    $reviewBeforeRunObject.reviewed_at_utc = '2000-01-01T00:00:00Z'
    Set-Utf8Text `
        -Path (Join-Path $reviewBeforeRun.Bundle 'review.json') `
        -Content ($reviewBeforeRunObject | ConvertTo-Json -Depth 10 -Compress)
    Assert-Rejected -Fixture $reviewBeforeRun -Name 'review timestamp before evidence completion'

    $futureReview = New-EvidenceBundle 'future-review'
    $futureReviewObject = Get-Content `
        -LiteralPath (Join-Path $futureReview.Bundle 'review.json') `
        -Raw | ConvertFrom-Json
    $futureReviewObject.reviewed_at_utc = '9999-12-31T23:59:59Z'
    Set-Utf8Text `
        -Path (Join-Path $futureReview.Bundle 'review.json') `
        -Content ($futureReviewObject | ConvertTo-Json -Depth 10 -Compress)
    Assert-Rejected -Fixture $futureReview -Name 'review timestamp more than five minutes in the future'

    $missingReview = New-EvidenceBundle 'missing-review' -SkipReview
    Assert-Rejected -Fixture $missingReview -Name 'reviewed evidence without review.json'

    $reviewNotLast = New-EvidenceBundle `
        'review-not-last' `
        -EvidenceFiles @('summary.json', 'review.json', 'detail.json')
    Set-Utf8Text -Path (Join-Path $reviewNotLast.Bundle 'detail.json') -Content '{}'
    Assert-Rejected -Fixture $reviewNotLast -Name 'review.json is not the final declared evidence file'

    $invalidReview = New-EvidenceBundle 'invalid-review-contract'
    Set-Utf8Text -Path (Join-Path $invalidReview.Bundle 'review.json') -Content '{"schema_version":1}'
    Assert-Rejected -Fixture $invalidReview -Name 'incomplete review contract'

    $badReviewDigest = New-EvidenceBundle 'review-digest-mismatch'
    $badReviewObject = Get-Content -LiteralPath (Join-Path $badReviewDigest.Bundle 'review.json') -Raw |
        ConvertFrom-Json
    $badReviewObject.source_bundle_sha256 = 'f' * 64
    Set-Utf8Text `
        -Path (Join-Path $badReviewDigest.Bundle 'review.json') `
        -Content ($badReviewObject | ConvertTo-Json -Depth 10 -Compress)
    Assert-Rejected -Fixture $badReviewDigest -Name 'review source-bundle digest mismatch'

    $badReviewer = New-EvidenceBundle 'reviewer-trailing-hyphen'
    $badReviewerObject = Get-Content -LiteralPath (Join-Path $badReviewer.Bundle 'review.json') -Raw |
        ConvertFrom-Json
    $badReviewerObject.reviewer_id = 'github-reviewer-'
    Set-Utf8Text `
        -Path (Join-Path $badReviewer.Bundle 'review.json') `
        -Content ($badReviewerObject | ConvertTo-Json -Depth 10 -Compress)
    Assert-Rejected -Fixture $badReviewer -Name 'reviewer identifier trailing hyphen'

    $badReviewScope = New-EvidenceBundle 'review-scope-order'
    $badReviewScopeObject = Get-Content -LiteralPath (Join-Path $badReviewScope.Bundle 'review.json') -Raw |
        ConvertFrom-Json
    $badReviewScopeObject.review_scope = @('bundle-integrity', 'privacy-redaction')
    Set-Utf8Text `
        -Path (Join-Path $badReviewScope.Bundle 'review.json') `
        -Content ($badReviewScopeObject | ConvertTo-Json -Depth 10 -Compress)
    Assert-Rejected -Fixture $badReviewScope -Name 'review scope order'

    $badSourceOrder = New-EvidenceBundle 'review-source-order'
    $badSourceOrderObject = Get-Content -LiteralPath (Join-Path $badSourceOrder.Bundle 'review.json') -Raw |
        ConvertFrom-Json
    [Array]::Reverse($badSourceOrderObject.source_files)
    Set-Utf8Text `
        -Path (Join-Path $badSourceOrder.Bundle 'review.json') `
        -Content ($badSourceOrderObject | ConvertTo-Json -Depth 10 -Compress)
    Assert-Rejected -Fixture $badSourceOrder -Name 'review source_files ordinal order'

    $extraReviewProperty = New-EvidenceBundle 'review-extra-property'
    $extraReviewObject = Get-Content -LiteralPath (Join-Path $extraReviewProperty.Bundle 'review.json') -Raw |
        ConvertFrom-Json
    $extraReviewObject | Add-Member -NotePropertyName automation_passed -NotePropertyValue $true
    Set-Utf8Text `
        -Path (Join-Path $extraReviewProperty.Bundle 'review.json') `
        -Content ($extraReviewObject | ConvertTo-Json -Depth 10 -Compress)
    Assert-Rejected -Fixture $extraReviewProperty -Name 'extra review root property'
    Assert-Accepted -Fixture $canonical -Name 'expected commit, package, and Pass binding' -Direct -RequiredResult Pass
    $releaseGateSummary = New-SshLive001Summary
    $releaseGate = New-EvidenceBundle `
        'release-gate' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary $releaseGateSummary
    Assert-Accepted `
        -Fixture $releaseGate `
        -Name 'exact SSH-LIVE-001 release gate binding' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
    Assert-Accepted `
        -Fixture $releaseGate `
        -Name 'root scan enforces and accepts canonical SSH-LIVE-001 Pass profile'
    $releaseGateMissingMeasurements = New-EvidenceBundle `
        'release-gate-missing-measurements' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary (New-CanonicalSummary `
            -GateId 'SSH-LIVE-001' `
            -Checks @(New-SshLive001Checks))
    Assert-Rejected `
        -Fixture $releaseGateMissingMeasurements `
        -Name 'SSH-LIVE-001 Pass without measurements does not satisfy release gate' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
    Assert-Rejected `
        -Fixture $releaseGateMissingMeasurements `
        -Name 'root scan rejects SSH-LIVE-001 Pass without measurements'

    foreach ($measurementCase in @(
        @{ Name = 'false verification'; Field = 'package_sha256_verified'; Value = $false },
        @{ Name = 'wrong transfer size'; Field = 'sftp_bytes'; Value = 65535 },
        @{ Name = 'wrong audit count'; Field = 'audit_exec_count'; Value = 3 },
        @{ Name = 'short cancellation'; Field = 'cancellation_elapsed_milliseconds'; Value = 99 },
        @{ Name = 'long cancellation'; Field = 'cancellation_elapsed_milliseconds'; Value = 10000 },
        @{ Name = 'short timeout'; Field = 'timeout_elapsed_milliseconds'; Value = 11999 },
        @{ Name = 'long timeout'; Field = 'timeout_elapsed_milliseconds'; Value = 30000 },
        @{ Name = 'non-integer count'; Field = 'check_count'; Value = '12' })) {
        $measurements = New-SshLive001Measurements
        $measurements[$measurementCase.Field] = $measurementCase.Value
        $fixture = New-EvidenceBundle `
            "release-gate-measurement-$($measurementCase.Field)-$($measurementCase.Name -replace ' ', '-')" `
            -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
            -Summary (New-CanonicalSummary `
                -GateId 'SSH-LIVE-001' `
                -Checks @(New-SshLive001Checks) `
                -AdditionalProperties @{ measurements = $measurements })
        Assert-Rejected `
            -Fixture $fixture `
            -Name "SSH-LIVE-001 $($measurementCase.Name) measurement" `
            -Direct `
            -RequiredGateId 'SSH-LIVE-001' `
            -RequiredResult Pass
    }

    foreach ($numericEncoding in @(
        @{ Name = 'decimal'; Value = '12.0' },
        @{ Name = 'exponent'; Value = '1.2e1' })) {
        $noncanonicalNumberSummary = (New-SshLive001Summary).Replace(
            '"check_count": 12',
            "`"check_count`": $($numericEncoding.Value)")
        $noncanonicalNumberFixture = New-EvidenceBundle `
            "release-gate-noncanonical-$($numericEncoding.Name)" `
            -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
            -Summary $noncanonicalNumberSummary
        Assert-Rejected `
            -Fixture $noncanonicalNumberFixture `
            -Name "SSH-LIVE-001 $($numericEncoding.Name) integer encoding" `
            -Direct `
            -RequiredGateId 'SSH-LIVE-001' `
            -RequiredResult Pass
    }

    $reorderedChecks = @(New-SshLive001Checks)
    $temporaryCheck = $reorderedChecks[4]
    $reorderedChecks[4] = $reorderedChecks[5]
    $reorderedChecks[5] = $temporaryCheck
    $reorderedCheckFixture = New-EvidenceBundle `
        'release-gate-reordered-checks' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary (New-SshLive001Summary -Checks $reorderedChecks)
    Assert-Rejected `
        -Fixture $reorderedCheckFixture `
        -Name 'SSH-LIVE-001 checks outside writer order' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass

    $failedHistorySummary = New-CanonicalSummary `
        -GateId 'SSH-LIVE-001' `
        -Result 'Fail' `
        -Checks @([ordered]@{ id = 'package-sha256'; result = 'Fail' }) `
        -AdditionalProperties @{
            measurements = [ordered]@{
                check_count = 12
                passed_count = 0
                failed_count = 1
                blocked_count = 11
                package_sha256_verified = $false
            }
        }
    $failedHistoryFixture = New-EvidenceBundle `
        'release-gate-failed-history' `
        -Overrides @{
            gate_id = 'SSH-LIVE-001'
            authentication = 'Password'
            result = 'Fail'
        } `
        -Summary $failedHistorySummary
    Assert-Accepted `
        -Fixture $failedHistoryFixture `
        -Name 'root scan retains partial SSH-LIVE-001 failure measurements'

    $blockedHistorySummary = New-CanonicalSummary `
        -GateId 'SSH-LIVE-001' `
        -Result 'Blocked' `
        -Checks @([ordered]@{ id = 'package-sha256'; result = 'Blocked' }) `
        -AdditionalProperties @{
            measurements = [ordered]@{
                check_count = 12
                passed_count = 0
                failed_count = 0
                blocked_count = 12
            }
        }
    $blockedHistoryFixture = New-EvidenceBundle `
        'release-gate-blocked-history' `
        -Overrides @{
            gate_id = 'SSH-LIVE-001'
            authentication = 'Password'
            result = 'Blocked'
        } `
        -Summary $blockedHistorySummary
    Assert-Accepted `
        -Fixture $blockedHistoryFixture `
        -Name 'root scan retains partial SSH-LIVE-001 blocked measurements'

    $missingMeasurementDocument = New-SshLive001Measurements
    $missingMeasurementDocument.Remove('host_key_rejection_verified')
    $releaseGateMissingMeasurement = New-EvidenceBundle `
        'release-gate-missing-measurement' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary (New-CanonicalSummary `
            -GateId 'SSH-LIVE-001' `
            -Checks @(New-SshLive001Checks) `
            -AdditionalProperties @{ measurements = $missingMeasurementDocument })
    Assert-Rejected `
        -Fixture $releaseGateMissingMeasurement `
        -Name 'SSH-LIVE-001 missing canonical measurement property' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass

    $extraMeasurementDocument = New-SshLive001Measurements
    $extraMeasurementDocument['automation_passed'] = $true
    $releaseGateExtraMeasurement = New-EvidenceBundle `
        'release-gate-extra-measurement' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary (New-CanonicalSummary `
            -GateId 'SSH-LIVE-001' `
            -Checks @(New-SshLive001Checks) `
            -AdditionalProperties @{ measurements = $extraMeasurementDocument })
    Assert-Rejected `
        -Fixture $releaseGateExtraMeasurement `
        -Name 'SSH-LIVE-001 unexpected measurement property' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
    Assert-Rejected `
        -Fixture $canonical `
        -Name 'different Pass gate does not satisfy SSH-LIVE-001 release gate' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
    Assert-Rejected `
        -Fixture $releaseGate `
        -Name 'invalid required release gate identifier' `
        -Direct `
        -RequiredGateId 'ssh_live_001'
    $partialReleaseGate = New-EvidenceBundle `
        'release-gate-partial' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' }
    Assert-Rejected `
        -Fixture $partialReleaseGate `
        -Name 'partial SSH-LIVE-001 checks do not satisfy release gate' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
    $publicKeyReleaseGate = New-EvidenceBundle `
        'release-gate-public-key' `
        -Overrides @{ gate_id = 'SSH-LIVE-001' } `
        -Summary $releaseGateSummary
    Assert-Rejected `
        -Fixture $publicKeyReleaseGate `
        -Name 'PublicKey evidence does not satisfy Direct Password release gate' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
    Assert-Rejected `
        -Fixture $publicKeyReleaseGate `
        -Name 'root scan rejects mislabeled SSH-LIVE-001 PublicKey evidence'
    foreach ($profileCase in @(
        @{ Name = 'ARM64 evidence'; Field = 'architecture'; Value = 'arm64' },
        @{ Name = 'indirect route evidence'; Field = 'route'; Value = 'Socks5' },
        @{ Name = 'unrecorded fingerprint evidence'; Field = 'expected_host_fingerprint'; Value = 'NotRecorded' },
        @{ Name = 'unsupported Windows build evidence'; Field = 'windows_build'; Value = '10.0.19045.0' })) {
        $profileFixture = New-EvidenceBundle `
            "release-gate-$($profileCase.Field)" `
            -Overrides @{
                gate_id = 'SSH-LIVE-001'
                authentication = 'Password'
                $profileCase.Field = $profileCase.Value
            } `
            -Summary $releaseGateSummary
        Assert-Rejected `
            -Fixture $profileFixture `
            -Name "$($profileCase.Name) does not satisfy SSH-LIVE-001 release profile" `
            -Direct `
            -RequiredGateId 'SSH-LIVE-001' `
            -RequiredResult Pass
        Assert-Rejected `
            -Fixture $profileFixture `
            -Name "root scan rejects $($profileCase.Name) labeled as SSH-LIVE-001"
    }
    $missingReleaseChecks = @(New-SshLive001Checks | Select-Object -Skip 1)
    $missingReleaseGate = New-EvidenceBundle `
        'release-gate-missing-check' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary (New-SshLive001Summary -Checks $missingReleaseChecks)
    Assert-Rejected `
        -Fixture $missingReleaseGate `
        -Name 'missing full-gate check does not satisfy SSH-LIVE-001' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
    $extraReleaseChecks = @(
        @(New-SshLive001Checks) + @([ordered]@{ id = 'unexpected-extra'; result = 'Pass' }))
    $extraReleaseGate = New-EvidenceBundle `
        'release-gate-extra-check' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary (New-SshLive001Summary -Checks $extraReleaseChecks)
    Assert-Rejected `
        -Fixture $extraReleaseGate `
        -Name 'extra check does not satisfy exact SSH-LIVE-001 profile' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
    $duplicateReleaseChecks = @(
        @(New-SshLive001Checks) + @([ordered]@{ id = 'package-sha256'; result = 'Pass' }))
    $duplicateReleaseGate = New-EvidenceBundle `
        'release-gate-duplicate-check' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary (New-SshLive001Summary -Checks $duplicateReleaseChecks)
    Assert-Rejected `
        -Fixture $duplicateReleaseGate `
        -Name 'duplicate check does not satisfy exact SSH-LIVE-001 profile' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
    $failedReleaseChecks = @(New-SshLive001Checks)
    $failedReleaseChecks[0] = [ordered]@{ id = 'package-sha256'; result = 'Fail' }
    $failedReleaseGate = New-EvidenceBundle `
        'release-gate-failed-check' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary (New-SshLive001Summary -Checks $failedReleaseChecks)
    Assert-Rejected `
        -Fixture $failedReleaseGate `
        -Name 'failed check does not satisfy SSH-LIVE-001 Pass profile' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass

    $pkgOverrides = @{
        gate_id = 'PKG-001'
        server_family = 'NotApplicable'
        server_version = 'NotApplicable'
        route = 'NotApplicable'
        authentication = 'NotApplicable'
        expected_host_fingerprint = 'NotRecorded'
    }
    $pkgGate = New-EvidenceBundle `
        'pkg-gate' `
        -RelativeBundle 'alpha4/package/pkg-gate' `
        -Overrides $pkgOverrides `
        -Summary (New-Pkg001Summary)
    Assert-Accepted `
        -Fixture $pkgGate `
        -Name 'exact PKG-001 reviewed x64 package gate' `
        -Direct `
        -RequiredGateId 'PKG-001' `
        -RequiredResult Pass
    Assert-Accepted `
        -Fixture $pkgGate `
        -Name 'root scan accepts the canonical package evidence scope and PKG-001 profile'

    $unconfirmedPackageReview = New-EvidenceBundle `
        'pkg-gate-unconfirmed-review' `
        -RelativeBundle 'alpha4/package/pkg-gate-unconfirmed-review' `
        -Overrides $pkgOverrides `
        -Summary (New-Pkg001Summary)
    $unconfirmedReviewPath = Join-Path $unconfirmedPackageReview.Bundle 'review.json'
    $unconfirmedReviewText = Get-Content -LiteralPath $unconfirmedReviewPath -Raw
    Set-Utf8Text `
        -Path $unconfirmedReviewPath `
        -Content $unconfirmedReviewText.Replace(
            '"manual_observation_confirmed":true',
            '"manual_observation_confirmed":false')
    Assert-Rejected `
        -Fixture $unconfirmedPackageReview `
        -Name 'PKG-001 requires explicit manual-observation review confirmation' `
        -Direct `
        -RequiredGateId 'PKG-001' `
        -RequiredResult Pass

    $wrongGateInPackageScope = New-EvidenceBundle `
        'wrong-gate-in-package-scope' `
        -RelativeBundle 'alpha4/package/wrong-gate-in-package-scope'
    Assert-Rejected `
        -Fixture $wrongGateInPackageScope `
        -Name 'package scope rejects every gate other than PKG-001'

    $packageGateInWrongScope = New-EvidenceBundle `
        'package-gate-in-wrong-scope' `
        -Overrides $pkgOverrides `
        -Summary (New-Pkg001Summary)
    Assert-Rejected `
        -Fixture $packageGateInWrongScope `
        -Name 'PKG-001 is reserved for the package evidence scope'

    foreach ($pkgProfileCase in @(
        @{ Name = 'ARM64 package'; Field = 'architecture'; Value = 'arm64' },
        @{ Name = 'SSH server tuple'; Field = 'server_family'; Value = 'OpenSSH' },
        @{ Name = 'SSH route tuple'; Field = 'route'; Value = 'Direct' },
        @{ Name = 'SSH authentication tuple'; Field = 'authentication'; Value = 'Password' },
        @{ Name = 'fingerprint tuple'; Field = 'expected_host_fingerprint'; Value = 'SHA256:[redacted]' },
        @{ Name = 'unsupported Windows build'; Field = 'windows_build'; Value = '10.0.19045.0' })) {
        $overrides = @{}
        foreach ($entry in $pkgOverrides.GetEnumerator()) {
            $overrides[$entry.Key] = $entry.Value
        }
        $overrides[$pkgProfileCase.Field] = $pkgProfileCase.Value
        $fixture = New-EvidenceBundle `
            "pkg-gate-$($pkgProfileCase.Field)" `
            -RelativeBundle "alpha4/package/pkg-gate-$($pkgProfileCase.Field)" `
            -Overrides $overrides `
            -Summary (New-Pkg001Summary)
        Assert-Rejected `
            -Fixture $fixture `
            -Name "$($pkgProfileCase.Name) does not satisfy PKG-001" `
            -Direct `
            -RequiredGateId 'PKG-001' `
            -RequiredResult Pass
        Assert-Rejected `
            -Fixture $fixture `
            -Name "root scan rejects $($pkgProfileCase.Name) labeled as PKG-001"
    }

    foreach ($pkgMeasurementCase in @(
        @{ Name = 'false package tree identity'; Field = 'package_tree_identity_verified'; Value = $false },
        @{ Name = 'false startup'; Field = 'ui_startup_verified'; Value = $false },
        @{ Name = 'wrong shortcut count'; Field = 'alt_navigation_shortcut_count'; Value = 6 },
        @{ Name = 'string check count'; Field = 'check_count'; Value = '6' })) {
        $measurements = New-Pkg001Measurements
        $measurements[$pkgMeasurementCase.Field] = $pkgMeasurementCase.Value
        $fixture = New-EvidenceBundle `
            "pkg-gate-measurement-$($pkgMeasurementCase.Field)" `
            -RelativeBundle "alpha4/package/pkg-gate-measurement-$($pkgMeasurementCase.Field)" `
            -Overrides $pkgOverrides `
            -Summary (New-Pkg001Summary -Measurements $measurements)
        Assert-Rejected `
            -Fixture $fixture `
            -Name "PKG-001 $($pkgMeasurementCase.Name) measurement" `
            -Direct `
            -RequiredGateId 'PKG-001' `
            -RequiredResult Pass
    }

    $missingPkgMeasurement = New-Pkg001Measurements
    $missingPkgMeasurement.Remove('ui_shutdown_verified')
    $missingPkgMeasurementFixture = New-EvidenceBundle `
        'pkg-gate-missing-measurement' `
        -RelativeBundle 'alpha4/package/pkg-gate-missing-measurement' `
        -Overrides $pkgOverrides `
        -Summary (New-Pkg001Summary -Measurements $missingPkgMeasurement)
    Assert-Rejected `
        -Fixture $missingPkgMeasurementFixture `
        -Name 'PKG-001 missing canonical measurement' `
        -Direct `
        -RequiredGateId 'PKG-001' `
        -RequiredResult Pass

    $extraPkgMeasurement = New-Pkg001Measurements
    $extraPkgMeasurement['automation_passed'] = $true
    $extraPkgMeasurementFixture = New-EvidenceBundle `
        'pkg-gate-extra-measurement' `
        -RelativeBundle 'alpha4/package/pkg-gate-extra-measurement' `
        -Overrides $pkgOverrides `
        -Summary (New-Pkg001Summary -Measurements $extraPkgMeasurement)
    Assert-Rejected `
        -Fixture $extraPkgMeasurementFixture `
        -Name 'PKG-001 unexpected measurement' `
        -Direct `
        -RequiredGateId 'PKG-001' `
        -RequiredResult Pass

    $reorderedPkgChecks = @(New-Pkg001Checks)
    $temporaryPkgCheck = $reorderedPkgChecks[3]
    $reorderedPkgChecks[3] = $reorderedPkgChecks[4]
    $reorderedPkgChecks[4] = $temporaryPkgCheck
    $reorderedPkgFixture = New-EvidenceBundle `
        'pkg-gate-reordered-checks' `
        -RelativeBundle 'alpha4/package/pkg-gate-reordered-checks' `
        -Overrides $pkgOverrides `
        -Summary (New-Pkg001Summary -Checks $reorderedPkgChecks)
    Assert-Rejected `
        -Fixture $reorderedPkgFixture `
        -Name 'PKG-001 checks outside canonical order' `
        -Direct `
        -RequiredGateId 'PKG-001' `
        -RequiredResult Pass

    $missingPkgChecks = @(New-Pkg001Checks | Select-Object -Skip 1)
    $missingPkgCheckFixture = New-EvidenceBundle `
        'pkg-gate-missing-check' `
        -RelativeBundle 'alpha4/package/pkg-gate-missing-check' `
        -Overrides $pkgOverrides `
        -Summary (New-Pkg001Summary -Checks $missingPkgChecks)
    Assert-Rejected `
        -Fixture $missingPkgCheckFixture `
        -Name 'PKG-001 missing complete gate check' `
        -Direct `
        -RequiredGateId 'PKG-001' `
        -RequiredResult Pass

    $reservedTupleFixture = New-EvidenceBundle `
        'not-applicable-reserved' `
        -Overrides @{ route = 'NotApplicable'; authentication = 'NotApplicable' }
    Assert-Rejected `
        -Fixture $reservedTupleFixture `
        -Name 'NotApplicable tuple is reserved for PKG-001'

    $safeScenario = New-EvidenceBundle `
        'safe-scenario-field' `
        -Summary (New-CanonicalSummary -AdditionalProperties @{
            scenario = [ordered]@{ id = 'isolated-fixture'; attempt = 1 }
        })
    Assert-Accepted -Fixture $safeScenario -Name 'bounded non-identifying scenario fields remain extensible'

    $attachments = New-EvidenceBundle `
        'safe-attachments' `
        -EvidenceFiles @('summary.json', 'review.txt', 'screen.png')
    Set-Utf8Text -Path (Join-Path $attachments.Bundle 'review.txt') -Content 'check=passed; identifying values were not recorded'
    $onePixelPng = [Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=')
    [System.IO.File]::WriteAllBytes((Join-Path $attachments.Bundle 'screen.png'), $onePixelPng)
    Set-CanonicalFixtureReview -Fixture $attachments
    Assert-Accepted -Fixture $attachments -Name 'safe UTF-8 text and structurally validated PNG attachments'
    Set-Utf8Text `
        -Path (Join-Path $attachments.Bundle 'review.txt') `
        -Content 'check=passed; final attachment changed after review'
    Assert-Rejected -Fixture $attachments -Name 'post-review attachment mutation with stale source hash'

    $failedRecord = New-EvidenceBundle 'failed-record' -Overrides @{ result = 'Fail' }
    Assert-Accepted -Fixture $failedRecord -Name 'Fail is a valid recorded result without a release-result binding'
    Assert-Rejected -Fixture $failedRecord -Name 'Fail does not satisfy a required Pass gate' -Direct -RequiredResult Pass
    $blockedRecord = New-EvidenceBundle 'blocked-record' -Overrides @{ result = 'Blocked' }
    Assert-Accepted -Fixture $blockedRecord -Name 'Blocked is supported by a blocked check'
    $blockedCategoryRecord = New-EvidenceBundle `
        'blocked-category-record' `
        -Overrides @{ result = 'Blocked' } `
        -Summary (New-CanonicalSummary `
            -Result Blocked `
            -Checks @([ordered]@{ id = 'manual-gate'; result = 'Pass' }) `
            -AdditionalProperties @{ blocking_category = 'ManualGateCoverageRequired' })
    Assert-Accepted -Fixture $blockedCategoryRecord -Name 'bounded blocking category supports a Blocked result'

    $emptySummary = New-EvidenceBundle 'summary-empty-object' -Summary '{}'
    Assert-Rejected -Fixture $emptySummary -Name 'empty summary object'
    $missingGateSummaryObject = New-CanonicalSummary | ConvertFrom-Json
    $missingGateSummaryObject.PSObject.Properties.Remove('gate_id')
    $missingGateSummary = New-EvidenceBundle `
        'summary-missing-gate' `
        -Summary ($missingGateSummaryObject | ConvertTo-Json -Depth 10 -Compress)
    Assert-Rejected -Fixture $missingGateSummary -Name 'summary missing a required manifest binding'
    $missingPrivacyObject = New-CanonicalSummary | ConvertFrom-Json
    $missingPrivacyObject.PSObject.Properties.Remove('privacy_notice')
    $missingPrivacy = New-EvidenceBundle `
        'summary-missing-privacy' `
        -Summary ($missingPrivacyObject | ConvertTo-Json -Depth 10 -Compress)
    Assert-Rejected -Fixture $missingPrivacy -Name 'summary missing canonical privacy notice'

    foreach ($summaryConflict in @(
        @{ Name = 'schema'; Summary = (New-CanonicalSummary).Replace('"schema_version": 1', '"schema_version": 2') },
        @{ Name = 'gate'; Summary = (New-CanonicalSummary -GateId 'SSH-LIVE-001') },
        @{ Name = 'result'; Summary = (New-CanonicalSummary -Result Fail) },
        @{ Name = 'timestamp'; Summary = (New-CanonicalSummary -StartedAtUtc '2026-08-20T01:02:04Z') },
        @{ Name = 'duration'; Summary = (New-CanonicalSummary -DurationSeconds 13) },
        @{ Name = 'redaction'; Summary = (New-CanonicalSummary -RedactionReviewed:$false) },
        @{ Name = 'privacy'; Summary = (New-CanonicalSummary).Replace($privacyNotice, 'Different notice.') }
    )) {
        $fixture = New-EvidenceBundle "summary-conflict-$($summaryConflict.Name)" -Summary $summaryConflict.Summary
        Assert-Rejected -Fixture $fixture -Name "summary $($summaryConflict.Name) conflict"
    }

    $emptyChecks = New-EvidenceBundle `
        'summary-empty-checks' `
        -Summary (New-CanonicalSummary -Checks ([object[]]@()))
    Assert-Rejected -Fixture $emptyChecks -Name 'empty summary checks array'
    $partialPass = New-EvidenceBundle `
        'summary-partial-pass' `
        -Summary (New-CanonicalSummary -Checks @(
            [ordered]@{ id = 'first'; result = 'Pass' },
            [ordered]@{ id = 'second'; result = 'Fail' }))
    Assert-Rejected -Fixture $partialPass -Name 'Pass summary containing a failed check'
    $unsupportedFail = New-EvidenceBundle `
        'summary-unsupported-fail' `
        -Overrides @{ result = 'Fail' } `
        -Summary (New-CanonicalSummary `
            -Result Fail `
            -Checks @([ordered]@{ id = 'first'; result = 'Pass' }))
    Assert-Rejected -Fixture $unsupportedFail -Name 'Fail summary without a failed check'
    $unsupportedBlocked = New-EvidenceBundle `
        'summary-unsupported-blocked' `
        -Overrides @{ result = 'Blocked' } `
        -Summary (New-CanonicalSummary `
            -Result Blocked `
            -Checks @([ordered]@{ id = 'first'; result = 'Pass' }))
    Assert-Rejected -Fixture $unsupportedBlocked -Name 'Blocked summary without blocked evidence or category'
    $duplicateCheckIds = New-EvidenceBundle `
        'summary-duplicate-check-ids' `
        -Summary (New-CanonicalSummary -Checks @(
            [ordered]@{ id = 'same'; result = 'Pass' },
            [ordered]@{ id = 'same'; result = 'Pass' }))
    Assert-Rejected -Fixture $duplicateCheckIds -Name 'duplicate summary check identifiers'
    $invalidCheckId = New-EvidenceBundle `
        'summary-invalid-check-id' `
        -Summary (New-CanonicalSummary `
            -Checks @([ordered]@{ id = 'Bad_Id'; result = 'Pass' }))
    Assert-Rejected -Fixture $invalidCheckId -Name 'noncanonical summary check identifier'
    $invalidCheckResult = New-EvidenceBundle `
        'summary-invalid-check-result' `
        -Summary (New-CanonicalSummary `
            -Checks @([ordered]@{ id = 'smoke'; result = 'Skipped' }))
    Assert-Rejected -Fixture $invalidCheckResult -Name 'unsupported summary check result'
    $tooManyChecks = for ($checkIndex = 0; $checkIndex -lt 65; $checkIndex++) {
        [ordered]@{ id = "check-$checkIndex"; result = 'Pass' }
    }
    $overboundedChecks = New-EvidenceBundle `
        'summary-overbounded-checks' `
        -Summary (New-CanonicalSummary -Checks $tooManyChecks)
    Assert-Rejected -Fixture $overboundedChecks -Name 'summary checks count above the bound'
    $duplicateRootJson = (New-CanonicalSummary).TrimEnd('}') + ',"gate_id":"SSH-LIVE-001"}'
    $duplicateRoot = New-EvidenceBundle 'summary-duplicate-root-property' -Summary $duplicateRootJson
    Assert-Rejected -Fixture $duplicateRoot -Name 'duplicate root JSON property'
    $duplicateNestedJson = (New-CanonicalSummary).Replace(
        '"id": "smoke",',
        '"id": "smoke", "result": "Pass",')
    $duplicateNested = New-EvidenceBundle 'summary-duplicate-nested-property' -Summary $duplicateNestedJson
    Assert-Rejected -Fixture $duplicateNested -Name 'duplicate nested JSON property'

    $unknown = New-EvidenceBundle 'unknown-field'
    Add-Content -LiteralPath $unknown.Manifest -Value 'notes: "not allowed"' -Encoding utf8NoBOM
    Assert-Rejected -Fixture $unknown -Name 'unknown YAML field'

    $duplicate = New-EvidenceBundle 'duplicate-field'
    Add-Content -LiteralPath $duplicate.Manifest -Value "commit: `"$commit`"" -Encoding utf8NoBOM
    Assert-Rejected -Fixture $duplicate -Name 'duplicate YAML field'

    $missing = New-EvidenceBundle 'missing-field' -Omit @('package_sha256')
    Assert-Rejected -Fixture $missing -Name 'missing required field'
    $badGateId = New-EvidenceBundle 'bad-gate-id' -Overrides @{ gate_id = 'ssh_live_001' }
    Assert-Rejected -Fixture $badGateId -Name 'noncanonical gate identifier'

    $anchor = New-EvidenceBundle 'yaml-anchor'
    (Get-Content -LiteralPath $anchor.Manifest -Raw).Replace('route: "Direct"', 'route: &route "Direct"') |
        Set-Content -LiteralPath $anchor.Manifest -Encoding utf8NoBOM
    Assert-Rejected -Fixture $anchor -Name 'YAML anchor'

    $multiline = New-EvidenceBundle 'yaml-multiline'
    Add-Content -LiteralPath $multiline.Manifest -Value @('notes: |', '  raw output') -Encoding utf8NoBOM
    Assert-Rejected -Fixture $multiline -Name 'multiline YAML and nested content'

    foreach ($enumCase in @(
        @{ Name = 'architecture-enum'; Field = 'architecture'; Value = 'amd64' },
        @{ Name = 'route-enum'; Field = 'route'; Value = 'direct' },
        @{ Name = 'authentication-enum'; Field = 'authentication'; Value = 'Key' },
        @{ Name = 'result-enum'; Field = 'result'; Value = 'Succeeded' }
    )) {
        $fixture = New-EvidenceBundle $enumCase.Name -Overrides @{ $enumCase.Field = $enumCase.Value }
        Assert-Rejected -Fixture $fixture -Name $enumCase.Name
    }

    $zeroCommit = New-EvidenceBundle 'zero-commit' -Overrides @{ commit = ('0' * 40) }
    Assert-Rejected -Fixture $zeroCommit -Name 'all-zero commit placeholder'
    $uppercaseCommit = New-EvidenceBundle 'uppercase-commit' -Overrides @{ commit = $commit.ToUpperInvariant() }
    Assert-Rejected -Fixture $uppercaseCommit -Name 'uppercase commit'
    $zeroPackage = New-EvidenceBundle 'zero-package' -Overrides @{ package_sha256 = ('0' * 64) }
    Assert-Rejected -Fixture $zeroPackage -Name 'all-zero package digest placeholder'

    $badTimestamp = New-EvidenceBundle 'bad-timestamp' -Overrides @{ started_at_utc = '2026-08-20T01:02:03+09:00' }
    Assert-Rejected -Fixture $badTimestamp -Name 'non-UTC RFC3339 timestamp'
    $badDuration = New-EvidenceBundle 'bad-duration' -Overrides @{ duration_seconds = '-1' }
    Assert-Rejected -Fixture $badDuration -Name 'negative duration'
    $redactionFalse = New-EvidenceBundle 'redaction-false' -Overrides @{ redaction_reviewed = 'false' }
    Assert-Rejected -Fixture $redactionFalse -Name 'redaction review not completed'
    $rawFingerprint = New-EvidenceBundle 'raw-fingerprint' -Overrides @{ expected_host_fingerprint = 'SHA256:AbCdEf0123456789' }
    Assert-Rejected -Fixture $rawFingerprint -Name 'unredacted host fingerprint'

    foreach ($typedScalarCase in @(
        @{ Name = 'quoted-schema-version'; Field = 'schema_version'; Raw = 'schema_version: "1"' },
        @{ Name = 'quoted-duration'; Field = 'duration_seconds'; Raw = 'duration_seconds: "12"' },
        @{ Name = 'quoted-redaction'; Field = 'redaction_reviewed'; Raw = 'redaction_reviewed: "true"' }
    )) {
        $fixture = New-EvidenceBundle $typedScalarCase.Name
        $manifestText = Get-Content -LiteralPath $fixture.Manifest -Raw
        $manifestText = [regex]::Replace(
            $manifestText,
            "(?m)^$([regex]::Escape($typedScalarCase.Field)):\s+.*$",
            $typedScalarCase.Raw)
        Set-Utf8Text -Path $fixture.Manifest -Content $manifestText
        Assert-Rejected -Fixture $fixture -Name "$($typedScalarCase.Field) must retain its YAML scalar type"
    }

    foreach ($pathCase in @(
        @{ Name = 'rooted-path'; Path = 'C:/temp/summary.json' },
        @{ Name = 'parent-traversal'; Path = '../summary.json' },
        @{ Name = 'backslash-path'; Path = 'nested\summary.json' },
        @{ Name = 'ads-path'; Path = 'summary.json:secret' }
    )) {
        $fixture = New-EvidenceBundle $pathCase.Name -EvidenceFiles @($pathCase.Path)
        Assert-Rejected -Fixture $fixture -Name $pathCase.Name
    }

    $duplicateFiles = New-EvidenceBundle 'duplicate-files' -EvidenceFiles @('summary.json', 'summary.json')
    Assert-Rejected -Fixture $duplicateFiles -Name 'duplicate evidence path'
    $missingFile = New-EvidenceBundle 'missing-file' -EvidenceFiles @('summary.json', 'missing.json')
    Assert-Rejected -Fixture $missingFile -Name 'missing declared evidence file'
    $extraFile = New-EvidenceBundle 'extra-file'
    Set-Utf8Text -Path (Join-Path $extraFile.Bundle 'unlisted.json') -Content '{}'
    Assert-Rejected -Fixture $extraFile -Name 'unlisted extra bundle file'
    $pngFile = New-EvidenceBundle 'invalid-png' -EvidenceFiles @('summary.json', 'screenshot.png')
    [System.IO.File]::WriteAllBytes((Join-Path $pngFile.Bundle 'screenshot.png'), [byte[]](1, 2, 3))
    Assert-Rejected -Fixture $pngFile -Name 'invalid PNG signature and structure'
    $forbiddenExtension = New-EvidenceBundle 'forbidden-extension' -EvidenceFiles @('summary.json', 'capture.log')
    Set-Utf8Text -Path (Join-Path $forbiddenExtension.Bundle 'capture.log') -Content 'redacted'
    Assert-Rejected -Fixture $forbiddenExtension -Name 'unreviewable evidence extension'
    $secretText = New-EvidenceBundle 'secret-text' -EvidenceFiles @('summary.json', 'review.txt')
    Set-Utf8Text -Path (Join-Path $secretText.Bundle 'review.txt') -Content 'OTP: 123456'
    Assert-Rejected -Fixture $secretText -Name 'secret marker in UTF-8 text attachment'
    $controlText = New-EvidenceBundle 'control-text' -EvidenceFiles @('summary.json', 'review.txt')
    Set-Utf8Text -Path (Join-Path $controlText.Bundle 'review.txt') -Content ("safe" + [char]1 + "unsafe")
    Assert-Rejected -Fixture $controlText -Name 'forbidden ASCII control in UTF-8 text attachment'
    $badAncillaryPng = New-EvidenceBundle 'bad-png-ancillary' -EvidenceFiles @('summary.json', 'screen.png')
    $badPngBytes = Add-PngChunkBeforeIend `
        -Png $onePixelPng `
        -Type 'sRGB' `
        -Data ([byte[]](0, 1)) `
        -AfterIhdr
    [System.IO.File]::WriteAllBytes((Join-Path $badAncillaryPng.Bundle 'screen.png'), $badPngBytes)
    Assert-Rejected -Fixture $badAncillaryPng -Name 'invalid PNG ancillary chunk length with valid CRC'
    $badCrcPng = New-EvidenceBundle 'bad-png-crc' -EvidenceFiles @('summary.json', 'screen.png')
    $badCrcBytes = [byte[]]$onePixelPng.Clone()
    $idatTypeOffset = [System.Text.Encoding]::ASCII.GetString($badCrcBytes).IndexOf(
        'IDAT',
        [StringComparison]::Ordinal)
    if ($idatTypeOffset -lt 0) {
        throw 'PNG CRC fixture cannot locate its IDAT chunk.'
    }
    $badCrcBytes[$idatTypeOffset + 4] = $badCrcBytes[$idatTypeOffset + 4] -bxor 1
    [System.IO.File]::WriteAllBytes((Join-Path $badCrcPng.Bundle 'screen.png'), $badCrcBytes)
    Assert-Rejected -Fixture $badCrcPng -Name 'PNG data corruption with stale CRC'

    $badManifestUtf8 = New-EvidenceBundle 'manifest-invalid-utf8'
    [System.IO.File]::WriteAllBytes($badManifestUtf8.Manifest, [byte[]](0xff, 0xfe, 0xfd))
    Assert-Rejected -Fixture $badManifestUtf8 -Name 'invalid UTF-8 manifest'
    $badSummaryUtf8 = New-EvidenceBundle 'summary-invalid-utf8'
    [System.IO.File]::WriteAllBytes((Join-Path $badSummaryUtf8.Bundle 'summary.json'), [byte[]](0xff, 0xfe, 0xfd))
    Assert-Rejected -Fixture $badSummaryUtf8 -Name 'invalid UTF-8 summary'
    $badJson = New-EvidenceBundle 'invalid-json' -Summary '{not-json}'
    Assert-Rejected -Fixture $badJson -Name 'invalid summary JSON'
    $largeJson = New-EvidenceBundle 'oversized-json' -Summary ('{"detail":"' + ('a' * 1048576) + '"}')
    Assert-Rejected -Fixture $largeJson -Name 'oversized evidence file'

    $sensitiveCases = @(
        @{ Name = 'password'; Value = 'password: hunter2' },
        @{ Name = 'passphrase'; Value = 'passphrase=do-not-record' },
        @{ Name = 'otp'; Value = 'OTP: 123456' },
        @{ Name = 'private-key'; Value = "-----BEGIN OPENSSH PRIVATE KEY-----`nAAAA`n-----END OPENSSH PRIVATE KEY-----" },
        @{ Name = 'public-key'; Value = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAICc8HZuL31FZq8eNw1aY3bP6cJzPF1zjS8pXQoJ9 user' },
        @{ Name = 'user-at-host'; Value = 'tester@buildhost' },
        @{ Name = 'hostname'; Value = 'server.example.com' },
        @{ Name = 'hostname-label'; Value = 'hostname: buildhost' },
        @{ Name = 'ipv4'; Value = '192.0.2.10' },
        @{ Name = 'ipv6'; Value = '2001:db8::10' },
        @{ Name = 'url'; Value = 'ssh://example.invalid' },
        @{ Name = 'windows-path'; Value = 'C:\Users\tester\key' },
        @{ Name = 'unix-path'; Value = '/home/tester/.ssh/key' },
        @{ Name = 'prompt-transcript'; Value = 'PS C:\work> whoami' }
    )
    foreach ($sensitiveCase in $sensitiveCases) {
        $summary = New-CanonicalSummary -AdditionalProperties @{
            scenario_detail = $sensitiveCase.Value
        }
        $fixture = New-EvidenceBundle "sensitive-$($sensitiveCase.Name)" -Summary $summary
        Assert-Rejected -Fixture $fixture -Name "forbidden $($sensitiveCase.Name) material"
    }

    $transcriptProperty = New-EvidenceBundle `
        'transcript-property' `
        -Summary (New-CanonicalSummary -AdditionalProperties @{ transcript = 'redacted' })
    Assert-Rejected -Fixture $transcriptProperty -Name 'transcript JSON property'
    $secretProperty = New-EvidenceBundle `
        'secret-property' `
        -Summary (New-CanonicalSummary -AdditionalProperties @{ secret = 'redacted' })
    Assert-Rejected -Fixture $secretProperty -Name 'secret JSON property'

    $expectedMismatch = New-EvidenceBundle 'expected-mismatch'
    Assert-Rejected `
        -Fixture $expectedMismatch `
        -Name 'expected commit mismatch' `
        -Direct `
        -ExpectedCommit 'ffffffffffffffffffffffffffffffffffffffff'
    Assert-Rejected `
        -Fixture $expectedMismatch `
        -Name 'expected package mismatch' `
        -Direct `
        -ExpectedPackageSha256 ('f' * 64)

    $badLayout = New-EvidenceBundle 'bad-layout' -RelativeBundle 'alpha4/too-shallow'
    Assert-Rejected -Fixture $badLayout -Name 'manifest outside canonical root layout'

    $rootOrphan = New-EvidenceBundle 'root-orphan'
    Set-Utf8Text -Path (Join-Path $rootOrphan.Root 'orphan.txt') -Content 'orphan'
    Assert-Rejected -Fixture $rootOrphan -Name 'orphan root file'

    $releaseOrphan = New-EvidenceBundle 'release-orphan'
    Set-Utf8Text -Path (Join-Path $releaseOrphan.Root 'alpha4/orphan.txt') -Content 'orphan'
    Assert-Rejected -Fixture $releaseOrphan -Name 'orphan release file'

    $scopeOrphan = New-EvidenceBundle 'scope-orphan'
    Set-Utf8Text -Path (Join-Path $scopeOrphan.Root 'alpha4/ssh-auth/orphan.txt') -Content 'orphan'
    Assert-Rejected -Fixture $scopeOrphan -Name 'orphan scope file'

    $unknownScope = New-EvidenceBundle 'unknown-scope'
    New-Item -ItemType Directory -Path (Join-Path $unknownScope.Root 'alpha4/not-approved') -Force | Out-Null
    Set-Utf8Text -Path (Join-Path $unknownScope.Root 'alpha4/not-approved/README.md') -Content '# Unknown.'
    Assert-Rejected -Fixture $unknownScope -Name 'unknown evidence scope'

    $caseVariant = New-EvidenceBundle 'case-variant'
    Move-Item -LiteralPath $caseVariant.Manifest -Destination (Join-Path $caseVariant.Bundle 'MANIFEST.yml')
    Assert-Rejected -Fixture $caseVariant -Name 'case-variant manifest name'

    $nestedGarbage = New-EvidenceBundle 'nested-garbage'
    New-Item -ItemType Directory -Path (Join-Path $nestedGarbage.Bundle 'empty') -Force | Out-Null
    Assert-Rejected -Fixture $nestedGarbage -Name 'undeclared empty nested directory'

    $junctionFixture = New-EvidenceBundle 'junction-path' -EvidenceFiles @('summary.json', 'linked/detail.json')
    $junctionTarget = Join-Path $scratch 'junction-target'
    New-Item -ItemType Directory -Path $junctionTarget -Force | Out-Null
    Set-Utf8Text -Path (Join-Path $junctionTarget 'detail.json') -Content '{}'
    New-Item -ItemType Junction -Path (Join-Path $junctionFixture.Bundle 'linked') -Target $junctionTarget | Out-Null
    Assert-Rejected -Fixture $junctionFixture -Name 'reparse-point traversal'

    $junctionBundleTarget = New-EvidenceBundle 'junction-bundle-target'
    [System.IO.File]::WriteAllBytes($junctionBundleTarget.Manifest, [byte[]](0xff, 0xfe, 0xfd))
    $junctionRoot = Join-Path $scratch 'junction-bundle-root'
    Set-Utf8Text -Path (Join-Path $junctionRoot 'EVIDENCE_SCHEMA.md') -Content '# Fixture evidence schema.'
    Set-Utf8Text -Path (Join-Path $junctionRoot 'alpha4/README.md') -Content '# Fixture Alpha 4 index.'
    $junctionScope = Join-Path $junctionRoot 'alpha4\ssh-auth'
    New-Item -ItemType Directory -Path $junctionScope -Force | Out-Null
    Set-Utf8Text -Path (Join-Path $junctionScope 'README.md') -Content '# Fixture SSH authentication index.'
    New-Item `
        -ItemType Junction `
        -Path (Join-Path $junctionScope 'linked-bundle') `
        -Target $junctionBundleTarget.Bundle | Out-Null
    $caseCount++
    $junctionFailure = Get-ValidationFailure -Root $junctionRoot
    if ($null -eq $junctionFailure -or
        -not $junctionFailure.Contains('reparse', [StringComparison]::OrdinalIgnoreCase) -or
        $junctionFailure.Contains('UTF-8', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Bundle-root reparse fixture was read instead of being rejected at the path boundary.'
    }

    $writerPayload = Join-Path $scratch 'package-writer-payload'
    New-Item -ItemType Directory -Path $writerPayload -Force | Out-Null
    Set-Utf8Text -Path (Join-Path $writerPayload 'sutty.UI.exe') -Content 'fixture executable bytes'
    Set-Utf8Text -Path (Join-Path $writerPayload 'sutty.UI.dll') -Content 'fixture UI dependency bytes'
    Set-Utf8Text -Path (Join-Path $writerPayload 'Assets\fixture.asset') -Content 'fixture asset bytes'
    Set-Utf8Text `
        -Path (Join-Path $writerPayload 'BUILDINFO.txt') `
        -Content ((@(
            'Sutty v0.1.0-alpha.4'
            "Commit: $commit"
            'Channel: Alpha'
            'Signing: unsigned ZIP evaluation build'
            'Minimum OS: Windows 11 24H2'
            'Architecture: x64'
        ) -join [Environment]::NewLine) + [Environment]::NewLine)
    $writerPackageRoot = Join-Path $scratch 'package-writer-candidate'
    New-Item -ItemType Directory -Path $writerPackageRoot -Force | Out-Null
    $writerPackage = Join-Path $writerPackageRoot 'Sutty-v0.1.0-alpha.4-win-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($writerPayload, $writerPackage)
    $writerPackageSha256 = (Get-FileHash -LiteralPath $writerPackage -Algorithm SHA256).
        Hash.ToLowerInvariant()
    $writerStartedAt = [DateTimeOffset]::UtcNow.AddSeconds(-10).ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        [Globalization.CultureInfo]::InvariantCulture)
    $writerOutput = Join-Path $scratch 'package-writer-output'
    $writerBoundaryRepository = Join-Path $scratch 'package-writer-boundary-repository'
    $writerBoundaryScripts = Join-Path $writerBoundaryRepository '.github\scripts'
    $writerBoundaryEvidence = Join-Path $writerBoundaryRepository 'docs\evidence'
    New-Item -ItemType Directory -Path $writerBoundaryScripts -Force | Out-Null
    New-Item -ItemType Directory -Path $writerBoundaryEvidence -Force | Out-Null
    $writerBoundaryScript = Join-Path $writerBoundaryScripts 'Write-PackageEvidence.ps1'
    Copy-Item -LiteralPath $packageWriter -Destination $writerBoundaryScript
    Assert-ActionRejected -Name 'package writer rejects a case-variant exact committed evidence root' -Action {
        & $writerBoundaryScript `
            -PackagePath $writerPackage `
            -ObservedUiPath (Join-Path $writerPayload 'sutty.UI.exe') `
            -Tag 'v0.1.0-alpha.4' `
            -Commit $commit `
            -EvidenceOutputRoot $writerBoundaryEvidence.ToUpperInvariant() `
            -StartedAtUtc $writerStartedAt `
            -DurationSeconds 1 `
            -UiStartupResult Pass `
            -AltNavigationSilentResult Pass `
            -AltNavigationShortcutCount 7 `
            -UiShutdownResult Pass *> $null
    }
    $writerSourceBundle = @(& $packageWriter `
        -PackagePath $writerPackage `
        -ObservedUiPath (Join-Path $writerPayload 'sutty.UI.exe') `
        -Tag 'v0.1.0-alpha.4' `
        -Commit $commit `
        -EvidenceOutputRoot $writerOutput `
        -StartedAtUtc $writerStartedAt `
        -DurationSeconds 1 `
        -UiStartupResult Pass `
        -AltNavigationSilentResult Pass `
        -AltNavigationShortcutCount 7 `
        -UiShutdownResult Pass | Select-Object -Last 1)[0]
    $script:caseCount++
    $writerSourceManifest = Join-Path $writerSourceBundle 'manifest.yml'
    if (-not (Test-Path -LiteralPath $writerSourceManifest -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $writerSourceBundle 'review.json')) -or
        (Get-Content -LiteralPath $writerSourceManifest -Raw) -cnotmatch
            '(?m)^redaction_reviewed: false\r?$') {
        throw 'Package-evidence writer did not create one canonical unreviewed source bundle.'
    }
    $writerReviewedRoot = Join-Path $scratch 'package-writer-reviewed'
    $reviewedAt = [DateTimeOffset]::UtcNow.ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        [Globalization.CultureInfo]::InvariantCulture)
    Assert-ActionRejected -Name 'PKG-001 review requires manual-observation confirmation' -Action {
        & $evidenceReviewer `
            -SourceManifestPath $writerSourceManifest `
            -DestinationRoot (Join-Path $scratch 'package-writer-unconfirmed-review') `
            -ReviewerId 'github-package-reviewer' `
            -ReviewedAtUtc $reviewedAt `
            -PrivacyReview Confirmed `
            -ExpectedCommit $commit `
            -ExpectedPackageSha256 $writerPackageSha256 `
            -RequiredGateId PKG-001 `
            -RequiredResult Pass *> $null
    }
    $writerReviewedBundle = @(& $evidenceReviewer `
        -SourceManifestPath $writerSourceManifest `
        -DestinationRoot $writerReviewedRoot `
        -ReviewerId 'github-package-reviewer' `
        -ReviewedAtUtc $reviewedAt `
        -PrivacyReview Confirmed `
        -ManualObservationReview Confirmed `
        -ExpectedCommit $commit `
        -ExpectedPackageSha256 $writerPackageSha256 `
        -RequiredGateId PKG-001 `
        -RequiredResult Pass | Select-Object -Last 1)[0]
    $script:caseCount++
    $writerValidationFailure = Get-ValidationFailure `
        -Manifest (Join-Path $writerReviewedBundle 'manifest.yml') `
        -ExpectedCommit $commit `
        -ExpectedPackageSha256 $writerPackageSha256 `
        -RequiredGateId PKG-001 `
        -RequiredResult Pass
    if ($null -ne $writerValidationFailure) {
        throw "Reviewed package-writer output should pass: $writerValidationFailure"
    }

    Assert-ActionRejected -Name 'package writer rejects BUILDINFO commit mismatch' -Action {
        & $packageWriter `
            -PackagePath $writerPackage `
            -ObservedUiPath (Join-Path $writerPayload 'sutty.UI.exe') `
            -Tag 'v0.1.0-alpha.4' `
            -Commit ('f' * 40) `
            -EvidenceOutputRoot (Join-Path $scratch 'package-writer-wrong-commit') `
            -StartedAtUtc $writerStartedAt `
            -DurationSeconds 1 `
            -UiStartupResult Pass `
            -AltNavigationSilentResult Pass `
            -AltNavigationShortcutCount 7 `
            -UiShutdownResult Pass *> $null
    }
    $differentUiRoot = Join-Path $scratch 'package-writer-different-ui'
    Copy-FixtureTree -Source $writerPayload -Destination $differentUiRoot
    Set-Utf8Text `
        -Path (Join-Path $differentUiRoot 'sutty.UI.exe') `
        -Content 'different fixture executable bytes'
    Assert-ActionRejected -Name 'package writer rejects a different executed UI binary' -Action {
        & $packageWriter `
            -PackagePath $writerPackage `
            -ObservedUiPath (Join-Path $differentUiRoot 'sutty.UI.exe') `
            -Tag 'v0.1.0-alpha.4' `
            -Commit $commit `
            -EvidenceOutputRoot (Join-Path $scratch 'package-writer-different-ui-output') `
            -StartedAtUtc $writerStartedAt `
            -DurationSeconds 1 `
            -UiStartupResult Pass `
            -AltNavigationSilentResult Pass `
            -AltNavigationShortcutCount 7 `
            -UiShutdownResult Pass *> $null
    }
    $mutatedDllRoot = Join-Path $scratch 'package-writer-mutated-dll'
    Copy-FixtureTree -Source $writerPayload -Destination $mutatedDllRoot
    Set-Utf8Text `
        -Path (Join-Path $mutatedDllRoot 'sutty.UI.dll') `
        -Content 'mutated UI dependency bytes'
    Assert-ActionRejected -Name 'package writer rejects a mutated packaged DLL' -Action {
        & $packageWriter `
            -PackagePath $writerPackage `
            -ObservedUiPath (Join-Path $mutatedDllRoot 'sutty.UI.exe') `
            -Tag 'v0.1.0-alpha.4' `
            -Commit $commit `
            -EvidenceOutputRoot (Join-Path $scratch 'package-writer-mutated-dll-output') `
            -StartedAtUtc $writerStartedAt `
            -DurationSeconds 1 `
            -UiStartupResult Pass `
            -AltNavigationSilentResult Pass `
            -AltNavigationShortcutCount 7 `
            -UiShutdownResult Pass *> $null
    }
    $extraFileRoot = Join-Path $scratch 'package-writer-extra-file'
    Copy-FixtureTree -Source $writerPayload -Destination $extraFileRoot
    Set-Utf8Text -Path (Join-Path $extraFileRoot 'unexpected.txt') -Content 'unexpected file'
    Assert-ActionRejected -Name 'package writer rejects an extra observed-tree file' -Action {
        & $packageWriter `
            -PackagePath $writerPackage `
            -ObservedUiPath (Join-Path $extraFileRoot 'sutty.UI.exe') `
            -Tag 'v0.1.0-alpha.4' `
            -Commit $commit `
            -EvidenceOutputRoot (Join-Path $scratch 'package-writer-extra-file-output') `
            -StartedAtUtc $writerStartedAt `
            -DurationSeconds 1 `
            -UiStartupResult Pass `
            -AltNavigationSilentResult Pass `
            -AltNavigationShortcutCount 7 `
            -UiShutdownResult Pass *> $null
    }
    $missingFileRoot = Join-Path $scratch 'package-writer-missing-file'
    Copy-FixtureTree -Source $writerPayload -Destination $missingFileRoot
    Remove-Item -LiteralPath (Join-Path $missingFileRoot 'Assets\fixture.asset')
    Assert-ActionRejected -Name 'package writer rejects a missing observed-tree file' -Action {
        & $packageWriter `
            -PackagePath $writerPackage `
            -ObservedUiPath (Join-Path $missingFileRoot 'sutty.UI.exe') `
            -Tag 'v0.1.0-alpha.4' `
            -Commit $commit `
            -EvidenceOutputRoot (Join-Path $scratch 'package-writer-missing-file-output') `
            -StartedAtUtc $writerStartedAt `
            -DurationSeconds 1 `
            -UiStartupResult Pass `
            -AltNavigationSilentResult Pass `
            -AltNavigationShortcutCount 7 `
            -UiShutdownResult Pass *> $null
    }
    Assert-ActionRejected -Name 'package writer rejects an incomplete silent navigation Pass' -Action {
        & $packageWriter `
            -PackagePath $writerPackage `
            -ObservedUiPath (Join-Path $writerPayload 'sutty.UI.exe') `
            -Tag 'v0.1.0-alpha.4' `
            -Commit $commit `
            -EvidenceOutputRoot (Join-Path $scratch 'package-writer-incomplete-navigation') `
            -StartedAtUtc $writerStartedAt `
            -DurationSeconds 1 `
            -UiStartupResult Pass `
            -AltNavigationSilentResult Pass `
            -AltNavigationShortcutCount 6 `
            -UiShutdownResult Pass *> $null
    }
    Assert-ActionRejected -Name 'package writer rejects impossible post-startup results' -Action {
        & $packageWriter `
            -PackagePath $writerPackage `
            -ObservedUiPath (Join-Path $writerPayload 'sutty.UI.exe') `
            -Tag 'v0.1.0-alpha.4' `
            -Commit $commit `
            -EvidenceOutputRoot (Join-Path $scratch 'package-writer-impossible-results') `
            -StartedAtUtc $writerStartedAt `
            -DurationSeconds 1 `
            -UiStartupResult Fail `
            -AltNavigationSilentResult Pass `
            -AltNavigationShortcutCount 7 `
            -UiShutdownResult Pass *> $null
    }

    $failedWriterSourceBundle = @(& $packageWriter `
        -PackagePath $writerPackage `
        -ObservedUiPath (Join-Path $writerPayload 'sutty.UI.exe') `
        -Tag 'v0.1.0-alpha.4' `
        -Commit $commit `
        -EvidenceOutputRoot (Join-Path $scratch 'package-writer-failed-output') `
        -StartedAtUtc $writerStartedAt `
        -DurationSeconds 1 `
        -UiStartupResult Fail `
        -AltNavigationSilentResult Blocked `
        -AltNavigationShortcutCount 0 `
        -UiShutdownResult Blocked | Select-Object -Last 1)[0]
    $failedReviewedBundle = @(& $evidenceReviewer `
        -SourceManifestPath (Join-Path $failedWriterSourceBundle 'manifest.yml') `
        -DestinationRoot (Join-Path $scratch 'package-writer-failed-reviewed') `
        -ReviewerId 'github-package-reviewer' `
        -ReviewedAtUtc ([DateTimeOffset]::UtcNow.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            [Globalization.CultureInfo]::InvariantCulture)) `
        -PrivacyReview Confirmed `
        -ManualObservationReview Confirmed `
        -ExpectedCommit $commit `
        -ExpectedPackageSha256 $writerPackageSha256 `
        -RequiredGateId PKG-001 `
        -RequiredResult Fail | Select-Object -Last 1)[0]
    $script:caseCount++
    $failedWriterValidation = Get-ValidationFailure `
        -Manifest (Join-Path $failedReviewedBundle 'manifest.yml') `
        -ExpectedCommit $commit `
        -ExpectedPackageSha256 $writerPackageSha256 `
        -RequiredGateId PKG-001 `
        -RequiredResult Fail
    if ($null -ne $failedWriterValidation) {
        throw "Reviewed failed package output should remain valid evidence: $failedWriterValidation"
    }
    $script:caseCount++
    if ($null -eq (Get-ValidationFailure `
            -Manifest (Join-Path $failedReviewedBundle 'manifest.yml') `
            -ExpectedCommit $commit `
            -ExpectedPackageSha256 $writerPackageSha256 `
            -RequiredGateId PKG-001 `
            -RequiredResult Pass)) {
        throw 'A reviewed PKG-001 Fail bundle satisfied the release Pass binding.'
    }

    Write-Host "Live-evidence guard self-tests passed ($caseCount accepted/rejected fixture cases)."
}
finally {
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ((Test-Path -LiteralPath $resolvedScratch) -and
        $resolvedScratch.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith(
            'sutty-live-evidence-tests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
