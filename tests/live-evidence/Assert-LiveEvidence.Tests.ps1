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
    return $summary | ConvertTo-Json -Depth 10 -Compress
}

function New-SshLive001Checks {
    foreach ($id in @(
        'package-sha256',
        'package-commit-identity',
        'package-core-identity',
        'authentication-success',
        'authentication-rejection',
        'host-key-rejection',
        'connection-cancellation',
        'transport-timeout',
        'negotiated-reconnect',
        'command-pty-sftp',
        'remote-local-cleanup',
        'server-session-audit')) {
        [ordered]@{ id = $id; result = 'Pass' }
    }
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
        [switch]$SkipSummary
    )

    $root = Join-Path $scratch $Name
    if ([string]::IsNullOrWhiteSpace($RelativeBundle)) {
        $RelativeBundle = "alpha4/ssh-primary/$Name"
    }
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
        if ($entry.Key -cin $Omit) {
            continue
        }
        if ($entry.Key -cin @('schema_version', 'duration_seconds', 'redaction_reviewed')) {
            $lines.Add("$($entry.Key): $($entry.Value)")
        }
        else {
            $lines.Add("$($entry.Key): $(ConvertTo-QuotedScalar $entry.Value)")
        }
    }
    if ('evidence_files' -cnotin $Omit) {
        $lines.Add('evidence_files:')
        foreach ($evidenceFile in $EvidenceFiles) {
            $lines.Add("  - $(ConvertTo-QuotedScalar $evidenceFile)")
        }
    }
    $manifestPath = Join-Path $bundle 'manifest.yml'
    Set-Utf8Text -Path $manifestPath -Content (
        [string]::Join([Environment]::NewLine, $lines) + [Environment]::NewLine)

    if (-not $SkipSummary) {
        Set-Utf8Text -Path (Join-Path $bundle 'summary.json') -Content $Summary
    }

    return @{
        Root = $root
        Bundle = $bundle
        Manifest = $manifestPath
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
    Assert-Accepted -Fixture $canonical -Name 'expected commit, package, and Pass binding' -Direct -RequiredResult Pass
    $releaseGateSummary = New-CanonicalSummary `
        -GateId 'SSH-LIVE-001' `
        -Checks @(New-SshLive001Checks)
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
    }
    $missingReleaseChecks = @(New-SshLive001Checks | Select-Object -Skip 1)
    $missingReleaseGate = New-EvidenceBundle `
        'release-gate-missing-check' `
        -Overrides @{ gate_id = 'SSH-LIVE-001'; authentication = 'Password' } `
        -Summary (New-CanonicalSummary -GateId 'SSH-LIVE-001' -Checks $missingReleaseChecks)
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
        -Summary (New-CanonicalSummary -GateId 'SSH-LIVE-001' -Checks $extraReleaseChecks)
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
        -Summary (New-CanonicalSummary -GateId 'SSH-LIVE-001' -Checks $duplicateReleaseChecks)
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
        -Summary (New-CanonicalSummary -GateId 'SSH-LIVE-001' -Checks $failedReleaseChecks)
    Assert-Rejected `
        -Fixture $failedReleaseGate `
        -Name 'failed check does not satisfy SSH-LIVE-001 Pass profile' `
        -Direct `
        -RequiredGateId 'SSH-LIVE-001' `
        -RequiredResult Pass
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
    Assert-Accepted -Fixture $attachments -Name 'safe UTF-8 text and structurally validated PNG attachments'

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
        @{ Name = 'schema'; Summary = (New-CanonicalSummary).Replace('"schema_version":1', '"schema_version":2') },
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
    $duplicateNestedJson = [regex]::Replace(
        (New-CanonicalSummary),
        '"id":"smoke","result":"Pass"',
        '"id":"smoke","result":"Pass","result":"Pass"',
        1)
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

    $junctionFixture = New-EvidenceBundle 'junction-path' -EvidenceFiles @('summary.json', 'linked/detail.json')
    $junctionTarget = Join-Path $scratch 'junction-target'
    New-Item -ItemType Directory -Path $junctionTarget -Force | Out-Null
    Set-Utf8Text -Path (Join-Path $junctionTarget 'detail.json') -Content '{}'
    New-Item -ItemType Junction -Path (Join-Path $junctionFixture.Bundle 'linked') -Target $junctionTarget | Out-Null
    Assert-Rejected -Fixture $junctionFixture -Name 'reparse-point traversal'

    $junctionBundleTarget = New-EvidenceBundle 'junction-bundle-target'
    [System.IO.File]::WriteAllBytes($junctionBundleTarget.Manifest, [byte[]](0xff, 0xfe, 0xfd))
    $junctionRoot = Join-Path $scratch 'junction-bundle-root'
    $junctionScope = Join-Path $junctionRoot 'alpha4\ssh-primary'
    New-Item -ItemType Directory -Path $junctionScope -Force | Out-Null
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
