[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Expect-Failure {
    param(
        [Parameter(Mandatory)][scriptblock]$Operation,
        [Parameter(Mandatory)][string]$Message
    )

    try {
        & $Operation
    }
    catch {
        return
    }
    throw $Message
}

function Write-Utf8 {
    param([string]$Path, [string]$Content)

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$reviewScript = Join-Path $repositoryRoot '.github\scripts\Review-LiveEvidence.ps1'
$validator = Join-Path $repositoryRoot '.github\scripts\Assert-LiveEvidence.ps1'
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $temporaryBase "sutty-live-review-test-$([Guid]::NewGuid().ToString('N'))"
$sourceBundle = Join-Path $scratch 'candidate\ssh-info-001-source'
$destinationRoot = Join-Path $scratch 'reviewed'

try {
    [IO.Directory]::CreateDirectory($sourceBundle) | Out-Null
    $commit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $packageSha256 = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $manifest = @"
schema_version: 1
gate_id: "SSH-INFO-001"
commit: "$commit"
package_sha256: "$packageSha256"
windows_build: "10.0.26200.9168"
architecture: "x64"
server_family: "OpenSSH"
server_version: "9.6p1"
route: "Direct"
authentication: "Password"
expected_host_fingerprint: "SHA256:[redacted]"
result: "Pass"
started_at_utc: "2026-08-21T00:00:00.000Z"
duration_seconds: 20
evidence_files:
  - "summary.json"
redaction_reviewed: false
"@ -replace "`r`n", "`n"
    $summary = @"
{
  "schema_version": 1,
  "gate_id": "SSH-INFO-001",
  "result": "Pass",
  "started_at_utc": "2026-08-21T00:00:00.000Z",
  "duration_seconds": 20,
  "check_id": "complete",
  "checks": [
    {
      "id": "connection-info",
      "result": "Pass"
    }
  ],
  "measurements": {
    "check_count": 1,
    "passed_count": 1,
    "failed_count": 0,
    "blocked_count": 0
  },
  "redaction_reviewed": false,
  "privacy_notice": "Connection identifiers, credentials, filesystem locations, session content, and cryptographic material are excluded."
}
"@ -replace "`r`n", "`n"
    $sourceManifest = Join-Path $sourceBundle 'manifest.yml'
    $sourceSummary = Join-Path $sourceBundle 'summary.json'
    Write-Utf8 -Path $sourceManifest -Content $manifest
    Write-Utf8 -Path $sourceSummary -Content $summary
    $manifestBefore = Get-Sha256 $sourceManifest
    $summaryBefore = Get-Sha256 $sourceSummary
    $reviewedAt = '2026-08-21T01:00:00.000Z'

    $reviewOutput = @(& $reviewScript `
            -SourceManifestPath $sourceManifest `
            -DestinationRoot $destinationRoot `
            -ReviewerId 'github-sutty-reviewer' `
            -ReviewedAtUtc $reviewedAt `
            -PrivacyReview Confirmed `
            -ExpectedCommit $commit `
            -ExpectedPackageSha256 $packageSha256 `
            -RequiredResult Pass)
    $reviewedBundle = $reviewOutput[-1]
    Assert-True (Test-Path -LiteralPath $reviewedBundle -PathType Container) `
        'The reviewed bundle was not atomically published.'
    Assert-True ((Get-Sha256 $sourceManifest) -ceq $manifestBefore -and
        (Get-Sha256 $sourceSummary) -ceq $summaryBefore) `
        'The review command modified its unreviewed source bundle.'

    $reviewedManifest = Get-Content -LiteralPath (Join-Path $reviewedBundle 'manifest.yml') -Raw
    $reviewedSummary = Get-Content -LiteralPath (Join-Path $reviewedBundle 'summary.json') -Raw
    Assert-True ($reviewedManifest.Contains('  - "review.json"', [StringComparison]::Ordinal) -and
        $reviewedManifest.Contains('redaction_reviewed: true', [StringComparison]::Ordinal) -and
        $reviewedSummary.Contains('"redaction_reviewed": true', [StringComparison]::Ordinal)) `
        'The new reviewed bundle does not contain the canonical review promotion markers.'

    $review = Get-Content -LiteralPath (Join-Path $reviewedBundle 'review.json') -Raw |
        ConvertFrom-Json -DateKind String
    Assert-True ($review.schema_version -eq 1 -and
        $review.reviewer_id -ceq 'github-sutty-reviewer' -and
        $review.reviewed_at_utc -ceq $reviewedAt -and
        $review.review_scope.Count -eq 2 -and
        $review.review_scope[0] -ceq 'privacy-redaction' -and
        $review.review_scope[1] -ceq 'bundle-integrity') `
        'The review record identity or scope is not canonical.'
    Assert-True ($review.source_files.Count -eq 2 -and
        $review.source_files[0].name -ceq 'manifest.yml' -and
        $review.source_files[0].sha256 -ceq $manifestBefore -and
        $review.source_files[1].name -ceq 'summary.json' -and
        $review.source_files[1].sha256 -ceq $summaryBefore) `
        'The review record does not bind the exact source files in ordinal order.'
    $canonical = [Text.StringBuilder]::new()
    foreach ($file in $review.source_files) {
        $null = $canonical.Append($file.sha256).Append(' ').Append(
            ([long]$file.size_bytes).ToString([Globalization.CultureInfo]::InvariantCulture)).Append(
            ' ').Append($file.name).Append("`n")
    }
    $expectedBundleSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.UTF8Encoding]::new($false).GetBytes($canonical.ToString()))).ToLowerInvariant()
    Assert-True ($review.source_bundle_sha256 -ceq $expectedBundleSha256) `
        'The review record aggregate source digest is not canonical.'

    & $validator `
        -ManifestPath (Join-Path $reviewedBundle 'manifest.yml') `
        -ExpectedCommit $commit `
        -ExpectedPackageSha256 $packageSha256 `
        -RequiredResult Pass

    Expect-Failure {
        & $reviewScript `
            -SourceManifestPath $sourceManifest `
            -DestinationRoot $destinationRoot `
            -ReviewerId 'github-sutty-reviewer' `
            -ReviewedAtUtc $reviewedAt `
            -PrivacyReview Confirmed
    } 'A second promotion was allowed to overwrite an existing reviewed bundle.'
    Expect-Failure {
        & $reviewScript `
            -SourceManifestPath (Join-Path $reviewedBundle 'manifest.yml') `
            -DestinationRoot (Join-Path $scratch 'second-review') `
            -ReviewerId 'github-sutty-reviewer' `
            -ReviewedAtUtc $reviewedAt `
            -PrivacyReview Confirmed
    } 'An already reviewed bundle was accepted as an unreviewed source.'
    Expect-Failure {
        & $reviewScript `
            -SourceManifestPath $sourceManifest `
            -DestinationRoot (Join-Path $scratch 'invalid-reviewer') `
            -ReviewerId 'github-reviewer-' `
            -ReviewedAtUtc $reviewedAt `
            -PrivacyReview Confirmed
    } 'A noncanonical reviewer identity was accepted.'

    Write-Host 'Review-LiveEvidence focused tests passed.'
}
finally {
    if (Test-Path -LiteralPath $scratch -PathType Container) {
        $scratchItem = Get-Item -LiteralPath $scratch -Force
        if (($scratchItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not $scratchItem.FullName.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
            $scratchItem.Name -cnotmatch '^sutty-live-review-test-[0-9a-f]{32}$') {
            throw 'Refusing to remove an unverified review-test scratch directory.'
        }
        [IO.Directory]::Delete($scratchItem.FullName, $true)
    }
}
