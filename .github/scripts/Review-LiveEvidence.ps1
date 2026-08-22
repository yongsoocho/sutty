[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceManifestPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$DestinationRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^github-[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$')]
    [string]$ReviewerId,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$')]
    [string]$ReviewedAtUtc,

    [Parameter(Mandatory)]
    [ValidateSet('Confirmed')]
    [string]$PrivacyReview,

    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedCommit,

    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedPackageSha256,

    [ValidatePattern('^(?=.{1,64}$)[A-Z0-9]+(?:-[A-Z0-9]+)+$')]
    [string]$RequiredGateId,

    [ValidateSet('Pass', 'Fail', 'Blocked')]
    [string]$RequiredResult
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8Strict = [Text.UTF8Encoding]::new($false, $true)
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Assert-PhysicalAncestors {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    $current = Get-Item -LiteralPath $Path -Force
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description traverses a symbolic link or reparse point."
        }
        $current = $current.Parent
    }
}

function Get-StrictUtf8Text {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$MaximumBytes,
        [Parameter(Mandatory)][string]$Description
    )

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -gt $MaximumBytes) {
        throw "$Description is outside the physical bounded review contract."
    }
    $bytes = [IO.File]::ReadAllBytes($item.FullName)
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xef -and $bytes[1] -eq 0xbb -and $bytes[2] -eq 0xbf) {
        throw "$Description must be UTF-8 without a byte-order mark."
    }
    try {
        return $utf8Strict.GetString($bytes)
    }
    catch {
        throw "$Description is not strict UTF-8."
    }
}

function Write-DurableText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content,
        [switch]$Replace
    )

    $mode = if ($Replace) { [IO.FileMode]::Create } else { [IO.FileMode]::CreateNew }
    $stream = [IO.FileStream]::new(
        $Path,
        $mode,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        65536,
        [IO.FileOptions]::WriteThrough)
    try {
        $bytes = $utf8NoBom.GetBytes($Content)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Get-LowerSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ManifestEvidenceFiles {
    param([Parameter(Mandatory)][string]$ManifestText)

    $files = [Collections.Generic.List[string]]::new()
    $inEvidenceFiles = $false
    foreach ($line in [regex]::Split($ManifestText, '\r?\n')) {
        if ($line -ceq 'evidence_files:') {
            if ($inEvidenceFiles) {
                throw 'The source manifest contains duplicate evidence_files fields.'
            }
            $inEvidenceFiles = $true
            continue
        }
        if ($inEvidenceFiles -and $line -cmatch '^  - (?<token>"(?:[^"\\]|\\.)*")$') {
            $value = $Matches.token | ConvertFrom-Json
            if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) {
                throw 'The source manifest contains an invalid evidence file item.'
            }
            $files.Add($value)
            continue
        }
        if ($inEvidenceFiles -and $line -match '^[a-z]') {
            $inEvidenceFiles = $false
        }
    }
    if ($files.Count -eq 0) {
        throw 'The source manifest declares no evidence files.'
    }
    return $files.ToArray()
}

function Get-ManifestStringValue {
    param(
        [Parameter(Mandatory)][string]$ManifestText,
        [Parameter(Mandatory)][string]$Name
    )

    $pattern = '(?m)^{0}: (?<token>"(?:[^"\\]|\\.)*")\r?$' -f
        [regex]::Escape($Name)
    $matches = [regex]::Matches($ManifestText, $pattern)
    if ($matches.Count -ne 1) {
        throw "The source manifest must contain exactly one canonical $Name field."
    }
    $value = $matches[0].Groups['token'].Value | ConvertFrom-Json
    if ($value -isnot [string]) {
        throw "The source manifest $Name field must be a string."
    }
    return $value
}

$reviewedTimestamp = [DateTimeOffset]::MinValue
$timestampStyles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
    [Globalization.DateTimeStyles]::AdjustToUniversal
if (-not [DateTimeOffset]::TryParse(
        $ReviewedAtUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        $timestampStyles,
        [ref]$reviewedTimestamp) -or
    $reviewedTimestamp.Offset -ne [TimeSpan]::Zero) {
    throw 'ReviewedAtUtc must be a valid RFC3339 UTC timestamp ending in Z.'
}

if (-not (Test-Path -LiteralPath $SourceManifestPath -PathType Leaf)) {
    throw 'The source evidence manifest is missing.'
}
$sourceManifest = (Resolve-Path -LiteralPath $SourceManifestPath).Path
if ([IO.Path]::GetFileName($sourceManifest) -cne 'manifest.yml') {
    throw 'The source evidence manifest must be named exactly manifest.yml.'
}
$sourceRoot = (Get-Item -LiteralPath (Split-Path -Parent $sourceManifest) -Force).FullName
Assert-PhysicalAncestors -Path $sourceRoot -Description 'Source evidence bundle'

if (-not [IO.Path]::IsPathFullyQualified($DestinationRoot)) {
    throw 'DestinationRoot must be an absolute path.'
}
$resolvedDestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
if ($resolvedDestinationRoot.TrimEnd('\', '/') -ceq
    ([IO.Path]::GetPathRoot($resolvedDestinationRoot)).TrimEnd('\', '/')) {
    throw 'DestinationRoot must not be a filesystem root.'
}
[IO.Directory]::CreateDirectory($resolvedDestinationRoot) | Out-Null
$resolvedDestinationRoot = (Get-Item -LiteralPath $resolvedDestinationRoot -Force).FullName
Assert-PhysicalAncestors -Path $resolvedDestinationRoot -Description 'Reviewed evidence destination'
$sourcePrefix = $sourceRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($resolvedDestinationRoot -ceq $sourceRoot -or
    $resolvedDestinationRoot.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DestinationRoot must not be the source bundle or one of its descendants.'
}

$manifestText = Get-StrictUtf8Text `
    -Path $sourceManifest `
    -MaximumBytes 1048576 `
    -Description 'Source manifest'
$manifestReviewMatches = [regex]::Matches(
    $manifestText,
    '(?m)^redaction_reviewed: false\r?$')
if ($manifestReviewMatches.Count -ne 1 -or
    [regex]::IsMatch($manifestText, '(?m)^redaction_reviewed: true\r?$')) {
    throw 'Only a canonical unreviewed source manifest can be promoted.'
}
$evidenceFiles = @(Get-ManifestEvidenceFiles -ManifestText $manifestText)
if (@($evidenceFiles | Where-Object { $_ -ceq 'summary.json' }).Count -ne 1 -or
    @($evidenceFiles | Where-Object { $_ -ceq 'review.json' }).Count -ne 0) {
    throw 'The source bundle must declare summary.json once and must not already declare review.json.'
}
$gateId = Get-ManifestStringValue -ManifestText $manifestText -Name 'gate_id'

$summaryPath = Join-Path $sourceRoot 'summary.json'
if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
    throw 'The source bundle summary is missing.'
}
$summaryText = Get-StrictUtf8Text `
    -Path $summaryPath `
    -MaximumBytes 1048576 `
    -Description 'Source summary'
$summaryReviewMatches = [regex]::Matches(
    $summaryText,
    '(?m)^\s*"redaction_reviewed": false,?\r?$')
if ($summaryReviewMatches.Count -ne 1 -or
    [regex]::IsMatch($summaryText, '(?m)^\s*"redaction_reviewed": true,?\r?$')) {
    throw 'Only a canonical unreviewed source summary can be promoted.'
}
$summaryDocument = [Text.Json.JsonDocument]::Parse($summaryText)
try {
    $sourceStartedText = $summaryDocument.RootElement.GetProperty('started_at_utc').GetString()
    $sourceDurationSeconds = $summaryDocument.RootElement.GetProperty('duration_seconds').GetInt64()
    $sourceStarted = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            $sourceStartedText,
            [Globalization.CultureInfo]::InvariantCulture,
            $timestampStyles,
            [ref]$sourceStarted) -or
        $sourceDurationSeconds -lt 0 -or
        $reviewedTimestamp -lt $sourceStarted.AddSeconds($sourceDurationSeconds)) {
        throw 'ReviewedAtUtc must be at or after the source evidence run completed.'
    }
    if ($reviewedTimestamp -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw 'ReviewedAtUtc must not be more than five minutes in the future.'
    }
}
finally {
    $summaryDocument.Dispose()
}

$sourceDirectories = @(Get-ChildItem -LiteralPath $sourceRoot -Directory -Recurse -Force)
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force)
if (@($sourceDirectories + $sourceFiles | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        }).Count -gt 0) {
    throw 'The source bundle must not contain symbolic links or reparse points.'
}

$declaredNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$declaredNames.Add('manifest.yml') | Out-Null
foreach ($name in $evidenceFiles) {
    if (-not $declaredNames.Add($name)) {
        throw 'The source manifest contains duplicate case-insensitive evidence paths.'
    }
}
$sourceItemByName = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::Ordinal)
foreach ($sourceFile in $sourceFiles) {
    $relativeName = [IO.Path]::GetRelativePath($sourceRoot, $sourceFile.FullName).Replace('\', '/')
    $sourceItemByName.Add($relativeName, $sourceFile)
}
$sourceNames = [string[]]$sourceItemByName.Keys
[Array]::Sort($sourceNames, [StringComparer]::Ordinal)
$sourceItems = @($sourceNames | ForEach-Object {
        [pscustomobject]@{
            Item = $sourceItemByName[$_]
            Name = $_
        }
    })
if ($sourceItems.Count -ne $declaredNames.Count -or
    @($sourceItems | Where-Object { -not $declaredNames.Contains($_.Name) }).Count -gt 0) {
    throw 'The source bundle file inventory does not exactly match its manifest.'
}

$sourceRecords = [Collections.Generic.List[object]]::new()
$bundleDigestText = [Text.StringBuilder]::new()
foreach ($sourceItem in $sourceItems) {
    $digest = Get-LowerSha256 -Path $sourceItem.Item.FullName
    $size = [long]$sourceItem.Item.Length
    $sourceRecords.Add([ordered]@{
            name = $sourceItem.Name
            sha256 = $digest
            size_bytes = $size
        })
    $null = $bundleDigestText.Append($digest).Append(' ').Append(
        $size.ToString([Globalization.CultureInfo]::InvariantCulture)).Append(' ').Append(
        $sourceItem.Name).Append("`n")
}
$sourceBundleSha256 = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData(
        $utf8NoBom.GetBytes($bundleDigestText.ToString()))).ToLowerInvariant()

$reviewDocument = [ordered]@{
    schema_version = 1
    reviewer_id = $ReviewerId
    reviewed_at_utc = $ReviewedAtUtc
    source_bundle_sha256 = $sourceBundleSha256
    source_files = $sourceRecords.ToArray()
    review_scope = @('privacy-redaction', 'bundle-integrity')
}
$reviewJson = ($reviewDocument | ConvertTo-Json -Depth 5) + "`n"

$bundleSuffix = "reviewed-$($sourceBundleSha256.Substring(0, 12))"
$maximumGateLength = 64 - $bundleSuffix.Length - 1
$gateSegment = $gateId.ToLowerInvariant()
if ($gateSegment.Length -gt $maximumGateLength) {
    $gateSegment = $gateSegment.Substring(0, $maximumGateLength).TrimEnd('-')
}
$bundleName = "$gateSegment-$bundleSuffix"
$finalPath = Join-Path $resolvedDestinationRoot $bundleName
$identifier = [Guid]::NewGuid().ToString('N')
$stagingPath = Join-Path $resolvedDestinationRoot ".sutty-evidence-review-staging-$identifier"
if (Test-Path -LiteralPath $finalPath) {
    throw 'The reviewed evidence bundle already exists; existing evidence is immutable.'
}
if (Test-Path -LiteralPath $stagingPath) {
    throw 'The fresh reviewed evidence staging path already exists.'
}

[IO.Directory]::CreateDirectory($stagingPath) | Out-Null
try {
    foreach ($sourceItem in $sourceItems) {
        $target = [IO.Path]::GetFullPath((Join-Path $stagingPath $sourceItem.Name))
        $stagingPrefix = $stagingPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $target.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A source evidence path escaped the reviewed staging directory.'
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($sourceItem.Item.FullName, $target, $false)
        if ((Get-LowerSha256 -Path $sourceItem.Item.FullName) -cne
                (Get-LowerSha256 -Path $target) -or
            $sourceItem.Item.Length -ne (Get-Item -LiteralPath $target).Length) {
            throw 'A source evidence file changed while the reviewed bundle was staged.'
        }
    }

    $newline = if ($manifestText.Contains("`r`n", [StringComparison]::Ordinal)) {
        "`r`n"
    }
    else {
        "`n"
    }
    $manifestReplacement = '  - "review.json"' + $newline + 'redaction_reviewed: true'
    $reviewedManifest = [regex]::Replace(
        $manifestText,
        '(?m)^redaction_reviewed: false\r?$',
        [Text.RegularExpressions.MatchEvaluator]{ param($match) $manifestReplacement },
        1)
    $reviewedSummary = [regex]::Replace(
        $summaryText,
        '(?m)(^\s*"redaction_reviewed": )false(?=,?\r?$)',
        '${1}true',
        1)
    Write-DurableText -Path (Join-Path $stagingPath 'manifest.yml') `
        -Content $reviewedManifest -Replace
    Write-DurableText -Path (Join-Path $stagingPath 'summary.json') `
        -Content $reviewedSummary -Replace
    Write-DurableText -Path (Join-Path $stagingPath 'review.json') -Content $reviewJson

    $validatorPath = Join-Path $PSScriptRoot 'Assert-LiveEvidence.ps1'
    if (-not (Test-Path -LiteralPath $validatorPath -PathType Leaf)) {
        throw 'The live-evidence validator is unavailable.'
    }
    $validatorParameters = @{
        ManifestPath = Join-Path $stagingPath 'manifest.yml'
    }
    foreach ($optional in @(
            @{ Name = 'ExpectedCommit'; Value = $ExpectedCommit },
            @{ Name = 'ExpectedPackageSha256'; Value = $ExpectedPackageSha256 },
            @{ Name = 'RequiredGateId'; Value = $RequiredGateId },
            @{ Name = 'RequiredResult'; Value = $RequiredResult })) {
        if (-not [string]::IsNullOrWhiteSpace($optional.Value)) {
            $validatorParameters[$optional.Name] = $optional.Value
        }
    }
    & $validatorPath @validatorParameters

    [IO.Directory]::Move($stagingPath, $finalPath)
}
catch {
    if (Test-Path -LiteralPath $stagingPath -PathType Container) {
        $stagingItem = Get-Item -LiteralPath $stagingPath -Force
        $expectedPrefix = $resolvedDestinationRoot.TrimEnd('\', '/') +
            [IO.Path]::DirectorySeparatorChar + '.sutty-evidence-review-staging-'
        if (($stagingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
            $stagingItem.FullName.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            [IO.Directory]::Delete($stagingItem.FullName, $true)
        }
    }
    throw
}

Write-Host "Reviewed live-evidence bundle created: $finalPath"
Write-Output $finalPath
