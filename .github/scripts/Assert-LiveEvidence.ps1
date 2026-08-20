[CmdletBinding(DefaultParameterSetName = 'Root')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Root')]
    [ValidateNotNullOrEmpty()]
    [string]$EvidenceRoot,

    [Parameter(Mandatory, ParameterSetName = 'Manifest')]
    [ValidateNotNullOrEmpty()]
    [string]$ManifestPath,

    [string]$ExpectedCommit,

    [string]$ExpectedPackageSha256,

    [ValidateSet('Pass', 'Fail', 'Blocked')]
    [string]$RequiredResult
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$violations = [System.Collections.Generic.List[string]]::new()
$requiredKeys = @(
    'schema_version'
    'gate_id'
    'commit'
    'package_sha256'
    'windows_build'
    'architecture'
    'server_family'
    'server_version'
    'route'
    'authentication'
    'expected_host_fingerprint'
    'result'
    'started_at_utc'
    'duration_seconds'
    'evidence_files'
    'redaction_reviewed'
)
$requiredKeySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($requiredKey in $requiredKeys) {
    $requiredKeySet.Add($requiredKey) | Out-Null
}
$utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)

if ($null -eq ('SuttyLiveEvidencePngCrc' -as [type])) {
    Add-Type -TypeDefinition @'
public static class SuttyLiveEvidencePngCrc
{
    public static uint Compute(byte[] bytes, int offset, int count)
    {
        uint crc = 0xffffffffu;
        for (int index = offset; index < offset + count; index++)
        {
            crc ^= bytes[index];
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1u) != 0
                    ? (crc >> 1) ^ 0xedb88320u
                    : crc >> 1;
            }
        }
        return ~crc;
    }
}
'@
}

function Add-Violation {
    param([string]$Message)

    $violations.Add($Message)
}

function Test-DirectoryAncestorsArePhysical {
    param(
        [string]$DirectoryPath,
        [string]$Description
    )

    $current = Get-Item -LiteralPath $DirectoryPath -Force
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Add-Violation "$Description traverses a symbolic link or reparse-point directory."
            return $false
        }
        $current = $current.Parent
    }
    return $true
}

function Get-StrictUtf8Text {
    param(
        [string]$Path,
        [long]$MaximumBytes,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Violation "$Description is missing."
        return $null
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-Violation "$Description must not be a symbolic link or reparse point."
        return $null
    }
    if ($item.Length -gt $MaximumBytes) {
        Add-Violation "$Description exceeds the $MaximumBytes-byte review limit."
        return $null
    }

    try {
        return $utf8Strict.GetString([System.IO.File]::ReadAllBytes($item.FullName))
    }
    catch {
        Add-Violation "$Description must be valid UTF-8 text."
        return $null
    }
}

function ConvertFrom-CanonicalScalar {
    param(
        [string]$Token,
        [string]$Description
    )

    $tokenValue = $Token.Trim()
    if ([string]::IsNullOrWhiteSpace($tokenValue)) {
        Add-Violation "$Description must not be empty."
        return $null
    }

    if ($tokenValue.StartsWith('"', [StringComparison]::Ordinal)) {
        try {
            $document = [System.Text.Json.JsonDocument]::Parse($tokenValue)
            try {
                if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                    Add-Violation "$Description must be a JSON-style quoted string or a canonical plain scalar."
                    return $null
                }
                return $document.RootElement.GetString()
            }
            finally {
                $document.Dispose()
            }
        }
        catch {
            Add-Violation "$Description contains an invalid JSON-style quoted string."
            return $null
        }
    }

    if ($tokenValue.StartsWith("'", [StringComparison]::Ordinal) -or
        $tokenValue -match '^(?:[&*!>|]|<<:)') {
        Add-Violation "$Description uses unsupported YAML syntax."
        return $null
    }

    return $tokenValue
}

function Read-EvidenceManifest {
    param(
        [string]$Path,
        [string]$Description
    )

    $text = Get-StrictUtf8Text -Path $Path -MaximumBytes 131072 -Description $Description
    if ($null -eq $text) {
        return $null
    }
    if ($text.IndexOf([char]0) -ge 0) {
        Add-Violation "$Description contains a NUL character."
        return $null
    }

    $values = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    $quotedFields = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $files = [System.Collections.Generic.List[string]]::new()
    $inEvidenceFiles = $false
    $lineNumber = 0

    foreach ($line in ($text -split '\r?\n')) {
        $lineNumber++
        if ($line.Contains("`t", [StringComparison]::Ordinal)) {
            Add-Violation "$Description line $lineNumber contains a tab; only canonical space indentation is allowed."
            continue
        }
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line.TrimStart().StartsWith('#', [StringComparison]::Ordinal)) {
            Add-Violation "$Description line $lineNumber contains a comment; comments are not allowed in evidence records."
            continue
        }

        if ($line -cmatch '^(?<key>[a-z][a-z0-9_]*):(?<tail>.*)$') {
            $key = $Matches.key
            $tail = $Matches.tail
            $inEvidenceFiles = $false

            if ($values.ContainsKey($key)) {
                Add-Violation "$Description contains duplicate field $key."
                continue
            }
            if (-not $requiredKeySet.Contains($key)) {
                Add-Violation "$Description contains unsupported field $key."
            }

            if ($key -ceq 'evidence_files') {
                if (-not [string]::IsNullOrWhiteSpace($tail)) {
                    Add-Violation "$Description evidence_files must be a canonical indented list."
                }
                $values.Add($key, $files)
                $inEvidenceFiles = $true
                continue
            }

            if ($tail -cnotmatch '^\s+(?<token>\S.*)$') {
                Add-Violation "$Description field $key must contain one scalar value."
                $values.Add($key, $null)
                continue
            }
            if ($Matches.token.Trim().StartsWith('"', [StringComparison]::Ordinal)) {
                $quotedFields.Add($key) | Out-Null
            }
            $scalar = ConvertFrom-CanonicalScalar -Token $Matches.token -Description "$Description field $key"
            $values.Add($key, $scalar)
            continue
        }

        if ($line -cmatch '^  -\s+(?<token>\S.*)$') {
            if (-not $inEvidenceFiles) {
                Add-Violation "$Description line $lineNumber contains a list item outside evidence_files."
                continue
            }
            $file = ConvertFrom-CanonicalScalar -Token $Matches.token -Description "$Description evidence_files item"
            if ($null -ne $file) {
                $files.Add($file)
            }
            continue
        }

        Add-Violation "$Description line $lineNumber is outside the supported flat YAML schema."
    }

    foreach ($key in $requiredKeys) {
        if (-not $values.ContainsKey($key)) {
            Add-Violation "$Description is missing required field $key."
        }
    }

    return [pscustomobject]@{
        Values = $values
        QuotedFields = $quotedFields
    }
}

function Test-ForbiddenText {
    param(
        [string]$Text,
        [string]$Description
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return
    }
    if ($Text -match '[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]') {
        Add-Violation "$Description contains a forbidden ASCII control character."
        return
    }

    $forbiddenPatterns = @(
        '(?im)-----BEGIN [A-Z0-9 ]*(?:PRIVATE|PUBLIC) KEY-----',
        '(?im)^\s*PuTTY-User-Key-File-[0-9]+\s*:',
        '(?im)\b(?:ssh-rsa|ssh-ed25519|ecdsa-sha2-[^\s]+)\s+[A-Za-z0-9+/]{32,}={0,3}',
        '(?im)\b(?:password|passphrase|secret|token|otp|one[_ -]?time[_ -]?password|verification[_ -]?code|kbi[_ -]?answer|api[_-]?key|private[_-]?key|authorization|cookie|credential)\b\s*[:=]\s*[^\s,}\]]+',
        '(?im)\b(?:host|hostname|server[_-]?name|endpoint|address|ip|user|username|path|local[_-]?path|remote[_-]?path|transcript|stdout|stderr|command|command[_-]?output|raw[_-]?output)\b\s*[:=]',
        '(?i)\b(?:ssh|sftp|https?)://',
        '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,63}\b',
        '(?i)(?<![A-Za-z0-9._-])[A-Za-z0-9._-]+@[A-Za-z0-9][A-Za-z0-9_-]{0,62}(?![A-Za-z0-9._-])',
        '(?i)(?<![A-Za-z0-9_-])(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}(?![A-Za-z0-9_-])',
        '(?<![0-9])(?:(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})\.){3}(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})(?![0-9])',
        '(?i)(?<![A-Za-z0-9])[A-Z]:[\\/]',
        '(?m)(?:^|[\s"''=:])\\\\[^\s\\]+\\',
        '(?i)(?<![A-Za-z0-9])/(?:home|Users|root|etc|var|tmp|srv|opt|mnt|data|usr)/',
        '(?i)(?<![A-Za-z0-9])~[\\/]',
        "`e\[",
        '(?im)^\s*(?:debug[123]?:|last login:|welcome to\s)',
        '(?m)^\s*(?:PS\s+[^>\r\n]+>|[$#>])\s+\S'
    )
    foreach ($pattern in $forbiddenPatterns) {
        if ([regex]::IsMatch($Text, $pattern)) {
            Add-Violation "$Description contains forbidden identifying, secret, key, path, or transcript material."
            return
        }
    }

    $ipv6Candidates = [regex]::Matches(
        $Text,
        '(?<![0-9A-Fa-f:])(?=[0-9A-Fa-f:]*:[0-9A-Fa-f:]*:)[0-9A-Fa-f:]{2,}(?![0-9A-Fa-f:])')
    foreach ($candidate in $ipv6Candidates) {
        $address = $null
        if ([System.Net.IPAddress]::TryParse($candidate.Value, [ref]$address) -and
            $address.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) {
            Add-Violation "$Description contains a forbidden IP address."
            return
        }
    }
}

function Test-JsonElement {
    param(
        [System.Text.Json.JsonElement]$Element,
        [string]$Description
    )

    switch ($Element.ValueKind) {
        ([System.Text.Json.JsonValueKind]::Object) {
            $propertyNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if (-not $propertyNames.Add($property.Name)) {
                    Add-Violation "$Description contains a duplicate JSON property."
                }
                $normalizedName = ($property.Name -replace '[-_]', '').ToLowerInvariant()
                if ($normalizedName -in @(
                    'host', 'hostname', 'hostnamevalue', 'servername', 'endpoint', 'address',
                    'ip', 'ipaddress', 'user', 'username', 'password', 'passphrase', 'secret',
                    'token', 'apikey', 'privatekey', 'rawkey', 'credential', 'authorization',
                    'cookie', 'transcript', 'stdout', 'stderr', 'command', 'commandline',
                    'commandoutput', 'rawoutput', 'output', 'path', 'localpath', 'remotepath',
                    'keypath')) {
                    Add-Violation "$Description contains a forbidden JSON property."
                }
                Test-JsonElement -Element $property.Value -Description $Description
            }
        }
        ([System.Text.Json.JsonValueKind]::Array) {
            foreach ($item in $Element.EnumerateArray()) {
                Test-JsonElement -Element $item -Description $Description
            }
        }
        ([System.Text.Json.JsonValueKind]::String) {
            Test-ForbiddenText -Text $Element.GetString() -Description $Description
        }
    }
}

function Test-SummaryContract {
    param(
        [System.Text.Json.JsonElement]$RootElement,
        [System.Collections.Generic.Dictionary[string, object]]$ManifestValues,
        [string]$Description
    )

    function Get-ManifestValue([string]$Name) {
        if ($ManifestValues.ContainsKey($Name) -and $null -ne $ManifestValues[$Name]) {
            return [string]$ManifestValues[$Name]
        }
        return $null
    }

    function Get-RequiredSummaryProperty([string]$Name) {
        $matches = @($RootElement.EnumerateObject() | Where-Object { $_.Name -ceq $Name })
        if ($matches.Count -ne 1) {
            Add-Violation "$Description must contain exactly one $Name property."
            return $null
        }
        return $matches[0]
    }

    $schemaVersion = Get-RequiredSummaryProperty 'schema_version'
    $parsedSchemaVersion = 0
    if ($null -ne $schemaVersion -and
        ($schemaVersion.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $schemaVersion.Value.TryGetInt32([ref]$parsedSchemaVersion) -or
        $parsedSchemaVersion -ne 1)) {
        Add-Violation "$Description schema_version must be the JSON integer 1."
    }

    foreach ($stringField in @('gate_id', 'result', 'started_at_utc')) {
        $property = Get-RequiredSummaryProperty $stringField
        if ($null -ne $property -and
            ($property.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $property.Value.GetString() -cne (Get-ManifestValue $stringField))) {
            Add-Violation "$Description $stringField must exactly match manifest.yml."
        }
    }

    $duration = Get-RequiredSummaryProperty 'duration_seconds'
    $parsedSummaryDuration = [long]0
    $parsedManifestDuration = [long]0
    $manifestDurationValid = [long]::TryParse(
        (Get-ManifestValue 'duration_seconds'),
        [Globalization.NumberStyles]::None,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$parsedManifestDuration)
    if ($null -ne $duration -and
        ($duration.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $duration.Value.TryGetInt64([ref]$parsedSummaryDuration) -or
        -not $manifestDurationValid -or
        $parsedSummaryDuration -ne $parsedManifestDuration)) {
        Add-Violation "$Description duration_seconds must be a JSON integer matching manifest.yml."
    }

    $redactionReviewed = Get-RequiredSummaryProperty 'redaction_reviewed'
    $expectedRedactionReviewed = (Get-ManifestValue 'redaction_reviewed') -ceq 'true'
    if ($null -ne $redactionReviewed -and
        ($redactionReviewed.Value.ValueKind -notin @(
            [System.Text.Json.JsonValueKind]::True,
            [System.Text.Json.JsonValueKind]::False) -or
        $redactionReviewed.Value.GetBoolean() -ne $expectedRedactionReviewed)) {
        Add-Violation "$Description redaction_reviewed must be a JSON boolean matching manifest.yml."
    }

    $privacyNotice = Get-RequiredSummaryProperty 'privacy_notice'
    $canonicalPrivacyNotice =
        'Connection identifiers, credentials, filesystem locations, session content, and cryptographic material are excluded.'
    if ($null -ne $privacyNotice -and
        ($privacyNotice.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
        $privacyNotice.Value.GetString() -cne $canonicalPrivacyNotice)) {
        Add-Violation "$Description privacy_notice must contain the canonical exclusion notice."
    }

    $checksProperty = Get-RequiredSummaryProperty 'checks'
    $checkResults = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $checksProperty) {
        if ($checksProperty.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or
            $checksProperty.Value.GetArrayLength() -lt 1 -or
            $checksProperty.Value.GetArrayLength() -gt 64) {
            Add-Violation "$Description checks must be a non-empty array with at most 64 items."
        }
        else {
            $checkIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($check in $checksProperty.Value.EnumerateArray()) {
                if ($check.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                    Add-Violation "$Description checks items must be JSON objects."
                    continue
                }
                $idProperties = @($check.EnumerateObject() | Where-Object { $_.Name -ceq 'id' })
                $resultProperties = @($check.EnumerateObject() | Where-Object { $_.Name -ceq 'result' })
                if ($idProperties.Count -ne 1 -or
                    $idProperties[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                    Add-Violation "$Description each checks item must contain one string id."
                }
                else {
                    $checkId = $idProperties[0].Value.GetString()
                    if ($checkId -cnotmatch '^[a-z0-9][a-z0-9-]{0,63}$' -or
                        -not $checkIds.Add($checkId)) {
                        Add-Violation "$Description checks ids must be bounded canonical unique identifiers."
                    }
                }
                if ($resultProperties.Count -ne 1 -or
                    $resultProperties[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
                    $resultProperties[0].Value.GetString() -cnotin @('Pass', 'Fail', 'Blocked')) {
                    Add-Violation "$Description each checks item must contain one allowed result."
                }
                else {
                    $checkResults.Add($resultProperties[0].Value.GetString())
                }
            }
        }
    }

    $manifestResult = Get-ManifestValue 'result'
    if ($manifestResult -ceq 'Pass' -and
        ($checkResults.Count -eq 0 -or @($checkResults | Where-Object { $_ -cne 'Pass' }).Count -gt 0)) {
        Add-Violation "$Description cannot declare Pass unless every check passed."
    }
    elseif ($manifestResult -ceq 'Fail' -and -not $checkResults.Contains('Fail')) {
        Add-Violation "$Description cannot declare Fail without at least one failed check."
    }
    elseif ($manifestResult -ceq 'Blocked' -and -not $checkResults.Contains('Blocked')) {
        $blockingCategories = @(
            $RootElement.EnumerateObject() | Where-Object { $_.Name -ceq 'blocking_category' })
        $hasBoundedBlockingCategory = $blockingCategories.Count -eq 1 -and
            $blockingCategories[0].Value.ValueKind -eq [System.Text.Json.JsonValueKind]::String -and
            $blockingCategories[0].Value.GetString() -cmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$'
        if (-not $hasBoundedBlockingCategory) {
            Add-Violation "$Description cannot declare Blocked without a blocked check or bounded blocking_category."
        }
    }
}

function Test-EvidenceJson {
    param(
        [string]$Path,
        [string]$Description,
        [System.Collections.Generic.Dictionary[string, object]]$ManifestValues,
        [switch]$Summary
    )

    $text = Get-StrictUtf8Text -Path $Path -MaximumBytes 1048576 -Description $Description
    if ($null -eq $text) {
        return
    }
    Test-ForbiddenText -Text $text -Description $Description

    try {
        $document = [System.Text.Json.JsonDocument]::Parse($text)
        try {
            if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                Add-Violation "$Description must contain one JSON object."
                return
            }
            Test-JsonElement -Element $document.RootElement -Description $Description
            if ($Summary) {
                Test-SummaryContract `
                    -RootElement $document.RootElement `
                    -ManifestValues $ManifestValues `
                    -Description $Description
            }
        }
        finally {
            $document.Dispose()
        }
    }
    catch {
        Add-Violation "$Description must be valid JSON."
    }
}

function Test-EvidenceText {
    param(
        [string]$Path,
        [string]$Description
    )

    $text = Get-StrictUtf8Text -Path $Path -MaximumBytes 1048576 -Description $Description
    if ($null -ne $text) {
        Test-ForbiddenText -Text $text -Description $Description
    }
}

function Get-BigEndianUInt32 {
    param(
        [byte[]]$Bytes,
        [int]$Offset
    )

    return [uint64](
        ([uint64]$Bytes[$Offset] -shl 24) -bor
        ([uint64]$Bytes[$Offset + 1] -shl 16) -bor
        ([uint64]$Bytes[$Offset + 2] -shl 8) -bor
        [uint64]$Bytes[$Offset + 3])
}

function Test-EvidencePng {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Violation "$Description is missing."
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-Violation "$Description must not be a symbolic link or reparse point."
        return
    }
    if ($item.Length -gt 5242880) {
        Add-Violation "$Description exceeds the 5242880-byte PNG review limit."
        return
    }

    $bytes = [System.IO.File]::ReadAllBytes($item.FullName)
    $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    if ($bytes.Length -lt $signature.Length) {
        Add-Violation "$Description is not a structurally valid PNG."
        return
    }
    for ($index = 0; $index -lt $signature.Length; $index++) {
        if ($bytes[$index] -ne $signature[$index]) {
            Add-Violation "$Description has an invalid PNG signature."
            return
        }
    }

    $allowedChunks = @('IHDR', 'PLTE', 'IDAT', 'IEND', 'tRNS', 'sRGB', 'gAMA', 'cHRM', 'pHYs')
    $offset = 8
    $chunkIndex = 0
    $seenHeader = $false
    $seenData = $false
    $seenEnd = $false
    $seenPalette = $false
    $seenTransparency = $false
    $dataSequenceEnded = $false
    $colorType = -1
    $paletteEntries = 0
    $ancillarySeen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    while ($offset -lt $bytes.Length) {
        if ($chunkIndex -ge 4096) {
            Add-Violation "$Description contains too many PNG chunks."
            return
        }
        if ($bytes.Length - $offset -lt 12) {
            Add-Violation "$Description contains a truncated PNG chunk."
            return
        }
        $length = Get-BigEndianUInt32 -Bytes $bytes -Offset $offset
        if ($length -gt 5242880 -or $length + 12 -gt $bytes.Length - $offset) {
            Add-Violation "$Description contains an invalid PNG chunk length."
            return
        }
        $type = [System.Text.Encoding]::ASCII.GetString($bytes, $offset + 4, 4)
        if ($type -cnotin $allowedChunks) {
            Add-Violation "$Description contains a forbidden or unknown PNG metadata chunk."
            return
        }
        $storedCrc = Get-BigEndianUInt32 -Bytes $bytes -Offset ($offset + 8 + [int]$length)
        $actualCrc = [SuttyLiveEvidencePngCrc]::Compute(
            $bytes,
            $offset + 4,
            4 + [int]$length)
        if ([uint64]$actualCrc -ne $storedCrc) {
            Add-Violation "$Description contains a PNG chunk with an invalid CRC."
            return
        }
        if ($chunkIndex -eq 0 -and ($type -cne 'IHDR' -or $length -ne 13)) {
            Add-Violation "$Description must begin with one canonical IHDR chunk."
            return
        }
        if ($type -ceq 'IHDR') {
            if ($seenHeader -or $length -ne 13) {
                Add-Violation "$Description contains an invalid duplicate IHDR chunk."
                return
            }
            $seenHeader = $true
            $width = Get-BigEndianUInt32 -Bytes $bytes -Offset ($offset + 8)
            $height = Get-BigEndianUInt32 -Bytes $bytes -Offset ($offset + 12)
            if ($width -eq 0 -or $height -eq 0 -or $width -gt 16384 -or $height -gt 16384 -or
                $width * $height -gt 67108864) {
                Add-Violation "$Description has unsafe PNG dimensions."
                return
            }
            $bitDepth = $bytes[$offset + 16]
            $colorType = $bytes[$offset + 17]
            $validBitDepth = switch ($colorType) {
                0 { $bitDepth -in @(1, 2, 4, 8, 16) }
                2 { $bitDepth -in @(8, 16) }
                3 { $bitDepth -in @(1, 2, 4, 8) }
                4 { $bitDepth -in @(8, 16) }
                6 { $bitDepth -in @(8, 16) }
                default { $false }
            }
            if (-not $validBitDepth -or $bytes[$offset + 18] -ne 0 -or
                $bytes[$offset + 19] -ne 0 -or $bytes[$offset + 20] -notin @(0, 1)) {
                Add-Violation "$Description contains an unsupported PNG image header."
                return
            }
        }
        elseif ($type -ceq 'PLTE') {
            if ($seenPalette -or $seenData -or $colorType -in @(0, 4) -or
                $length -eq 0 -or $length -gt 768 -or $length % 3 -ne 0) {
                Add-Violation "$Description contains an invalid PNG palette chunk."
                return
            }
            $seenPalette = $true
            $paletteEntries = [int]($length / 3)
        }
        elseif ($type -ceq 'IDAT') {
            if ($dataSequenceEnded -or ($colorType -eq 3 -and -not $seenPalette)) {
                Add-Violation "$Description contains non-consecutive PNG data chunks."
                return
            }
            $seenData = $true
        }
        elseif ($type -ceq 'tRNS') {
            $validTransparencyLength = switch ($colorType) {
                0 { $length -eq 2 }
                2 { $length -eq 6 }
                3 { $seenPalette -and $length -gt 0 -and $length -le $paletteEntries }
                default { $false }
            }
            if ($seenTransparency -or $seenData -or -not $validTransparencyLength) {
                Add-Violation "$Description contains an invalid PNG transparency chunk."
                return
            }
            $seenTransparency = $true
        }
        elseif ($type -cin @('sRGB', 'gAMA', 'cHRM', 'pHYs')) {
            if (-not $ancillarySeen.Add($type) -or $seenData -or
                ($type -cne 'pHYs' -and $seenPalette)) {
                Add-Violation "$Description contains a duplicate or misplaced PNG ancillary chunk."
                return
            }
            switch ($type) {
                'sRGB' {
                    if ($length -ne 1 -or $bytes[$offset + 8] -gt 3) {
                        Add-Violation "$Description contains an invalid PNG sRGB chunk."
                        return
                    }
                }
                'gAMA' {
                    $gamma = if ($length -eq 4) {
                        Get-BigEndianUInt32 -Bytes $bytes -Offset ($offset + 8)
                    }
                    else { 0 }
                    if ($length -ne 4 -or $gamma -eq 0 -or $gamma -gt 1000000) {
                        Add-Violation "$Description contains an invalid PNG gAMA chunk."
                        return
                    }
                }
                'cHRM' {
                    if ($length -ne 32) {
                        Add-Violation "$Description contains an invalid PNG cHRM chunk."
                        return
                    }
                    $chromaticities = for ($valueIndex = 0; $valueIndex -lt 8; $valueIndex++) {
                        Get-BigEndianUInt32 -Bytes $bytes -Offset ($offset + 8 + ($valueIndex * 4))
                    }
                    if (@($chromaticities | Where-Object { $_ -gt 100000 }).Count -gt 0 -or
                        $chromaticities[0] + $chromaticities[1] -gt 100000 -or
                        $chromaticities[2] + $chromaticities[3] -gt 100000 -or
                        $chromaticities[4] + $chromaticities[5] -gt 100000 -or
                        $chromaticities[6] + $chromaticities[7] -gt 100000) {
                        Add-Violation "$Description contains out-of-range PNG chromaticities."
                        return
                    }
                }
                'pHYs' {
                    $pixelsX = if ($length -eq 9) {
                        Get-BigEndianUInt32 -Bytes $bytes -Offset ($offset + 8)
                    }
                    else { 0 }
                    $pixelsY = if ($length -eq 9) {
                        Get-BigEndianUInt32 -Bytes $bytes -Offset ($offset + 12)
                    }
                    else { 0 }
                    if ($length -ne 9 -or $pixelsX -eq 0 -or $pixelsY -eq 0 -or
                        $pixelsX -gt 1000000 -or $pixelsY -gt 1000000 -or
                        $bytes[$offset + 16] -notin @(0, 1)) {
                        Add-Violation "$Description contains an invalid PNG pHYs chunk."
                        return
                    }
                }
            }
        }
        elseif ($type -ceq 'IEND') {
            if ($seenEnd -or -not $seenData -or $length -ne 0) {
                Add-Violation "$Description contains an invalid IEND chunk."
                return
            }
            $seenEnd = $true
            if ($offset + 12 -ne $bytes.Length) {
                Add-Violation "$Description contains bytes after its IEND chunk."
                return
            }
        }
        elseif ($seenData) {
            $dataSequenceEnded = $true
        }
        $offset += [int]$length + 12
        $chunkIndex++
    }
    if (-not $seenHeader -or -not $seenData -or -not $seenEnd -or
        ($colorType -eq 3 -and -not $seenPalette)) {
        Add-Violation "$Description is missing a required PNG chunk."
    }
}

function Test-SafeEvidencePath {
    param(
        [string]$RelativePath,
        [string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\', [StringComparison]::Ordinal) -or
        $RelativePath.Contains(':', [StringComparison]::Ordinal) -or
        $RelativePath -match '[\x00-\x1f\x7f]') {
        Add-Violation "$Description must be a portable relative path."
        return $false
    }

    $segments = @($RelativePath.Split('/'))
    if ($segments.Count -eq 0 -or @($segments | Where-Object {
        [string]::IsNullOrEmpty($_) -or $_ -eq '.' -or $_ -eq '..' -or
        $_ -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        $_.EndsWith('.', [StringComparison]::Ordinal)
    }).Count -gt 0) {
        Add-Violation "$Description contains an invalid or traversing path segment."
        return $false
    }

    $reservedNames = @('CON', 'PRN', 'AUX', 'NUL', 'COM1', 'COM2', 'COM3', 'COM4', 'COM5',
        'COM6', 'COM7', 'COM8', 'COM9', 'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6',
        'LPT7', 'LPT8', 'LPT9')
    foreach ($segment in $segments) {
        if ([System.IO.Path]::GetFileNameWithoutExtension($segment).ToUpperInvariant() -in $reservedNames) {
            Add-Violation "$Description contains a Windows-reserved path segment."
            return $false
        }
    }
    return $true
}

function Test-LiveEvidenceManifest {
    param(
        [string]$Path,
        [string]$Description
    )

    if ([System.IO.Path]::GetFileName($Path) -cne 'manifest.yml') {
        Add-Violation "$Description must be named exactly manifest.yml."
        return
    }

    $manifestDirectory = Split-Path -Parent $Path
    if (-not (Test-DirectoryAncestorsArePhysical `
        -DirectoryPath $manifestDirectory `
        -Description "$Description path")) {
        return
    }

    $parsedManifest = Read-EvidenceManifest -Path $Path -Description $Description
    if ($null -eq $parsedManifest) {
        return
    }
    $values = $parsedManifest.Values
    $quotedFields = $parsedManifest.QuotedFields

    function Get-Value([string]$Key) {
        if ($values.ContainsKey($Key) -and $null -ne $values[$Key]) {
            return [string]$values[$Key]
        }
        return $null
    }

    if ((Get-Value 'schema_version') -cne '1' -or $quotedFields.Contains('schema_version')) {
        Add-Violation "$Description schema_version must be exactly 1."
    }
    if ((Get-Value 'gate_id') -cnotmatch '^(?=.{1,64}$)[A-Z0-9]+(?:-[A-Z0-9]+)+$') {
        Add-Violation "$Description gate_id must be an uppercase hyphenated identifier of at most 64 characters."
    }

    $commit = Get-Value 'commit'
    if ($commit -cnotmatch '^[0-9a-f]{40}$' -or $commit -cmatch '^0{40}$') {
        Add-Violation "$Description commit must be a 40-character lowercase Git object ID."
    }
    elseif (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and $commit -cne $ExpectedCommit) {
        Add-Violation "$Description commit does not match the expected release commit."
    }

    $packageSha256 = Get-Value 'package_sha256'
    if ($packageSha256 -cnotmatch '^[0-9a-f]{64}$' -or $packageSha256 -cmatch '^0{64}$') {
        Add-Violation "$Description package_sha256 must be a 64-character lowercase SHA-256 digest."
    }
    elseif (-not [string]::IsNullOrWhiteSpace($ExpectedPackageSha256) -and
        $packageSha256 -cne $ExpectedPackageSha256) {
        Add-Violation "$Description package_sha256 does not match the expected package digest."
    }

    if ((Get-Value 'windows_build') -cnotmatch '^(?:10\.0\.)?[0-9]{5}(?:\.[0-9]{1,6})?$') {
        Add-Violation "$Description windows_build must be a numeric Windows build such as 10.0.26100.0."
    }
    if ((Get-Value 'architecture') -cnotin @('x64', 'arm64')) {
        Add-Violation "$Description architecture is outside the allowed enum."
    }
    if ((Get-Value 'server_family') -cnotmatch '^[A-Za-z][A-Za-z0-9_+-]{0,31}$') {
        Add-Violation "$Description server_family must be a non-identifying product-family label."
    }
    if ((Get-Value 'server_version') -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+~-]{0,31}$') {
        Add-Violation "$Description server_version must be a short non-identifying version label."
    }
    else {
        Test-ForbiddenText -Text (Get-Value 'server_version') -Description "$Description server_version"
    }
    if ((Get-Value 'route') -cnotin @(
        'Direct', 'HttpConnect', 'Socks4', 'Socks5', 'SshJump', 'ExternalProxyCommand')) {
        Add-Violation "$Description route is outside the allowed enum."
    }
    if ((Get-Value 'authentication') -cnotin @(
        'Password', 'PublicKey', 'Agent', 'KeyboardInteractive')) {
        Add-Violation "$Description authentication is outside the allowed enum."
    }
    if ((Get-Value 'expected_host_fingerprint') -cnotin @('SHA256:[redacted]', 'NotRecorded')) {
        Add-Violation "$Description expected_host_fingerprint must be a redacted contract value."
    }

    $result = Get-Value 'result'
    if ($result -cnotin @('Pass', 'Fail', 'Blocked')) {
        Add-Violation "$Description result is outside the allowed enum."
    }
    elseif (-not [string]::IsNullOrWhiteSpace($RequiredResult) -and $result -cne $RequiredResult) {
        Add-Violation "$Description result does not satisfy the required gate result."
    }

    $startedAt = Get-Value 'started_at_utc'
    $timestampPattern = '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$'
    $parsedTimestamp = [DateTimeOffset]::MinValue
    $timestampStyles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
        [Globalization.DateTimeStyles]::AdjustToUniversal
    if ($startedAt -cnotmatch $timestampPattern -or
        -not [DateTimeOffset]::TryParse(
            $startedAt,
            [Globalization.CultureInfo]::InvariantCulture,
            $timestampStyles,
            [ref]$parsedTimestamp)) {
        Add-Violation "$Description started_at_utc must be a valid RFC3339 UTC timestamp ending in Z."
    }

    $duration = Get-Value 'duration_seconds'
    $parsedDuration = [long]0
    if ($quotedFields.Contains('duration_seconds') -or
        $duration -cnotmatch '^(?:0|[1-9][0-9]*)$' -or
        -not [long]::TryParse(
            $duration,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsedDuration)) {
        Add-Violation "$Description duration_seconds must be a nonnegative integer."
    }

    $redactionReviewed = Get-Value 'redaction_reviewed'
    if ($quotedFields.Contains('redaction_reviewed') -or
        $redactionReviewed -cnotin @('true', 'false')) {
        Add-Violation "$Description redaction_reviewed must be a canonical YAML boolean."
    }
    elseif ($redactionReviewed -cne 'true') {
        Add-Violation "$Description redaction_reviewed must be true before evidence can pass validation."
    }

    if (-not $values.ContainsKey('evidence_files') -or
        $values['evidence_files'] -isnot [System.Collections.Generic.List[string]]) {
        return
    }
    $evidenceFiles = [System.Collections.Generic.List[string]]$values['evidence_files']
    if ($evidenceFiles.Count -eq 0) {
        Add-Violation "$Description evidence_files must contain at least summary.json."
        return
    }
    if (@($evidenceFiles | Where-Object { $_ -ceq 'summary.json' }).Count -ne 1) {
        Add-Violation "$Description evidence_files must contain summary.json exactly once."
    }

    $bundleDirectory = (Get-Item -LiteralPath (Split-Path -Parent $Path) -Force).FullName
    $bundleItem = Get-Item -LiteralPath $bundleDirectory -Force
    if (($bundleItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-Violation "$Description bundle directory must not be a symbolic link or reparse point."
        return
    }
    $bundlePrefix = $bundleDirectory.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $normalizedEvidenceFiles = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)

    for ($index = 0; $index -lt $evidenceFiles.Count; $index++) {
        $relativePath = $evidenceFiles[$index]
        $pathDescription = "$Description evidence_files item $($index + 1)"
        if (-not (Test-SafeEvidencePath -RelativePath $relativePath -Description $pathDescription)) {
            continue
        }
        $extension = [System.IO.Path]::GetExtension($relativePath)
        if ($extension -cnotin @('.json', '.txt', '.png')) {
            Add-Violation "$pathDescription must reference an allowed .json, .txt, or .png evidence file."
            continue
        }
        if (-not $normalizedEvidenceFiles.Add($relativePath)) {
            Add-Violation "$Description evidence_files contains a duplicate path."
            continue
        }

        $platformPath = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $bundleDirectory $platformPath))
        if (-not $fullPath.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Violation "$pathDescription resolves outside its evidence bundle."
            continue
        }
        $segments = @($relativePath.Split('/'))
        $currentDirectory = $bundleDirectory
        $hasUnsafeAncestor = $false
        for ($segmentIndex = 0; $segmentIndex -lt $segments.Count - 1; $segmentIndex++) {
            $segment = $segments[$segmentIndex]
            $currentDirectory = Join-Path $currentDirectory $segment
            if (Test-Path -LiteralPath $currentDirectory -PathType Container) {
                $directoryItem = Get-Item -LiteralPath $currentDirectory -Force
                if (($directoryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    Add-Violation "$pathDescription traverses a symbolic link or reparse point."
                    $hasUnsafeAncestor = $true
                    break
                }
            }
        }
        if ($hasUnsafeAncestor) {
            continue
        }
        switch ($extension) {
            '.json' {
                Test-EvidenceJson `
                    -Path $fullPath `
                    -Description $pathDescription `
                    -ManifestValues $values `
                    -Summary:($relativePath -ceq 'summary.json')
            }
            '.txt' { Test-EvidenceText -Path $fullPath -Description $pathDescription }
            '.png' { Test-EvidencePng -Path $fullPath -Description $pathDescription }
        }
    }

    $descendantDirectories = @(Get-ChildItem -LiteralPath $bundleDirectory -Directory -Recurse -Force)
    if (@($descendantDirectories | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    }).Count -gt 0) {
        Add-Violation "$Description bundle contains a symbolic link or reparse-point directory."
        return
    }
    $actualFiles = @(
        Get-ChildItem -LiteralPath $bundleDirectory -File -Recurse -Force |
            Where-Object { $_.FullName -cne (Get-Item -LiteralPath $Path).FullName } |
            ForEach-Object {
                [System.IO.Path]::GetRelativePath($bundleDirectory, $_.FullName).Replace('\', '/')
            }
    )
    foreach ($actualFile in $actualFiles) {
        if (-not $normalizedEvidenceFiles.Contains($actualFile)) {
            Add-Violation "$Description bundle contains a file not declared by evidence_files."
        }
    }
    foreach ($declaredFile in $normalizedEvidenceFiles) {
        if ($declaredFile -cnotin $actualFiles) {
            Add-Violation "$Description evidence_files references a missing file."
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
    ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$' -or $ExpectedCommit -cmatch '^0{40}$')) {
    Add-Violation 'ExpectedCommit must be a 40-character lowercase Git object ID.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageSha256) -and
    ($ExpectedPackageSha256 -cnotmatch '^[0-9a-f]{64}$' -or $ExpectedPackageSha256 -cmatch '^0{64}$')) {
    Add-Violation 'ExpectedPackageSha256 must be a 64-character lowercase SHA-256 digest.'
}

$validatedCount = 0
if ($PSCmdlet.ParameterSetName -eq 'Manifest') {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        Add-Violation 'Live-evidence manifest is missing.'
    }
    else {
        $resolvedManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
        Test-LiveEvidenceManifest -Path $resolvedManifest -Description 'live-evidence manifest'
        $validatedCount = 1
    }
}
else {
    if (-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) {
        Add-Violation 'Live-evidence root is missing.'
    }
    else {
        $resolvedRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
        if (Test-DirectoryAncestorsArePhysical `
            -DirectoryPath $resolvedRoot `
            -Description 'Live-evidence root') {
            $rootDirectories = @(Get-ChildItem -LiteralPath $resolvedRoot -Directory -Recurse -Force)
            if (@($rootDirectories | Where-Object {
                ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            }).Count -gt 0) {
                Add-Violation 'Live-evidence root contains a symbolic link or reparse-point directory.'
            }
            else {
                $manifests = @(
                    Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse -Force |
                        Where-Object { $_.Name -ieq 'manifest.yml' } |
                        Sort-Object FullName
                )
                foreach ($manifest in $manifests) {
                    $relativeManifest = [System.IO.Path]::GetRelativePath(
                        $resolvedRoot,
                        $manifest.FullName).Replace('\', '/')
                    if ($relativeManifest -cnotmatch '^alpha[0-9]+/[a-z0-9][a-z0-9-]{0,63}/[a-z0-9][a-z0-9-]{0,63}/manifest\.yml$') {
                        Add-Violation 'A live-evidence manifest is outside docs/evidence/alpha*/<slice>/<bundle>/manifest.yml.'
                        continue
                    }
                    Test-LiveEvidenceManifest `
                        -Path $manifest.FullName `
                        -Description "live-evidence manifest $relativeManifest"
                    $validatedCount++
                }
            }
        }
    }
}

if ($violations.Count -gt 0) {
    $uniqueViolations = @($violations | Sort-Object -Unique)
    $details = ($uniqueViolations | ForEach-Object { " - $_" }) -join [Environment]::NewLine
    throw "Live-evidence validation failed with $($uniqueViolations.Count) violation(s):$([Environment]::NewLine)$details"
}

Write-Host "Live-evidence validation passed for $validatedCount committed evidence manifest(s)."
