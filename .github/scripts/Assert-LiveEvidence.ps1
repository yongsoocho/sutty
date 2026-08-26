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

    [Parameter(ParameterSetName = 'Manifest')]
    [string]$RequiredGateId,

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
$sshLive001RequiredCheckIds = @(
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
)
$sshLive001TrueMeasurementNames = @(
    'package_sha256_verified'
    'package_commit_identity_verified'
    'package_core_identity_verified'
    'authentication_success_verified'
    'sftp_checksum_verified'
    'command_pty_sftp_verified'
    'remote_cleanup_verified'
    'local_cleanup_verified'
    'reconnect_verified'
    'server_audit_verified'
    'authentication_rejection_verified'
    'host_key_rejection_verified'
    'cancellation_verified'
    'timeout_verified'
)
$sshLive001ExactIntegerMeasurements = [ordered]@{
    check_count = [long]12
    passed_count = [long]12
    failed_count = [long]0
    blocked_count = [long]0
    sftp_bytes = [long](64 * 1024)
    audit_exec_count = [long]4
    audit_shell_count = [long]1
    audit_sftp_count = [long]2
    audit_other_count = [long]0
}
$sshLive001BoundedIntegerMeasurements = [ordered]@{
    cancellation_elapsed_milliseconds = @([long]100, [long]10000)
    timeout_elapsed_milliseconds = @([long]12000, [long]30000)
}
$pkg001RequiredCheckIds = @(
    'package-sha256'
    'package-commit-identity'
    'package-tree-identity'
    'ui-startup'
    'alt-navigation-silent'
    'ui-shutdown'
)
$pkg001TrueMeasurementNames = @(
    'package_sha256_verified'
    'package_commit_identity_verified'
    'package_tree_identity_verified'
    'ui_startup_verified'
    'alt_navigation_silent_verified'
    'ui_shutdown_verified'
)
$pkg001ExactIntegerMeasurements = [ordered]@{
    check_count = [long]6
    passed_count = [long]6
    failed_count = [long]0
    blocked_count = [long]0
    alt_navigation_shortcut_count = [long]7
}
$approvedEvidenceScopes = [System.Collections.Generic.Dictionary[string, string[]]]::new(
    [StringComparer]::Ordinal)
$approvedEvidenceScopes.Add('alpha4', @(
    'connection-info'
    'package'
    'ssh-auth'
    'ssh-routes'
    'ssh-transport'
))
$requiredKeySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($requiredKey in $requiredKeys) {
    $requiredKeySet.Add($requiredKey) | Out-Null
}
$utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

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

function Test-SshLive001Measurements {
    param(
        [System.Text.Json.JsonElement]$RootElement,
        [string]$Description
    )

    $measurementMatches = @(
        $RootElement.EnumerateObject() | Where-Object { $_.Name -ceq 'measurements' })
    if ($measurementMatches.Count -ne 1) {
        Add-Violation "$Description SSH-LIVE-001 must contain exactly one measurements property."
        return
    }
    if ($measurementMatches[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        Add-Violation "$Description SSH-LIVE-001 measurements must be a JSON object."
        return
    }

    [string[]]$requiredNames = @(
        $sshLive001TrueMeasurementNames +
        @($sshLive001ExactIntegerMeasurements.Keys) +
        @($sshLive001BoundedIntegerMeasurements.Keys))
    $requiredNameSet = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($name in $requiredNames) {
        $requiredNameSet.Add($name) | Out-Null
    }
    $measurementsByName = [System.Collections.Generic.Dictionary[
        string,
        System.Text.Json.JsonProperty]]::new([StringComparer]::Ordinal)
    $measurementProperties = @($measurementMatches[0].Value.EnumerateObject())
    if ($measurementProperties.Count -ne $requiredNames.Count) {
        Add-Violation (
            "$Description SSH-LIVE-001 measurements must contain exactly the " +
            "$($requiredNames.Count) canonical properties.")
    }
    foreach ($property in $measurementProperties) {
        if (-not $requiredNameSet.Contains($property.Name)) {
            Add-Violation "$Description SSH-LIVE-001 measurements contains an unexpected property."
        }
        if ($measurementsByName.ContainsKey($property.Name)) {
            Add-Violation "$Description SSH-LIVE-001 measurements contains a duplicate property."
        }
        else {
            $measurementsByName.Add($property.Name, $property)
        }
    }

    foreach ($name in $requiredNames) {
        if (-not $measurementsByName.ContainsKey($name)) {
            Add-Violation "$Description SSH-LIVE-001 measurements is missing $name."
        }
    }
    foreach ($name in $sshLive001TrueMeasurementNames) {
        if ($measurementsByName.ContainsKey($name) -and
            $measurementsByName[$name].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::True) {
            Add-Violation "$Description SSH-LIVE-001 measurement $name must be the JSON boolean true."
        }
    }
    foreach ($entry in $sshLive001ExactIntegerMeasurements.GetEnumerator()) {
        if (-not $measurementsByName.ContainsKey($entry.Key)) {
            continue
        }
        $parsedValue = [long]0
        $propertyValue = $measurementsByName[$entry.Key].Value
        if ($propertyValue.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            $propertyValue.GetRawText() -cnotmatch '^(?:0|[1-9][0-9]*)$' -or
            -not $propertyValue.TryGetInt64([ref]$parsedValue) -or
            $parsedValue -ne [long]$entry.Value) {
            Add-Violation (
                "$Description SSH-LIVE-001 measurement $($entry.Key) must be the JSON integer " +
                "$($entry.Value).")
        }
    }
    foreach ($entry in $sshLive001BoundedIntegerMeasurements.GetEnumerator()) {
        if (-not $measurementsByName.ContainsKey($entry.Key)) {
            continue
        }
        $parsedValue = [long]0
        $propertyValue = $measurementsByName[$entry.Key].Value
        $minimum = [long]$entry.Value[0]
        $exclusiveMaximum = [long]$entry.Value[1]
        if ($propertyValue.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            $propertyValue.GetRawText() -cnotmatch '^(?:0|[1-9][0-9]*)$' -or
            -not $propertyValue.TryGetInt64([ref]$parsedValue) -or
            $parsedValue -lt $minimum -or
            $parsedValue -ge $exclusiveMaximum) {
            Add-Violation (
                "$Description SSH-LIVE-001 measurement $($entry.Key) must be a JSON integer " +
                "from $minimum through $($exclusiveMaximum - 1).")
        }
    }
}

function Test-Pkg001Measurements {
    param(
        [System.Text.Json.JsonElement]$RootElement,
        [string]$Description
    )

    $measurementMatches = @(
        $RootElement.EnumerateObject() | Where-Object { $_.Name -ceq 'measurements' })
    if ($measurementMatches.Count -ne 1) {
        Add-Violation "$Description PKG-001 must contain exactly one measurements property."
        return
    }
    if ($measurementMatches[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        Add-Violation "$Description PKG-001 measurements must be a JSON object."
        return
    }

    [string[]]$requiredNames = @(
        $pkg001TrueMeasurementNames + @($pkg001ExactIntegerMeasurements.Keys))
    $requiredNameSet = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($name in $requiredNames) {
        $requiredNameSet.Add($name) | Out-Null
    }
    $measurementsByName = [System.Collections.Generic.Dictionary[
        string,
        System.Text.Json.JsonProperty]]::new([StringComparer]::Ordinal)
    $measurementProperties = @($measurementMatches[0].Value.EnumerateObject())
    if ($measurementProperties.Count -ne $requiredNames.Count) {
        Add-Violation (
            "$Description PKG-001 measurements must contain exactly the " +
            "$($requiredNames.Count) canonical properties.")
    }
    foreach ($property in $measurementProperties) {
        if (-not $requiredNameSet.Contains($property.Name)) {
            Add-Violation "$Description PKG-001 measurements contains an unexpected property."
        }
        if ($measurementsByName.ContainsKey($property.Name)) {
            Add-Violation "$Description PKG-001 measurements contains a duplicate property."
        }
        else {
            $measurementsByName.Add($property.Name, $property)
        }
    }
    foreach ($name in $requiredNames) {
        if (-not $measurementsByName.ContainsKey($name)) {
            Add-Violation "$Description PKG-001 measurements is missing $name."
        }
    }
    foreach ($name in $pkg001TrueMeasurementNames) {
        if ($measurementsByName.ContainsKey($name) -and
            $measurementsByName[$name].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::True) {
            Add-Violation "$Description PKG-001 measurement $name must be the JSON boolean true."
        }
    }
    foreach ($entry in $pkg001ExactIntegerMeasurements.GetEnumerator()) {
        if (-not $measurementsByName.ContainsKey($entry.Key)) {
            continue
        }
        $parsedValue = [long]0
        $propertyValue = $measurementsByName[$entry.Key].Value
        if ($propertyValue.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            $propertyValue.GetRawText() -cnotmatch '^(?:0|[1-9][0-9]*)$' -or
            -not $propertyValue.TryGetInt64([ref]$parsedValue) -or
            $parsedValue -ne [long]$entry.Value) {
            Add-Violation (
                "$Description PKG-001 measurement $($entry.Key) must be the JSON integer " +
                "$($entry.Value).")
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
    $checkIdsInOrder = [System.Collections.Generic.List[string]]::new()
    $checkResultsById = [System.Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
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
                $validCheckId = $null
                $validCheckResult = $null
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
                    else {
                        $validCheckId = $checkId
                        $checkIdsInOrder.Add($checkId)
                    }
                }
                if ($resultProperties.Count -ne 1 -or
                    $resultProperties[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
                    $resultProperties[0].Value.GetString() -cnotin @('Pass', 'Fail', 'Blocked')) {
                    Add-Violation "$Description each checks item must contain one allowed result."
                }
                else {
                    $validCheckResult = $resultProperties[0].Value.GetString()
                    $checkResults.Add($validCheckResult)
                }
                if ($null -ne $validCheckId -and $null -ne $validCheckResult) {
                    $checkResultsById.Add($validCheckId, $validCheckResult)
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

    $enforceSshLive001PassProfile =
        (Get-ManifestValue 'gate_id') -ceq 'SSH-LIVE-001' -and
        $manifestResult -ceq 'Pass'
    if ($enforceSshLive001PassProfile) {
        Test-SshLive001Measurements -RootElement $RootElement -Description $Description
        if ($checkResultsById.Count -ne $sshLive001RequiredCheckIds.Count) {
            Add-Violation "$Description SSH-LIVE-001 must contain exactly the 12 complete gate checks."
        }
        for ($index = 0; $index -lt $sshLive001RequiredCheckIds.Count; $index++) {
            $requiredCheckId = $sshLive001RequiredCheckIds[$index]
            if (-not $checkResultsById.ContainsKey($requiredCheckId) -or
                $checkResultsById[$requiredCheckId] -cne 'Pass') {
                Add-Violation "$Description SSH-LIVE-001 check $requiredCheckId must appear exactly once as Pass."
            }
            if ($checkIdsInOrder.Count -le $index -or
                $checkIdsInOrder[$index] -cne $requiredCheckId) {
                Add-Violation (
                    "$Description SSH-LIVE-001 check position $($index + 1) must be " +
                    "$requiredCheckId.")
            }
        }
    }

    $enforcePkg001PassProfile =
        (Get-ManifestValue 'gate_id') -ceq 'PKG-001' -and
        $manifestResult -ceq 'Pass'
    if ($enforcePkg001PassProfile) {
        Test-Pkg001Measurements -RootElement $RootElement -Description $Description
        if ($checkResultsById.Count -ne $pkg001RequiredCheckIds.Count) {
            Add-Violation "$Description PKG-001 must contain exactly the 6 complete gate checks."
        }
        for ($index = 0; $index -lt $pkg001RequiredCheckIds.Count; $index++) {
            $requiredCheckId = $pkg001RequiredCheckIds[$index]
            if (-not $checkResultsById.ContainsKey($requiredCheckId) -or
                $checkResultsById[$requiredCheckId] -cne 'Pass') {
                Add-Violation "$Description PKG-001 check $requiredCheckId must appear exactly once as Pass."
            }
            if ($checkIdsInOrder.Count -le $index -or
                $checkIdsInOrder[$index] -cne $requiredCheckId) {
                Add-Violation (
                    "$Description PKG-001 check position $($index + 1) must be " +
                    "$requiredCheckId.")
            }
        }
    }
}

function New-ReviewSourceCandidate {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    return [pscustomobject]@{
        Sha256 = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
        SizeBytes = [long]$Bytes.Length
    }
}

function Get-ReviewSourceCandidates {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$BundleDirectory,
        [Parameter(Mandatory)][string]$Description
    )

    $bundlePrefix = $BundleDirectory.TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    $path = [System.IO.Path]::GetFullPath((Join-Path `
        $BundleDirectory `
        $Name.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
    if (-not $path.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-Violation "$Description source file is missing or escapes its evidence bundle: $Name"
        return @()
    }
    $item = Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-Violation "$Description source file must not be a symbolic link or reparse point: $Name"
        return @()
    }

    if ($Name -cnotin @('manifest.yml', 'summary.json')) {
        return @(New-ReviewSourceCandidate -Bytes ([System.IO.File]::ReadAllBytes($item.FullName)))
    }

    $text = Get-StrictUtf8Text `
        -Path $item.FullName `
        -MaximumBytes 1048576 `
        -Description "$Description reviewed $Name"
    if ($null -eq $text) {
        return @()
    }

    $sourceTexts = [System.Collections.Generic.List[string]]::new()
    if ($Name -ceq 'manifest.yml') {
        $reviewMarker = [regex]::new(
            '(?m)^  - "review\.json"(?<marker>\r\n|\n)redaction_reviewed: true(?<tail>\n|\z)')
        $reviewMatches = @($reviewMarker.Matches($text))
        if ($reviewMatches.Count -ne 1) {
            Add-Violation "$Description manifest.yml is not an exact post-review manifest transformation."
            return @()
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
            $roundTrip = $sourcePattern.Replace($sourceText, $replacement, 1)
            if ($roundTrip -ceq $text) {
                $sourceTexts.Add($sourceText)
            }
        }
    }
    else {
        $reviewedPattern = [regex]::new(
            '(?m)(^\s*"redaction_reviewed": )true(?=,?\r?$)')
        if (@($reviewedPattern.Matches($text)).Count -ne 1) {
            Add-Violation "$Description summary.json is not an exact post-review summary transformation."
            return @()
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
        Add-Violation "$Description $Name cannot be reversed to bytes accepted by Review-LiveEvidence.ps1."
        return @()
    }

    $candidates = [System.Collections.Generic.List[object]]::new()
    $identities = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sourceText in $sourceTexts) {
        $candidate = New-ReviewSourceCandidate -Bytes $utf8NoBom.GetBytes($sourceText)
        $identity = "$($candidate.Sha256):$($candidate.SizeBytes)"
        if ($identities.Add($identity)) {
            $candidates.Add($candidate)
        }
    }
    return @($candidates)
}

function Test-ReviewContract {
    param(
        [System.Text.Json.JsonElement]$RootElement,
        [System.Collections.Generic.Dictionary[string, object]]$ManifestValues,
        [string]$ReviewPath,
        [string]$Description
    )

    $isPackageReview = $ManifestValues.ContainsKey('gate_id') -and
        [string]$ManifestValues['gate_id'] -ceq 'PKG-001'
    $requiredProperties = @(
        'schema_version'
        'reviewer_id'
        'reviewed_at_utc'
        'source_bundle_sha256'
        'source_files'
        'review_scope'
    )
    if ($isPackageReview) {
        $requiredProperties += 'manual_observation_confirmed'
    }
    $actualProperties = @($RootElement.EnumerateObject())
    if ($actualProperties.Count -ne $requiredProperties.Count -or
        @($actualProperties | Where-Object { $_.Name -cnotin $requiredProperties }).Count -gt 0) {
        Add-Violation "$Description must contain only the exact review-contract root properties."
    }

    function Get-ReviewProperty([string]$Name) {
        $matches = @($actualProperties | Where-Object { $_.Name -ceq $Name })
        if ($matches.Count -ne 1) {
            Add-Violation "$Description must contain exactly one $Name property."
            return $null
        }
        return $matches[0]
    }

    $schemaVersion = Get-ReviewProperty 'schema_version'
    $parsedSchemaVersion = 0
    if ($null -ne $schemaVersion -and
        ($schemaVersion.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $schemaVersion.Value.TryGetInt32([ref]$parsedSchemaVersion) -or
        $parsedSchemaVersion -ne 1)) {
        Add-Violation "$Description schema_version must be the JSON integer 1."
    }

    $reviewerId = Get-ReviewProperty 'reviewer_id'
    if ($null -ne $reviewerId -and
        ($reviewerId.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
        $reviewerId.Value.GetString() -cnotmatch '^github-[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$')) {
        Add-Violation "$Description reviewer_id must be a bounded github- reviewer identifier."
    }

    $reviewedAt = Get-ReviewProperty 'reviewed_at_utc'
    $parsedReviewedAt = [DateTimeOffset]::MinValue
    $timestampStyles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
        [Globalization.DateTimeStyles]::AdjustToUniversal
    $reviewedAtIsValid = $false
    if ($null -ne $reviewedAt) {
        $reviewedAtIsValid = $reviewedAt.Value.ValueKind -eq
            [System.Text.Json.JsonValueKind]::String -and
            $reviewedAt.Value.GetString() -cmatch
                '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$' -and
            [DateTimeOffset]::TryParse(
                $reviewedAt.Value.GetString(),
                [Globalization.CultureInfo]::InvariantCulture,
                $timestampStyles,
                [ref]$parsedReviewedAt)
    }
    if (-not $reviewedAtIsValid) {
        Add-Violation "$Description reviewed_at_utc must be a valid RFC3339 UTC timestamp ending in Z."
    }
    else {
        if ($parsedReviewedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
            Add-Violation "$Description reviewed_at_utc must not be more than five minutes in the future."
        }
        if ($ManifestValues.ContainsKey('started_at_utc') -and
            $ManifestValues.ContainsKey('duration_seconds')) {
            $startedAt = [DateTimeOffset]::MinValue
            $durationSeconds = [long]0
            $startedAtText = [string]$ManifestValues['started_at_utc']
            $durationText = [string]$ManifestValues['duration_seconds']
            if ($startedAtText -cmatch
                    '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$' -and
                [DateTimeOffset]::TryParse(
                    $startedAtText,
                    [Globalization.CultureInfo]::InvariantCulture,
                    $timestampStyles,
                    [ref]$startedAt) -and
                $durationText -cmatch '^(?:0|[1-9][0-9]*)$' -and
                [long]::TryParse(
                    $durationText,
                    [Globalization.NumberStyles]::None,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [ref]$durationSeconds)) {
                try {
                    $completedAt = $startedAt.AddSeconds($durationSeconds)
                    if ($parsedReviewedAt -lt $completedAt) {
                        Add-Violation "$Description reviewed_at_utc must be at or after the evidence run completed."
                    }
                }
                catch {
                    Add-Violation "$Description evidence completion time is outside the supported timestamp range."
                }
            }
        }
    }

    $sourceBundleSha256 = Get-ReviewProperty 'source_bundle_sha256'
    $declaredBundleDigest = $null
    if ($null -ne $sourceBundleSha256 -and
        $sourceBundleSha256.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
        $declaredBundleDigest = $sourceBundleSha256.Value.GetString()
    }
    if ($declaredBundleDigest -cnotmatch '^[0-9a-f]{64}$' -or
        $declaredBundleDigest -cmatch '^0{64}$') {
        Add-Violation "$Description source_bundle_sha256 must be a nonzero lowercase SHA-256 digest."
    }

    $expectedSourceNames = [System.Collections.Generic.List[string]]::new()
    $expectedSourceNames.Add('manifest.yml')
    if ($ManifestValues.ContainsKey('evidence_files') -and
        $ManifestValues['evidence_files'] -is [System.Collections.Generic.List[string]]) {
        foreach ($evidenceFile in [System.Collections.Generic.List[string]]$ManifestValues['evidence_files']) {
            if ($evidenceFile -cne 'review.json') {
                $expectedSourceNames.Add($evidenceFile)
            }
        }
    }
    $expectedSourceNameArray = $expectedSourceNames.ToArray()
    [Array]::Sort($expectedSourceNameArray, [StringComparer]::Ordinal)

    $sourceFiles = Get-ReviewProperty 'source_files'
    $sourceRecords = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $sourceFiles) {
        if ($sourceFiles.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            Add-Violation "$Description source_files must be a JSON array."
        }
        else {
            foreach ($sourceFile in $sourceFiles.Value.EnumerateArray()) {
                if ($sourceFile.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                    Add-Violation "$Description source_files items must be JSON objects."
                    continue
                }
                $properties = @($sourceFile.EnumerateObject())
                if ($properties.Count -ne 3 -or
                    @($properties | Where-Object { $_.Name -cnotin @('name', 'sha256', 'size_bytes') }).Count -gt 0) {
                    Add-Violation "$Description source_files items must contain only name, sha256, and size_bytes."
                    continue
                }

                $nameProperties = @($properties | Where-Object { $_.Name -ceq 'name' })
                $shaProperties = @($properties | Where-Object { $_.Name -ceq 'sha256' })
                $sizeProperties = @($properties | Where-Object { $_.Name -ceq 'size_bytes' })
                if ($nameProperties.Count -ne 1 -or
                    $shaProperties.Count -ne 1 -or
                    $sizeProperties.Count -ne 1 -or
                    $nameProperties[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
                    $shaProperties[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
                    $sizeProperties[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number) {
                    Add-Violation "$Description source_files item types are invalid."
                    continue
                }

                $name = $nameProperties[0].Value.GetString()
                $sha256 = $shaProperties[0].Value.GetString()
                $sizeBytes = [long]0
                if (-not (Test-SafeEvidencePath -RelativePath $name -Description "$Description source file name") -or
                    $name -ceq 'review.json' -or
                    $sha256 -cnotmatch '^[0-9a-f]{64}$' -or
                    $sha256 -cmatch '^0{64}$' -or
                    -not $sizeProperties[0].Value.TryGetInt64([ref]$sizeBytes) -or
                    $sizeBytes -lt 0) {
                    Add-Violation "$Description source_files contains an invalid source record."
                    continue
                }
                $sourceRecords.Add([pscustomobject]@{
                    Name = $name
                    Sha256 = $sha256
                    SizeBytes = $sizeBytes
                })
            }
        }
    }

    $actualSourceNames = @($sourceRecords | ForEach-Object { $_.Name })
    if ($actualSourceNames.Count -ne $expectedSourceNameArray.Count) {
        Add-Violation "$Description source_files must cover the source manifest and every original declared file exactly once."
    }
    else {
        for ($index = 0; $index -lt $expectedSourceNameArray.Count; $index++) {
            if ($actualSourceNames[$index] -cne $expectedSourceNameArray[$index]) {
                Add-Violation "$Description source_files must be unique and sorted by ordinal name."
                break
            }
        }
    }

    if ($sourceRecords.Count -gt 0 -and
        -not [string]::IsNullOrWhiteSpace($ReviewPath)) {
        $bundleDirectory = Split-Path -Parent $ReviewPath
        foreach ($record in $sourceRecords) {
            $candidates = @(Get-ReviewSourceCandidates `
                -Name $record.Name `
                -BundleDirectory $bundleDirectory `
                -Description $Description)
            if (@($candidates | Where-Object {
                        $_.Sha256 -ceq $record.Sha256 -and
                        $_.SizeBytes -eq $record.SizeBytes
                    }).Count -ne 1) {
                Add-Violation "$Description source_files does not bind the exact reviewed source bytes for $($record.Name)."
            }
        }
    }

    if ($sourceRecords.Count -gt 0) {
        $canonicalRecords = [Text.StringBuilder]::new()
        foreach ($record in $sourceRecords) {
            $canonicalRecords.Append($record.Sha256) | Out-Null
            $canonicalRecords.Append(' ') | Out-Null
            $canonicalRecords.Append($record.SizeBytes.ToString(
                [Globalization.CultureInfo]::InvariantCulture)) | Out-Null
            $canonicalRecords.Append(' ') | Out-Null
            $canonicalRecords.Append($record.Name) | Out-Null
            $canonicalRecords.Append("`n") | Out-Null
        }
        $canonicalBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
            $canonicalRecords.ToString())
        $computedDigest = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData($canonicalBytes)).ToLowerInvariant()
        if ($declaredBundleDigest -cne $computedDigest) {
            Add-Violation "$Description source_bundle_sha256 does not match the canonical source_files digest."
        }
    }

    $reviewScope = Get-ReviewProperty 'review_scope'
    if ($null -ne $reviewScope) {
        $scopeValues = if ($reviewScope.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
            @($reviewScope.Value.EnumerateArray())
        }
        else {
            @()
        }
        if ($scopeValues.Count -ne 2 -or
            $scopeValues[0].ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $scopeValues[0].GetString() -cne 'privacy-redaction' -or
            $scopeValues[1].ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $scopeValues[1].GetString() -cne 'bundle-integrity') {
            Add-Violation "$Description review_scope must be exactly privacy-redaction followed by bundle-integrity."
        }
    }
    if ($isPackageReview) {
        $manualObservationConfirmed = Get-ReviewProperty 'manual_observation_confirmed'
        if ($null -ne $manualObservationConfirmed -and
            $manualObservationConfirmed.Value.ValueKind -ne
                [System.Text.Json.JsonValueKind]::True) {
            Add-Violation "$Description manual_observation_confirmed must be the JSON boolean true for PKG-001."
        }
    }
}

function Test-EvidenceJson {
    param(
        [string]$Path,
        [string]$Description,
        [System.Collections.Generic.Dictionary[string, object]]$ManifestValues,
        [switch]$Summary,
        [switch]$Review
    )

    $text = Get-StrictUtf8Text -Path $Path -MaximumBytes 1048576 -Description $Description
    if ($null -eq $text) {
        return
    }
    if (-not $Review) {
        Test-ForbiddenText -Text $text -Description $Description
    }

    try {
        $document = [System.Text.Json.JsonDocument]::Parse($text)
        try {
            if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                Add-Violation "$Description must contain one JSON object."
                return
            }
            if (-not $Review) {
                Test-JsonElement -Element $document.RootElement -Description $Description
            }
            if ($Summary) {
                Test-SummaryContract `
                    -RootElement $document.RootElement `
                    -ManifestValues $ManifestValues `
                    -Description $Description
            }
            if ($Review) {
                Test-ReviewContract `
                    -RootElement $document.RootElement `
                    -ManifestValues $ManifestValues `
                    -ReviewPath $Path `
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
        [string]$Description,
        [string]$ScopeRequiredGateId,
        [string]$ScopeForbiddenGateId
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
    $gateId = Get-Value 'gate_id'
    $effectiveRequiredGateId = if (-not [string]::IsNullOrWhiteSpace($ScopeRequiredGateId)) {
        $ScopeRequiredGateId
    }
    else {
        $RequiredGateId
    }
    if ($gateId -cnotmatch '^(?=.{1,64}$)[A-Z0-9]+(?:-[A-Z0-9]+)+$') {
        Add-Violation "$Description gate_id must be an uppercase hyphenated identifier of at most 64 characters."
    }
    elseif (-not [string]::IsNullOrWhiteSpace($effectiveRequiredGateId) -and
        $gateId -cne $effectiveRequiredGateId) {
        Add-Violation "$Description gate_id does not satisfy the required release gate."
    }
    if (-not [string]::IsNullOrWhiteSpace($ScopeForbiddenGateId) -and
        $gateId -ceq $ScopeForbiddenGateId) {
        Add-Violation "$Description gate_id is not allowed in this evidence scope."
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

    $windowsBuild = Get-Value 'windows_build'
    $windowsBuildMatch = [regex]::Match(
        $windowsBuild,
        '^(?:10\.0\.)?([0-9]{5})(?:\.[0-9]{1,6})?$')
    if (-not $windowsBuildMatch.Success) {
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
        'Direct', 'HttpConnect', 'Socks4', 'Socks5', 'SshJump', 'ExternalProxyCommand',
        'NotApplicable')) {
        Add-Violation "$Description route is outside the allowed enum."
    }
    if ((Get-Value 'authentication') -cnotin @(
        'Password', 'PublicKey', 'Agent', 'KeyboardInteractive', 'NotApplicable')) {
        Add-Violation "$Description authentication is outside the allowed enum."
    }
    if ((Get-Value 'expected_host_fingerprint') -cnotin @('SHA256:[redacted]', 'NotRecorded')) {
        Add-Violation "$Description expected_host_fingerprint must be a redacted contract value."
    }

    # The gate identifier defines its environment tuple even during whole-root
    # repository scans.  RequiredGateId is only an additional caller binding;
    # relying on it here would let a mislabeled SSH-LIVE-001 bundle pass CI.
    if ($gateId -ceq 'SSH-LIVE-001') {
        if (-not $windowsBuildMatch.Success -or [int]$windowsBuildMatch.Groups[1].Value -lt 26100) {
            Add-Violation "$Description SSH-LIVE-001 requires Windows 11 24H2 build 26100 or newer."
        }
        if ((Get-Value 'architecture') -cne 'x64' -or
            (Get-Value 'route') -cne 'Direct' -or
            (Get-Value 'authentication') -cne 'Password' -or
            (Get-Value 'expected_host_fingerprint') -cne 'SHA256:[redacted]') {
            Add-Violation "$Description SSH-LIVE-001 requires x64, Direct, Password, and a reviewed redacted host fingerprint."
        }
    }
    elseif ($gateId -ceq 'PKG-001') {
        if (-not $windowsBuildMatch.Success -or [int]$windowsBuildMatch.Groups[1].Value -lt 26100) {
            Add-Violation "$Description PKG-001 requires Windows 11 24H2 build 26100 or newer."
        }
        if ((Get-Value 'architecture') -cne 'x64' -or
            (Get-Value 'server_family') -cne 'NotApplicable' -or
            (Get-Value 'server_version') -cne 'NotApplicable' -or
            (Get-Value 'route') -cne 'NotApplicable' -or
            (Get-Value 'authentication') -cne 'NotApplicable' -or
            (Get-Value 'expected_host_fingerprint') -cne 'NotRecorded') {
            Add-Violation "$Description PKG-001 requires the exact x64 package and the canonical non-SSH tuple."
        }
    }
    elseif ((Get-Value 'server_family') -ceq 'NotApplicable' -or
        (Get-Value 'server_version') -ceq 'NotApplicable' -or
        (Get-Value 'route') -ceq 'NotApplicable' -or
        (Get-Value 'authentication') -ceq 'NotApplicable') {
        Add-Violation "$Description NotApplicable manifest values are reserved for PKG-001."
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
    if ($redactionReviewed -ceq 'true') {
        if (@($evidenceFiles | Where-Object { $_ -ceq 'review.json' }).Count -ne 1 -or
            $evidenceFiles[$evidenceFiles.Count - 1] -cne 'review.json') {
            Add-Violation "$Description reviewed evidence must declare review.json exactly once as its final evidence_files item."
        }
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
                    -Summary:($relativePath -ceq 'summary.json') `
                    -Review:($relativePath -ceq 'review.json')
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
    $expectedDirectories = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($declaredFile in $normalizedEvidenceFiles) {
        $segments = @($declaredFile.Split('/'))
        if ($segments.Count -le 1) {
            continue
        }
        $relativeDirectory = [System.Collections.Generic.List[string]]::new()
        for ($segmentIndex = 0; $segmentIndex -lt $segments.Count - 1; $segmentIndex++) {
            $relativeDirectory.Add($segments[$segmentIndex])
            $expectedDirectories.Add(($relativeDirectory -join '/')) | Out-Null
        }
    }
    $actualDirectories = @(
        $descendantDirectories | ForEach-Object {
            [System.IO.Path]::GetRelativePath($bundleDirectory, $_.FullName).Replace('\', '/')
        }
    )
    foreach ($actualDirectory in $actualDirectories) {
        if (-not $expectedDirectories.Contains($actualDirectory)) {
            Add-Violation "$Description bundle contains an undeclared or empty directory."
        }
    }
    foreach ($expectedDirectory in $expectedDirectories) {
        if ($expectedDirectory -cnotin $actualDirectories) {
            Add-Violation "$Description evidence_files references a missing directory."
        }
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
if (-not [string]::IsNullOrWhiteSpace($RequiredGateId) -and
    $RequiredGateId -cnotmatch '^(?=.{1,64}$)[A-Z0-9]+(?:-[A-Z0-9]+)+$') {
    Add-Violation 'RequiredGateId must be an uppercase hyphenated identifier of at most 64 characters.'
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
            $rootItems = @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force)
            if (@($rootItems | Where-Object {
                ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            }).Count -gt 0) {
                Add-Violation 'Live-evidence root contains a symbolic link or reparse point.'
            }
            else {
                $rootChildren = @(Get-ChildItem -LiteralPath $resolvedRoot -Force)
                $schemaFiles = @($rootChildren | Where-Object {
                    -not $_.PSIsContainer -and $_.Name -ceq 'EVIDENCE_SCHEMA.md'
                })
                if ($schemaFiles.Count -ne 1) {
                    Add-Violation 'Live-evidence root must contain exactly one EVIDENCE_SCHEMA.md file.'
                }

                foreach ($rootChild in $rootChildren) {
                    if (-not $rootChild.PSIsContainer) {
                        if ($rootChild.Name -cne 'EVIDENCE_SCHEMA.md') {
                            Add-Violation 'Live-evidence root contains a file outside the exact allowlist.'
                        }
                        continue
                    }

                    $releaseDirectoryName = $rootChild.Name
                    if (-not $approvedEvidenceScopes.ContainsKey($releaseDirectoryName)) {
                        Add-Violation 'Live-evidence root contains an unapproved or non-canonical release directory.'
                        continue
                    }

                    $releaseChildren = @(Get-ChildItem -LiteralPath $rootChild.FullName -Force)
                    $releaseReadmes = @($releaseChildren | Where-Object {
                        -not $_.PSIsContainer -and $_.Name -ceq 'README.md'
                    })
                    if ($releaseReadmes.Count -ne 1) {
                        Add-Violation "Live-evidence release directory $releaseDirectoryName must contain exactly one README.md file."
                    }

                    foreach ($releaseChild in $releaseChildren) {
                        if (-not $releaseChild.PSIsContainer) {
                            if ($releaseChild.Name -cne 'README.md') {
                                Add-Violation "Live-evidence release directory $releaseDirectoryName contains a file outside the exact allowlist."
                            }
                            continue
                        }

                        $scopeName = $releaseChild.Name
                        if ($scopeName -cnotin $approvedEvidenceScopes[$releaseDirectoryName]) {
                            Add-Violation "Live-evidence release directory $releaseDirectoryName contains an unapproved or non-canonical scope directory."
                            continue
                        }

                        $scopeChildren = @(Get-ChildItem -LiteralPath $releaseChild.FullName -Force)
                        $scopeReadmes = @($scopeChildren | Where-Object {
                            -not $_.PSIsContainer -and $_.Name -ceq 'README.md'
                        })
                        if ($scopeReadmes.Count -ne 1) {
                            Add-Violation "Live-evidence scope $releaseDirectoryName/$scopeName must contain exactly one README.md file."
                        }

                        foreach ($scopeChild in $scopeChildren) {
                            if (-not $scopeChild.PSIsContainer) {
                                if ($scopeChild.Name -cne 'README.md') {
                                    Add-Violation "Live-evidence scope $releaseDirectoryName/$scopeName contains a file outside the exact allowlist."
                                }
                                continue
                            }

                            $bundleName = $scopeChild.Name
                            if ($bundleName -cnotmatch '^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$') {
                                Add-Violation "Live-evidence scope $releaseDirectoryName/$scopeName contains a non-canonical bundle directory."
                                continue
                            }

                            $bundleChildren = @(Get-ChildItem -LiteralPath $scopeChild.FullName -Force)
                            $manifests = @($bundleChildren | Where-Object {
                                -not $_.PSIsContainer -and $_.Name -ceq 'manifest.yml'
                            })
                            if ($manifests.Count -ne 1) {
                                Add-Violation "Live-evidence bundle $releaseDirectoryName/$scopeName/$bundleName must contain exactly one manifest.yml file."
                                continue
                            }

                            $relativeManifest = "$releaseDirectoryName/$scopeName/$bundleName/manifest.yml"
                            $scopeRequiredGateId = if ($scopeName -ceq 'package') {
                                'PKG-001'
                            }
                            else {
                                $null
                            }
                            $scopeForbiddenGateId = if ($scopeName -cne 'package') {
                                'PKG-001'
                            }
                            else {
                                $null
                            }
                            Test-LiveEvidenceManifest `
                                -Path $manifests[0].FullName `
                                -Description "live-evidence manifest $relativeManifest" `
                                -ScopeRequiredGateId $scopeRequiredGateId `
                                -ScopeForbiddenGateId $scopeForbiddenGateId
                            $validatedCount++
                        }
                    }
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
