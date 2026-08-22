[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$AttestationPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateManifestPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Repository,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Tag,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateCommit,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateRunId,

    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$CandidateRunAttempt,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateArtifactId,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateArtifactName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateArtifactDigest,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$AcceptanceCommit,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$EvidenceManifestRepositoryPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PromotionRunId,

    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$PromotionRunAttempt,

    [switch]$WriteAttestation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$schemaVersion = 1
$requiredGateId = 'SSH-LIVE-001'
$alphaTagPattern = '^v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+$'
$commitPattern = '^[0-9a-f]{40}$'
$sha256Pattern = '^[0-9a-f]{64}$'
$artifactDigestPattern = '^sha256:[0-9a-f]{64}$'
$positiveIdPattern = '^[1-9][0-9]*$'
$githubReviewerPattern = '^github-[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$'
$timestampPattern = '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$'
$evidencePathPattern = '^docs/evidence/alpha[0-9]+/[a-z0-9][a-z0-9-]{0,63}/[a-z0-9][a-z0-9-]{0,63}/manifest\.yml$'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Read-StrictUtf8 {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        throw "$Description must not be a reparse point."
    }

    $bytes = [System.IO.File]::ReadAllBytes($item.FullName)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "$Description must be UTF-8 without a byte-order mark."
    }
    try {
        return [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    }
    catch {
        throw "$Description is not valid UTF-8."
    }
}

function Read-StrictJson {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    $text = Read-StrictUtf8 -Path $Path -Description $Description
    try {
        $options = [System.Text.Json.JsonDocumentOptions]::new()
        $options.AllowTrailingCommas = $false
        $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
        return [System.Text.Json.JsonDocument]::Parse($text, $options)
    }
    catch {
        throw "$Description is not strict JSON: $($_.Exception.Message)"
    }
}

function Assert-NoDuplicateJsonProperties {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw "$Description contains duplicate property '$($property.Name)'."
            }
            Assert-NoDuplicateJsonProperties `
                -Element $property.Value `
                -Description "$Description.$($property.Name)"
        }
    }
    elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($value in $Element.EnumerateArray()) {
            Assert-NoDuplicateJsonProperties -Element $value -Description "$Description[$index]"
            $index++
        }
    }
}

function Assert-ExactJsonProperties {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "$Description must be a JSON object."
    }
    $actual = @($Element.EnumerateObject() | ForEach-Object { $_.Name })
    if ([string]::Join('|', ($actual | Sort-Object)) -cne
        [string]::Join('|', ($Expected | Sort-Object))) {
        throw "$Description properties do not match the exact schema."
    }
}

function Get-JsonString {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description
    )

    $element = $Object.GetProperty($Name)
    if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
        throw "$Description.$Name must be a JSON string."
    }
    return $element.GetString()
}

function Get-JsonInt32 {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description,
        [int]$Minimum = 0
    )

    $element = $Object.GetProperty($Name)
    $value = 0
    if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        $element.GetRawText() -cnotmatch '^(?:0|[1-9][0-9]*)$' -or
        -not $element.TryGetInt32([ref]$value) -or $value -lt $Minimum) {
        throw "$Description.$Name must be a canonical JSON integer greater than or equal to $Minimum."
    }
    return $value
}

function Get-JsonInt64 {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description,
        [long]$Minimum = 0
    )

    $element = $Object.GetProperty($Name)
    $value = [long]0
    if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        $element.GetRawText() -cnotmatch '^(?:0|[1-9][0-9]*)$' -or
        -not $element.TryGetInt64([ref]$value) -or $value -lt $Minimum) {
        throw "$Description.$Name must be a canonical JSON integer greater than or equal to $Minimum."
    }
    return $value
}

function Assert-LowerSha256 {
    param([string]$Value, [string]$Description)

    if ($Value -cnotmatch $sha256Pattern -or $Value -cmatch '^0{64}$') {
        throw "$Description must be a nonzero lowercase SHA-256 digest."
    }
}

function Assert-Commit {
    param([string]$Value, [string]$Description)

    if ($Value -cnotmatch $commitPattern -or $Value -cmatch '^0{40}$') {
        throw "$Description must be a nonzero 40-character lowercase Git object ID."
    }
}

function Assert-Rfc3339Utc {
    param([string]$Value, [string]$Description)

    $parsed = [DateTimeOffset]::MinValue
    if ($Value -cnotmatch $timestampPattern -or
        -not [DateTimeOffset]::TryParse(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor
                [Globalization.DateTimeStyles]::AdjustToUniversal,
            [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero) {
        throw "$Description must be an RFC3339 UTC timestamp ending in Z."
    }
}

function Get-LowerSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-FileRecord {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Path
    )

    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -and
        -not $item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        return [ordered]@{
            name = $Name
            sha256 = Get-LowerSha256 -Path $item.FullName
            size_bytes = [long]$item.Length
        }
    }
    throw "Release input file must be a regular non-reparse file: $Path"
}

function New-ReviewSourceCandidate {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    return [ordered]@{
        sha256 = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
        size_bytes = [long]$Bytes.Length
    }
}

function Get-ReviewSourceCandidates {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$BundleDirectory
    )

    $bundlePrefix = $BundleDirectory.TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    $path = [System.IO.Path]::GetFullPath((Join-Path `
        $BundleDirectory `
        $Name.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
    if (-not $path.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Review source file is missing or escapes its evidence bundle: $Name"
    }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        throw "Review source file must not be a reparse point: $Name"
    }

    if ($Name -cnotin @('manifest.yml', 'summary.json')) {
        return @(New-ReviewSourceCandidate -Bytes ([System.IO.File]::ReadAllBytes($item.FullName)))
    }

    $text = Read-StrictUtf8 -Path $item.FullName -Description "reviewed $Name"
    $sourceTexts = [System.Collections.Generic.List[string]]::new()
    if ($Name -ceq 'manifest.yml') {
        $reviewMarker = [regex]::new(
            '(?m)^  - "review\.json"(?<marker>\r\n|\n)redaction_reviewed: true(?<tail>\n|\z)')
        $reviewMatches = @($reviewMarker.Matches($text))
        if ($reviewMatches.Count -ne 1) {
            throw 'Reviewed manifest is not an exact Review-LiveEvidence.ps1 transformation.'
        }
        $match = $reviewMatches[0]
        $prefix = $text.Substring(0, $match.Index)
        $suffix = $text.Substring($match.Index + $match.Length)
        $lineEndings = if ($match.Groups['tail'].Value.Length -eq 0) {
            @('')
        }
        elseif ($match.Groups['marker'].Value -ceq "`r`n") {
            @("`n", "`r`n")
        }
        else {
            @("`n")
        }

        $sourcePattern = [regex]::new('(?m)^redaction_reviewed: false\r?$')
        foreach ($lineEnding in $lineEndings) {
            $sourceText = $prefix + 'redaction_reviewed: false' + $lineEnding + $suffix
            if (@($sourcePattern.Matches($sourceText)).Count -ne 1 -or
                [regex]::IsMatch($sourceText, '(?m)^redaction_reviewed: true\r?$')) {
                continue
            }
            $writerNewline = if ($sourceText.Contains("`r`n", [StringComparison]::Ordinal)) {
                "`r`n"
            }
            else {
                "`n"
            }
            $replacement = '  - "review.json"' + $writerNewline + 'redaction_reviewed: true'
            if ($sourcePattern.Replace($sourceText, $replacement, 1) -ceq $text) {
                $sourceTexts.Add($sourceText)
            }
        }
    }
    else {
        $reviewedPattern = [regex]::new(
            '(?m)(^\s*"redaction_reviewed": )true(?=,?\r?$)')
        if (@($reviewedPattern.Matches($text)).Count -ne 1) {
            throw 'Reviewed summary is not an exact Review-LiveEvidence.ps1 transformation.'
        }
        $sourceText = $reviewedPattern.Replace($text, '${1}false', 1)
        $sourcePattern = [regex]::new(
            '(?m)(^\s*"redaction_reviewed": )false(?=,?\r?$)')
        if (@($sourcePattern.Matches($sourceText)).Count -eq 1 -and
            $sourcePattern.Replace($sourceText, '${1}true', 1) -ceq $text) {
            $sourceTexts.Add($sourceText)
        }
    }

    if ($sourceTexts.Count -eq 0) {
        throw "$Name cannot be reversed to bytes accepted by Review-LiveEvidence.ps1."
    }
    $candidates = [System.Collections.Generic.List[object]]::new()
    $identities = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sourceText in $sourceTexts) {
        $candidate = New-ReviewSourceCandidate -Bytes $utf8NoBom.GetBytes($sourceText)
        $identity = "$($candidate.sha256):$($candidate.size_bytes)"
        if ($identities.Add($identity)) {
            $candidates.Add($candidate)
        }
    }
    return @($candidates)
}

function Get-EvidenceCompletionUtc {
    param([Parameter(Mandatory)][string]$ManifestPath)

    $text = Read-StrictUtf8 -Path $ManifestPath -Description 'reviewed evidence manifest'
    $startedMatches = [regex]::Matches(
        $text,
        '(?m)^started_at_utc: "(?<value>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z)"\r?$')
    $durationMatches = [regex]::Matches(
        $text,
        '(?m)^duration_seconds: (?<value>0|[1-9][0-9]*)\r?$')
    if ($startedMatches.Count -ne 1 -or $durationMatches.Count -ne 1) {
        throw 'Reviewed evidence manifest lacks one canonical execution interval.'
    }
    $startedAt = [DateTimeOffset]::MinValue
    $durationSeconds = [long]0
    if (-not [DateTimeOffset]::TryParse(
            $startedMatches[0].Groups['value'].Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor
                [Globalization.DateTimeStyles]::AdjustToUniversal,
            [ref]$startedAt) -or
        -not [long]::TryParse(
            $durationMatches[0].Groups['value'].Value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$durationSeconds)) {
        throw 'Reviewed evidence manifest execution interval is invalid.'
    }
    try {
        return $startedAt.AddSeconds($durationSeconds)
    }
    catch {
        throw 'Reviewed evidence completion time is outside the supported timestamp range.'
    }
}

function Test-SafeSourceName {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name) -or
        [System.IO.Path]::IsPathRooted($Name) -or
        $Name.Contains('\', [StringComparison]::Ordinal) -or
        $Name.Contains(':', [StringComparison]::Ordinal) -or
        $Name -match '[\x00-\x1f\x7f]') {
        return $false
    }
    $segments = @($Name.Split('/'))
    return @($segments | Where-Object {
            [string]::IsNullOrEmpty($_) -or $_ -in @('.', '..')
        }).Count -eq 0
}

function Get-EvidenceFileNames {
    param([Parameter(Mandatory)][string]$ManifestPath)

    $text = Read-StrictUtf8 -Path $ManifestPath -Description 'reviewed evidence manifest'
    $lines = @($text -split '\r?\n')
    $headerIndexes = @()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -ceq 'evidence_files:') {
            $headerIndexes += $index
        }
    }
    if ($headerIndexes.Count -ne 1) {
        throw 'Reviewed evidence manifest must contain exactly one evidence_files section.'
    }

    $names = [System.Collections.Generic.List[string]]::new()
    for ($index = $headerIndexes[0] + 1; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($line -match '^[a-z_]+:') {
            break
        }
        if ([string]::IsNullOrEmpty($line)) {
            continue
        }
        if ($line -cnotmatch '^  - (?<value>"(?:[^"\\]|\\["\\/bfnrt]|\\u[0-9A-Fa-f]{4})*")$') {
            throw 'Reviewed evidence manifest contains a noncanonical evidence_files item.'
        }
        try {
            $scalar = [System.Text.Json.JsonDocument]::Parse($Matches.value)
            try {
                if ($scalar.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                    throw 'not a string'
                }
                $name = $scalar.RootElement.GetString()
            }
            finally {
                $scalar.Dispose()
            }
        }
        catch {
            throw 'Reviewed evidence manifest contains an invalid quoted evidence path.'
        }
        if (-not (Test-SafeSourceName -Name $name)) {
            throw "Reviewed evidence path is unsafe: $name"
        }
        $names.Add($name)
    }
    if ($names.Count -lt 2 -or $names[$names.Count - 1] -cne 'review.json' -or
        @($names | Where-Object { $_ -ceq 'review.json' }).Count -ne 1) {
        throw 'Reviewed evidence manifest must declare review.json exactly once and last.'
    }
    return @($names)
}

function Get-SourceBundleSha256 {
    param([Parameter(Mandatory)][object[]]$Records)

    $builder = [System.Text.StringBuilder]::new()
    foreach ($record in $Records) {
        [void]$builder.Append($record.sha256)
        [void]$builder.Append(' ')
        [void]$builder.Append(([long]$record.size_bytes).ToString([Globalization.CultureInfo]::InvariantCulture))
        [void]$builder.Append(' ')
        [void]$builder.Append($record.name)
        [void]$builder.Append("`n")
    }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($builder.ToString())
    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Read-AndValidateReview {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$ExpectedSourceNames,
        [Parameter(Mandatory)][string]$BundleDirectory,
        [Parameter(Mandatory)][DateTimeOffset]$EvidenceCompletedAtUtc
    )

    $document = Read-StrictJson -Path $Path -Description 'evidence review'
    try {
        $root = $document.RootElement
        Assert-NoDuplicateJsonProperties -Element $root -Description 'evidence review'
        Assert-ExactJsonProperties -Element $root -Expected @(
            'schema_version'
            'reviewer_id'
            'reviewed_at_utc'
            'source_bundle_sha256'
            'source_files'
            'review_scope'
        ) -Description 'evidence review'

        if ((Get-JsonInt32 -Object $root -Name schema_version -Description 'evidence review') -ne 1) {
            throw 'evidence review.schema_version must be the integer 1.'
        }
        $reviewerId = Get-JsonString -Object $root -Name reviewer_id -Description 'evidence review'
        if ($reviewerId -cnotmatch $githubReviewerPattern) {
            throw 'evidence review.reviewer_id is not a canonical github- actor identifier.'
        }
        $reviewedAtUtc = Get-JsonString -Object $root -Name reviewed_at_utc -Description 'evidence review'
        Assert-Rfc3339Utc -Value $reviewedAtUtc -Description 'evidence review.reviewed_at_utc'
        $parsedReviewedAtUtc = [DateTimeOffset]::Parse(
            $reviewedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor
                [Globalization.DateTimeStyles]::AdjustToUniversal)
        if ($parsedReviewedAtUtc -lt $EvidenceCompletedAtUtc) {
            throw 'evidence review.reviewed_at_utc must be at or after the evidence run completed.'
        }
        if ($parsedReviewedAtUtc -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
            throw 'evidence review.reviewed_at_utc must not be more than five minutes in the future.'
        }
        $sourceBundleSha256 = Get-JsonString `
            -Object $root -Name source_bundle_sha256 -Description 'evidence review'
        Assert-LowerSha256 `
            -Value $sourceBundleSha256 -Description 'evidence review.source_bundle_sha256'

        $filesElement = $root.GetProperty('source_files')
        if ($filesElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw 'evidence review.source_files must be a JSON array.'
        }
        $fileElements = @($filesElement.EnumerateArray())
        if ($fileElements.Count -ne $ExpectedSourceNames.Count) {
            throw 'evidence review.source_files does not match the reviewed source bundle inventory.'
        }
        $sourceRecords = [System.Collections.Generic.List[object]]::new()
        for ($index = 0; $index -lt $fileElements.Count; $index++) {
            $file = $fileElements[$index]
            Assert-ExactJsonProperties -Element $file -Expected @(
                'name'
                'sha256'
                'size_bytes'
            ) -Description "evidence review.source_files[$index]"
            $name = Get-JsonString `
                -Object $file -Name name -Description "evidence review.source_files[$index]"
            if (-not (Test-SafeSourceName -Name $name) -or
                $name -cne $ExpectedSourceNames[$index]) {
                throw "evidence review.source_files[$index].name is not the exact ordinal source name."
            }
            $sha256 = Get-JsonString `
                -Object $file -Name sha256 -Description "evidence review.source_files[$index]"
            Assert-LowerSha256 `
                -Value $sha256 -Description "evidence review.source_files[$index].sha256"
            $size = Get-JsonInt64 `
                -Object $file -Name size_bytes -Description "evidence review.source_files[$index]"
            $sourceRecords.Add([ordered]@{
                name = $name
                sha256 = $sha256
                size_bytes = $size
            })

            $candidates = @(Get-ReviewSourceCandidates `
                -Name $name `
                -BundleDirectory $BundleDirectory)
            if (@($candidates | Where-Object {
                        $_.sha256 -ceq $sha256 -and $_.size_bytes -eq $size
                    }).Count -ne 1) {
                throw "evidence review.source_files[$index] does not bind the exact reviewed source bytes."
            }
        }
        if ((Get-SourceBundleSha256 -Records @($sourceRecords)) -cne $sourceBundleSha256) {
            throw 'evidence review.source_bundle_sha256 does not match the canonical source_files lines.'
        }

        $scopeElement = $root.GetProperty('review_scope')
        if ($scopeElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw 'evidence review.review_scope must be a JSON array.'
        }
        $scope = @($scopeElement.EnumerateArray())
        if ($scope.Count -ne 2 -or
            $scope[0].ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $scope[1].ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $scope[0].GetString() -cne 'privacy-redaction' -or
            $scope[1].GetString() -cne 'bundle-integrity') {
            throw 'evidence review.review_scope must be exactly privacy-redaction then bundle-integrity.'
        }

        return [ordered]@{
            reviewer_id = $reviewerId
            reviewed_at_utc = $reviewedAtUtc
            source_bundle_sha256 = $sourceBundleSha256
        }
    }
    finally {
        $document.Dispose()
    }
}

function Assert-AttestationJson {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Expected
    )

    $document = Read-StrictJson -Path $Path -Description 'release attestation'
    try {
        $root = $document.RootElement
        Assert-NoDuplicateJsonProperties -Element $root -Description 'release attestation'
        Assert-ExactJsonProperties -Element $root -Expected @(
            'schema_version'
            'repository'
            'tag'
            'candidate'
            'acceptance'
            'promotion'
            'files'
        ) -Description 'release attestation'

        if ((Get-JsonInt32 -Object $root -Name schema_version -Description 'release attestation') -ne
            $schemaVersion) {
            throw "release attestation.schema_version must be the integer $schemaVersion."
        }
        foreach ($name in @('repository', 'tag')) {
            $actual = Get-JsonString -Object $root -Name $name -Description 'release attestation'
            if ($actual -cne $Expected[$name]) {
                throw "release attestation.$name does not match the expected identity."
            }
        }

        $candidate = $root.GetProperty('candidate')
        Assert-ExactJsonProperties -Element $candidate -Expected @(
            'commit'
            'run_id'
            'run_attempt'
            'artifact_id'
            'artifact_name'
            'artifact_digest'
            'manifest_sha256'
        ) -Description 'release attestation.candidate'
        foreach ($name in @(
            'commit', 'run_id', 'artifact_id', 'artifact_name',
            'artifact_digest', 'manifest_sha256')) {
            $actual = Get-JsonString `
                -Object $candidate -Name $name -Description 'release attestation.candidate'
            if ($actual -cne $Expected.candidate[$name]) {
                throw "release attestation.candidate.$name does not match the expected identity."
            }
        }
        if ((Get-JsonInt32 `
                -Object $candidate -Name run_attempt `
                -Description 'release attestation.candidate' -Minimum 1) -ne
            $Expected.candidate.run_attempt) {
            throw 'release attestation.candidate.run_attempt does not match the expected identity.'
        }

        $acceptance = $root.GetProperty('acceptance')
        Assert-ExactJsonProperties -Element $acceptance -Expected @(
            'commit'
            'evidence_manifest_path'
            'evidence_manifest_sha256'
            'review_path'
            'review_sha256'
            'reviewer_id'
            'reviewed_at_utc'
            'source_bundle_sha256'
            'gate_id'
        ) -Description 'release attestation.acceptance'
        foreach ($name in @(
            'commit', 'evidence_manifest_path', 'evidence_manifest_sha256',
            'review_path', 'review_sha256', 'reviewer_id', 'reviewed_at_utc',
            'source_bundle_sha256', 'gate_id')) {
            $actual = Get-JsonString `
                -Object $acceptance -Name $name -Description 'release attestation.acceptance'
            if ($actual -cne $Expected.acceptance[$name]) {
                throw "release attestation.acceptance.$name does not match the expected identity."
            }
        }

        $promotion = $root.GetProperty('promotion')
        Assert-ExactJsonProperties -Element $promotion -Expected @(
            'run_id'
            'run_attempt'
        ) -Description 'release attestation.promotion'
        $promotionRunId = Get-JsonString `
            -Object $promotion -Name run_id -Description 'release attestation.promotion'
        if ($promotionRunId -cne $Expected.promotion.run_id) {
            throw 'release attestation.promotion.run_id does not match the expected identity.'
        }
        if ((Get-JsonInt32 `
                -Object $promotion -Name run_attempt `
                -Description 'release attestation.promotion' -Minimum 1) -ne
            $Expected.promotion.run_attempt) {
            throw 'release attestation.promotion.run_attempt does not match the expected identity.'
        }

        $filesElement = $root.GetProperty('files')
        if ($filesElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw 'release attestation.files must be a JSON array.'
        }
        $files = @($filesElement.EnumerateArray())
        if ($files.Count -ne $Expected.files.Count) {
            throw 'release attestation.files does not contain the exact release inventory.'
        }
        for ($index = 0; $index -lt $files.Count; $index++) {
            $file = $files[$index]
            Assert-ExactJsonProperties -Element $file -Expected @(
                'name'
                'sha256'
                'size_bytes'
            ) -Description "release attestation.files[$index]"
            foreach ($name in @('name', 'sha256')) {
                $actual = Get-JsonString `
                    -Object $file -Name $name -Description "release attestation.files[$index]"
                if ($actual -cne $Expected.files[$index][$name]) {
                    throw "release attestation.files[$index].$name does not match the exact release file."
                }
            }
            if ((Get-JsonInt64 `
                    -Object $file -Name size_bytes `
                    -Description "release attestation.files[$index]") -ne
                $Expected.files[$index].size_bytes) {
                throw "release attestation.files[$index].size_bytes does not match the exact release file."
            }
        }
    }
    finally {
        $document.Dispose()
    }
}

if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must be an exact GitHub owner/name slug.'
}
if ($Tag -cnotmatch $alphaTagPattern) {
    throw 'Tag must be a canonical Alpha tag.'
}
Assert-Commit -Value $CandidateCommit -Description 'CandidateCommit'
Assert-Commit -Value $AcceptanceCommit -Description 'AcceptanceCommit'
foreach ($id in @(
    @{ Value = $CandidateRunId; Name = 'CandidateRunId' }
    @{ Value = $CandidateArtifactId; Name = 'CandidateArtifactId' }
    @{ Value = $PromotionRunId; Name = 'PromotionRunId' }
)) {
    if ($id.Value -cnotmatch $positiveIdPattern) {
        throw "$($id.Name) must be a positive decimal identifier."
    }
}
if ($CandidateArtifactDigest -cnotmatch $artifactDigestPattern -or
    $CandidateArtifactDigest -cmatch '^sha256:0{64}$') {
    throw 'CandidateArtifactDigest must be a nonzero lowercase sha256: digest.'
}
$expectedArtifactName = "sutty-$Tag-candidate-$CandidateRunId-attempt-$CandidateRunAttempt"
if ($CandidateArtifactName -cne $expectedArtifactName) {
    throw "CandidateArtifactName must be exactly $expectedArtifactName."
}
if ($EvidenceManifestRepositoryPath -cnotmatch $evidencePathPattern) {
    throw 'EvidenceManifestRepositoryPath is not a canonical repository evidence path.'
}
if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw "RepositoryRoot is missing: $RepositoryRoot"
}
$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if ((Get-Item -LiteralPath $resolvedRepositoryRoot -Force).Attributes.HasFlag(
        [System.IO.FileAttributes]::ReparsePoint)) {
    throw 'RepositoryRoot must not be a reparse point.'
}

$candidateManifestFullPath = [System.IO.Path]::GetFullPath($CandidateManifestPath)
$candidateRoot = Split-Path -Parent $candidateManifestFullPath
$packageDirectory = Join-Path $candidateRoot 'packages'
$candidateValidator = Join-Path $PSScriptRoot 'Assert-AlphaCandidate.ps1'
$liveEvidenceValidator = Join-Path $PSScriptRoot 'Assert-LiveEvidence.ps1'
if (-not (Test-Path -LiteralPath $candidateValidator -PathType Leaf) -or
    -not (Test-Path -LiteralPath $liveEvidenceValidator -PathType Leaf)) {
    throw 'Release attestation dependencies are missing.'
}

& $candidateValidator `
    -PackageDirectory $packageDirectory `
    -ManifestPath $candidateManifestFullPath `
    -Repository $Repository `
    -Tag $Tag `
    -Commit $CandidateCommit `
    -CandidateRunId $CandidateRunId `
    -CandidateRunAttempt $CandidateRunAttempt `
    -ArtifactName $CandidateArtifactName *> $null

$x64Name = "Sutty-$Tag-win-x64.zip"
$arm64Name = "Sutty-$Tag-win-arm64.zip"
$releaseFiles = @(
    Get-FileRecord -Name $x64Name -Path (Join-Path $packageDirectory $x64Name)
    Get-FileRecord -Name $arm64Name -Path (Join-Path $packageDirectory $arm64Name)
    Get-FileRecord -Name 'SHA256SUMS.txt' -Path (Join-Path $packageDirectory 'SHA256SUMS.txt')
    Get-FileRecord -Name 'CANDIDATE-MANIFEST.json' -Path $candidateManifestFullPath
)
$candidateManifestSha256 = $releaseFiles[3].sha256

$manifestFullPath = [System.IO.Path]::GetFullPath((Join-Path `
    $resolvedRepositoryRoot `
    $EvidenceManifestRepositoryPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
$repositoryPrefix = $resolvedRepositoryRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $manifestFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence manifest resolves outside RepositoryRoot.'
}
$reviewRepositoryPath = `
    ([System.IO.Path]::GetDirectoryName($EvidenceManifestRepositoryPath).Replace('\', '/') + '/review.json')
$reviewFullPath = Join-Path (Split-Path -Parent $manifestFullPath) 'review.json'

& $liveEvidenceValidator `
    -ManifestPath $manifestFullPath `
    -ExpectedCommit $CandidateCommit `
    -ExpectedPackageSha256 $releaseFiles[0].sha256 `
    -RequiredGateId $requiredGateId `
    -RequiredResult Pass *> $null

$declaredEvidenceFiles = @(Get-EvidenceFileNames -ManifestPath $manifestFullPath)
$sourceNameList = [System.Collections.Generic.List[string]]::new()
$sourceNameList.Add('manifest.yml')
foreach ($name in $declaredEvidenceFiles) {
    if ($name -cne 'review.json') {
        $sourceNameList.Add($name)
    }
}
$sourceNames = [string[]]@($sourceNameList)
[Array]::Sort($sourceNames, [StringComparer]::Ordinal)
$evidenceCompletedAtUtc = Get-EvidenceCompletionUtc -ManifestPath $manifestFullPath
$review = Read-AndValidateReview `
    -Path $reviewFullPath `
    -ExpectedSourceNames $sourceNames `
    -BundleDirectory (Split-Path -Parent $manifestFullPath) `
    -EvidenceCompletedAtUtc $evidenceCompletedAtUtc

$expectedAttestation = [ordered]@{
    schema_version = $schemaVersion
    repository = $Repository
    tag = $Tag
    candidate = [ordered]@{
        commit = $CandidateCommit
        run_id = $CandidateRunId
        run_attempt = $CandidateRunAttempt
        artifact_id = $CandidateArtifactId
        artifact_name = $CandidateArtifactName
        artifact_digest = $CandidateArtifactDigest
        manifest_sha256 = $candidateManifestSha256
    }
    acceptance = [ordered]@{
        commit = $AcceptanceCommit
        evidence_manifest_path = $EvidenceManifestRepositoryPath
        evidence_manifest_sha256 = Get-LowerSha256 -Path $manifestFullPath
        review_path = $reviewRepositoryPath
        review_sha256 = Get-LowerSha256 -Path $reviewFullPath
        reviewer_id = $review.reviewer_id
        reviewed_at_utc = $review.reviewed_at_utc
        source_bundle_sha256 = $review.source_bundle_sha256
        gate_id = $requiredGateId
    }
    promotion = [ordered]@{
        run_id = $PromotionRunId
        run_attempt = $PromotionRunAttempt
    }
    files = $releaseFiles
}

$attestationFullPath = [System.IO.Path]::GetFullPath($AttestationPath)
if ([System.IO.Path]::GetFileName($attestationFullPath) -cne 'RELEASE-ATTESTATION.json') {
    throw 'AttestationPath filename must be RELEASE-ATTESTATION.json.'
}
if ($WriteAttestation) {
    if (Test-Path -LiteralPath $attestationFullPath) {
        throw "Release attestation already exists: $attestationFullPath"
    }
    $parent = Split-Path -Parent $attestationFullPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    $json = $expectedAttestation | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        $attestationFullPath,
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

Assert-AttestationJson -Path $attestationFullPath -Expected $expectedAttestation
Write-Host "Release attestation contract passed for $Tag (candidate run $CandidateRunId, promotion run $PromotionRunId)."
