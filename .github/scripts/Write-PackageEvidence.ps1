[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ObservedUiPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+$')]
    [string]$Tag,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$Commit,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$EvidenceOutputRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$')]
    [string]$StartedAtUtc,

    [Parameter(Mandatory)]
    [ValidateRange(1, 86400)]
    [long]$DurationSeconds,

    [Parameter(Mandatory)]
    [ValidateSet('Pass', 'Fail', 'Blocked')]
    [string]$UiStartupResult,

    [Parameter(Mandatory)]
    [ValidateSet('Pass', 'Fail', 'Blocked')]
    [string]$AltNavigationSilentResult,

    [Parameter(Mandatory)]
    [ValidateRange(0, 7)]
    [int]$AltNavigationShortcutCount,

    [Parameter(Mandatory)]
    [ValidateSet('Pass', 'Fail', 'Blocked')]
    [string]$UiShutdownResult
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8Strict = [Text.UTF8Encoding]::new($false, $true)
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$privacyNotice =
    'Connection identifiers, credentials, filesystem locations, session content, and cryptographic material are excluded.'

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

function Test-SafeZipEntryName {
    param([Parameter(Mandatory)][string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 512 -or
        $Name.StartsWith('/', [StringComparison]::Ordinal) -or
        $Name.Contains('\', [StringComparison]::Ordinal) -or
        $Name.Contains(':', [StringComparison]::Ordinal) -or
        $Name.IndexOfAny([char[]](0..31 + 127)) -ge 0) {
        return $false
    }
    $path = if ($Name.EndsWith('/', [StringComparison]::Ordinal)) {
        $Name.Substring(0, $Name.Length - 1)
    }
    else {
        $Name
    }
    if ([string]::IsNullOrEmpty($path)) {
        return $false
    }
    $reservedNames = 'CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]'
    foreach ($segment in $path.Split('/')) {
        if ($segment.Length -lt 1 -or $segment.Length -gt 128 -or
            $segment -cin @('.', '..') -or
            $segment.EndsWith(' ', [StringComparison]::Ordinal) -or
            $segment.EndsWith('.', [StringComparison]::Ordinal) -or
            $segment.IndexOfAny([char[]]'< >"|?*'.Replace(' ', '')) -ge 0 -or
            $segment.Split('.')[0] -cmatch "^(?:$reservedNames)$") {
            return $false
        }
    }
    return $true
}

function Get-PhysicalTreeInventory {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Description
    )

    $rootItem = Get-Item -LiteralPath $Root -Force
    if ($rootItem -isnot [IO.DirectoryInfo] -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description root must be one physical directory."
    }

    $directories = [Collections.Generic.List[string]]::new()
    $files = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push($rootItem)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in $directory.GetFileSystemInfos()) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description contains a symbolic link or reparse point."
            }
            $relativePath = [IO.Path]::GetRelativePath(
                $rootItem.FullName,
                $item.FullName).Replace('\', '/')
            if ($item -is [IO.DirectoryInfo]) {
                if (-not (Test-SafeZipEntryName -Name "$relativePath/")) {
                    throw "$Description contains a non-portable directory path."
                }
                $directories.Add($relativePath)
                $pending.Push($item)
            }
            elseif ($item -is [IO.FileInfo]) {
                if (-not (Test-SafeZipEntryName -Name $relativePath)) {
                    throw "$Description contains a non-portable file path."
                }
                $files.Add([pscustomobject]@{
                    Path = $relativePath
                    FullName = $item.FullName
                    Length = [long]$item.Length
                })
            }
            else {
                throw "$Description contains an unsupported filesystem object."
            }
        }
    }

    return [pscustomobject]@{
        Directories = $directories.ToArray()
        Files = $files.ToArray()
    }
}

function Add-ExpectedDirectory {
    param(
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, string]]$Directories,
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, object]]$Files,
        [Parameter(Mandatory)][string]$Path
    )

    if ($Files.ContainsKey($Path)) {
        throw 'The Candidate ZIP contains a file/directory path collision.'
    }
    if ($Directories.ContainsKey($Path)) {
        if ($Directories[$Path] -cne $Path) {
            throw 'The Candidate ZIP contains inconsistent directory path casing.'
        }
        return
    }
    $Directories.Add($Path, $Path)
}

function Add-ExpectedParentDirectories {
    param(
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, string]]$Directories,
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, object]]$Files,
        [Parameter(Mandatory)][string]$Path
    )

    $segments = @($Path.Split('/'))
    for ($count = 1; $count -lt $segments.Count; $count++) {
        Add-ExpectedDirectory `
            -Directories $Directories `
            -Files $Files `
            -Path ([string]::Join('/', $segments[0..($count - 1)]))
    }
}

function Assert-ObservedTreeShape {
    param(
        [Parameter(Mandatory)]$Inventory,
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, object]]$ExpectedFiles,
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, string]]$ExpectedDirectories
    )

    $actualFiles = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Inventory.Files) {
        if ($actualFiles.ContainsKey($file.Path)) {
            throw 'The observed Candidate tree contains duplicate case-insensitive file paths.'
        }
        $actualFiles.Add($file.Path, $file)
        if (-not $ExpectedFiles.ContainsKey($file.Path)) {
            throw 'The observed Candidate tree contains an extra file.'
        }
        $expected = $ExpectedFiles[$file.Path]
        if ($file.Path -cne $expected.Path -or $file.Length -ne $expected.Length) {
            throw 'The observed Candidate tree file path, casing, or size differs from the ZIP.'
        }
    }
    if ($actualFiles.Count -ne $ExpectedFiles.Count) {
        throw 'The observed Candidate tree is missing a ZIP file.'
    }

    $actualDirectories = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($directory in $Inventory.Directories) {
        if ($actualDirectories.ContainsKey($directory)) {
            throw 'The observed Candidate tree contains duplicate case-insensitive directories.'
        }
        $actualDirectories.Add($directory, $directory)
        if (-not $ExpectedDirectories.ContainsKey($directory) -or
            $directory -cne $ExpectedDirectories[$directory]) {
            throw 'The observed Candidate tree contains an extra or case-mismatched directory.'
        }
    }
    if ($actualDirectories.Count -ne $ExpectedDirectories.Count) {
        throw 'The observed Candidate tree is missing a ZIP directory.'
    }

    return [pscustomobject]@{
        FilesByPath = $actualFiles
    }
}

function Write-DurableText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
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

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'PKG-001 must be recorded on Windows.'
}
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne
    [Runtime.InteropServices.Architecture]::X64) {
    throw 'PKG-001 recording requires an x64 recorder process for the exact x64 Candidate.'
}
$osVersion = [Environment]::OSVersion.Version
if ($osVersion.Build -lt 26100) {
    throw 'PKG-001 requires Windows 11 24H2 build 26100 or newer.'
}
$windowsBuild = "10.0.$($osVersion.Build).$([Math]::Max(0, $osVersion.Revision))"

$parsedStartedAt = [DateTimeOffset]::MinValue
$timestampStyles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
    [Globalization.DateTimeStyles]::AdjustToUniversal
if (-not [DateTimeOffset]::TryParse(
        $StartedAtUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        $timestampStyles,
        [ref]$parsedStartedAt) -or
    $parsedStartedAt.Offset -ne [TimeSpan]::Zero) {
    throw 'StartedAtUtc must be a valid RFC3339 UTC timestamp ending in Z.'
}
if ($parsedStartedAt.AddSeconds($DurationSeconds) -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
    throw 'The declared PKG-001 run completion must not be in the future.'
}
if ($UiStartupResult -cne 'Pass' -and
    ($AltNavigationSilentResult -cne 'Blocked' -or $UiShutdownResult -cne 'Blocked')) {
    throw 'Alt navigation and shutdown must be Blocked when UI startup did not pass.'
}
if ($UiStartupResult -cne 'Pass' -and $AltNavigationShortcutCount -ne 0) {
    throw 'AltNavigationShortcutCount must be 0 when UI startup did not pass.'
}
if ($AltNavigationSilentResult -ceq 'Pass' -and $AltNavigationShortcutCount -ne 7) {
    throw 'A silent Alt navigation Pass requires exactly 7 observed shortcuts.'
}
if ($AltNavigationSilentResult -ceq 'Fail' -and $AltNavigationShortcutCount -lt 1) {
    throw 'An Alt navigation Fail requires at least 1 observed shortcut.'
}

if (-not [IO.Path]::IsPathFullyQualified($PackagePath) -or
    -not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw 'PackagePath must be an absolute path to the exact Candidate ZIP.'
}
$resolvedPackage = (Get-Item -LiteralPath (Resolve-Path -LiteralPath $PackagePath).Path -Force)
if (($resolvedPackage.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'PackagePath must not be a symbolic link or reparse point.'
}
Assert-PhysicalAncestors -Path $resolvedPackage.Directory.FullName -Description 'PackagePath'
$expectedPackageName = "Sutty-$Tag-win-x64.zip"
if ($resolvedPackage.Name -cne $expectedPackageName) {
    throw "PackagePath filename must be exactly $expectedPackageName."
}
if (-not [IO.Path]::IsPathFullyQualified($ObservedUiPath) -or
    -not (Test-Path -LiteralPath $ObservedUiPath -PathType Leaf)) {
    throw 'ObservedUiPath must be an absolute path to the unpacked Candidate sutty.UI.exe.'
}
$resolvedObservedUi = Get-Item -LiteralPath (Resolve-Path -LiteralPath $ObservedUiPath).Path -Force
if ($resolvedObservedUi.Name -cne 'sutty.UI.exe' -or
    ($resolvedObservedUi.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'ObservedUiPath must be a physical file named exactly sutty.UI.exe.'
}
Assert-PhysicalAncestors `
    -Path $resolvedObservedUi.Directory.FullName `
    -Description 'ObservedUiPath'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageSha256 = $null
$expectedFiles = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$expectedDirectories = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$packageStream = [IO.FileStream]::new(
    $resolvedPackage.FullName,
    [IO.FileMode]::Open,
    [IO.FileAccess]::Read,
    [IO.FileShare]::None,
    65536,
    [IO.FileOptions]::SequentialScan)
try {
    $packageSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($packageStream)).ToLowerInvariant()
    $packageStream.Position = 0
    $archive = [IO.Compression.ZipArchive]::new(
        $packageStream,
        [IO.Compression.ZipArchiveMode]::Read,
        $true)
    try {
    if ($archive.Entries.Count -lt 2 -or $archive.Entries.Count -gt 10000) {
        throw 'The Candidate ZIP entry count is outside the bounded review contract.'
    }
    $entryNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $buildInfoEntries = [Collections.Generic.List[IO.Compression.ZipArchiveEntry]]::new()
    $uiEntries = [Collections.Generic.List[IO.Compression.ZipArchiveEntry]]::new()
    $explicitDirectories = [Collections.Generic.List[string]]::new()
    [long]$totalUncompressedBytes = 0
    foreach ($entry in $archive.Entries) {
        if (-not (Test-SafeZipEntryName -Name $entry.FullName) -or
            -not $entryNames.Add($entry.FullName)) {
            throw 'The Candidate ZIP contains an unsafe or duplicate entry.'
        }
        $unixFileType = ($entry.ExternalAttributes -shr 16) -band 0xF000
        $dosAttributes = $entry.ExternalAttributes -band 0xFFFF
        if ($unixFileType -eq 0xA000 -or
            ($dosAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The Candidate ZIP contains a symbolic-link or reparse-point entry.'
        }
        $isDirectory = $entry.FullName.EndsWith('/', [StringComparison]::Ordinal)
        if (($unixFileType -ne 0 -and $unixFileType -ne 0x4000 -and
                $unixFileType -ne 0x8000) -or
            ($isDirectory -and $unixFileType -eq 0x8000) -or
            (-not $isDirectory -and $unixFileType -eq 0x4000) -or
            ($isDirectory -and $entry.Length -ne 0)) {
            throw 'The Candidate ZIP contains an unsupported or inconsistent entry type.'
        }
        if ($isDirectory) {
            $explicitDirectories.Add(
                $entry.FullName.Substring(0, $entry.FullName.Length - 1))
            continue
        }
        if ($entry.Length -lt 0 -or $entry.Length -gt 536870912 -or
            $totalUncompressedBytes -gt (2147483648L - $entry.Length)) {
            throw 'The Candidate ZIP uncompressed file inventory exceeds its safety bounds.'
        }
        $totalUncompressedBytes += $entry.Length
        if ($expectedFiles.ContainsKey($entry.FullName)) {
            throw 'The Candidate ZIP contains duplicate case-insensitive file paths.'
        }
        $entryStream = $entry.Open()
        try {
            $entrySha256 = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($entryStream)).ToLowerInvariant()
        }
        finally {
            $entryStream.Dispose()
        }
        $expectedFiles.Add($entry.FullName, [pscustomobject]@{
            Path = $entry.FullName
            Length = [long]$entry.Length
            Sha256 = $entrySha256
        })
        if ($entry.FullName -ceq 'BUILDINFO.txt') {
            $buildInfoEntries.Add($entry)
        }
        if ($entry.FullName -ceq 'sutty.UI.exe') {
            $uiEntries.Add($entry)
        }
    }
    if ($buildInfoEntries.Count -ne 1 -or $buildInfoEntries[0].Length -lt 1 -or
        $buildInfoEntries[0].Length -gt 8192) {
        throw 'The Candidate ZIP must contain one bounded root BUILDINFO.txt.'
    }
    if ($uiEntries.Count -ne 1 -or $uiEntries[0].Length -lt 1 -or
        $uiEntries[0].Length -gt 536870912) {
        throw 'The Candidate ZIP must contain one bounded root sutty.UI.exe.'
    }
    foreach ($directory in $explicitDirectories) {
        Add-ExpectedDirectory `
            -Directories $expectedDirectories `
            -Files $expectedFiles `
            -Path $directory
        Add-ExpectedParentDirectories `
            -Directories $expectedDirectories `
            -Files $expectedFiles `
            -Path $directory
    }
    foreach ($file in $expectedFiles.Values) {
        Add-ExpectedParentDirectories `
            -Directories $expectedDirectories `
            -Files $expectedFiles `
            -Path $file.Path
    }

    $buildInfoStream = $buildInfoEntries[0].Open()
    try {
        $memory = [IO.MemoryStream]::new()
        try {
            $buildInfoStream.CopyTo($memory)
            $buildInfoBytes = $memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $buildInfoStream.Dispose()
    }
    if ($buildInfoBytes.Length -ge 3 -and
        $buildInfoBytes[0] -eq 0xef -and $buildInfoBytes[1] -eq 0xbb -and
        $buildInfoBytes[2] -eq 0xbf) {
        throw 'BUILDINFO.txt must be UTF-8 without a byte-order mark.'
    }
    try {
        $buildInfoText = $utf8Strict.GetString($buildInfoBytes)
    }
    catch {
        throw 'BUILDINFO.txt must be strict UTF-8.'
    }
    $buildInfoLines = @([regex]::Split($buildInfoText, '\r?\n'))
    while ($buildInfoLines.Count -gt 0 -and $buildInfoLines[-1] -ceq '') {
        $buildInfoLines = @($buildInfoLines | Select-Object -First ($buildInfoLines.Count - 1))
    }
    $expectedBuildInfo = @(
        "Sutty $Tag"
        "Commit: $Commit"
        'Channel: Alpha'
        'Signing: unsigned ZIP evaluation build'
        'Minimum OS: Windows 11 24H2'
        'Architecture: x64'
    )
    if ([string]::Join("`n", $buildInfoLines) -cne [string]::Join("`n", $expectedBuildInfo)) {
        throw 'BUILDINFO.txt does not bind the exact Alpha tag, commit, and x64 identity.'
    }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $packageStream.Dispose()
}

if ($packageSha256 -cnotmatch '^[0-9a-f]{64}$' -or $packageSha256 -cmatch '^0{64}$') {
    throw 'The exact Candidate ZIP did not produce a canonical SHA-256 digest.'
}
$observedRoot = $resolvedObservedUi.Directory.FullName
$observedInventory = Get-PhysicalTreeInventory `
    -Root $observedRoot `
    -Description 'Observed Candidate tree'
$observedShape = Assert-ObservedTreeShape `
    -Inventory $observedInventory `
    -ExpectedFiles $expectedFiles `
    -ExpectedDirectories $expectedDirectories
$observedStreams = [Collections.Generic.List[IO.FileStream]]::new()
try {
    foreach ($expectedFile in $expectedFiles.Values) {
        $observedFile = $observedShape.FilesByPath[$expectedFile.Path]
        $observedStream = [IO.FileStream]::new(
            $observedFile.FullName,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::None,
            65536,
            [IO.FileOptions]::SequentialScan)
        $observedStreams.Add($observedStream)
        if ($observedStream.Length -ne $expectedFile.Length) {
            throw 'The observed Candidate tree contains a file with a changed size.'
        }
        $observedSha256 = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($observedStream)).ToLowerInvariant()
        if ($observedSha256 -cne $expectedFile.Sha256) {
            throw 'The observed Candidate tree contains a file whose SHA-256 differs from the ZIP.'
        }
    }

    # Re-enumerate while every expected file is exclusively open so an insertion,
    # deletion, replacement, or reparse-point swap cannot escape the comparison.
    $finalObservedInventory = Get-PhysicalTreeInventory `
        -Root $observedRoot `
        -Description 'Observed Candidate tree'
    Assert-ObservedTreeShape `
        -Inventory $finalObservedInventory `
        -ExpectedFiles $expectedFiles `
        -ExpectedDirectories $expectedDirectories | Out-Null
}
finally {
    for ($index = $observedStreams.Count - 1; $index -ge 0; $index--) {
        $observedStreams[$index].Dispose()
    }
}

$checks = @(
    [ordered]@{ id = 'package-sha256'; result = 'Pass' }
    [ordered]@{ id = 'package-commit-identity'; result = 'Pass' }
    [ordered]@{ id = 'package-tree-identity'; result = 'Pass' }
    [ordered]@{ id = 'ui-startup'; result = $UiStartupResult }
    [ordered]@{ id = 'alt-navigation-silent'; result = $AltNavigationSilentResult }
    [ordered]@{ id = 'ui-shutdown'; result = $UiShutdownResult }
)
$checkResults = @($checks | ForEach-Object { $_.result })
$result = if ($checkResults -contains 'Fail') {
    'Fail'
}
elseif ($checkResults -contains 'Blocked') {
    'Blocked'
}
else {
    'Pass'
}
$summary = [ordered]@{
    schema_version = 1
    gate_id = 'PKG-001'
    result = $result
    started_at_utc = $StartedAtUtc
    duration_seconds = $DurationSeconds
    checks = $checks
    measurements = [ordered]@{
        check_count = 6
        passed_count = @($checkResults | Where-Object { $_ -ceq 'Pass' }).Count
        failed_count = @($checkResults | Where-Object { $_ -ceq 'Fail' }).Count
        blocked_count = @($checkResults | Where-Object { $_ -ceq 'Blocked' }).Count
        package_sha256_verified = $true
        package_commit_identity_verified = $true
        package_tree_identity_verified = $true
        ui_startup_verified = $UiStartupResult -ceq 'Pass'
        alt_navigation_silent_verified = $AltNavigationSilentResult -ceq 'Pass'
        ui_shutdown_verified = $UiShutdownResult -ceq 'Pass'
        alt_navigation_shortcut_count = $AltNavigationShortcutCount
    }
    redaction_reviewed = $false
    privacy_notice = $privacyNotice
}
if ($result -ceq 'Fail') {
    $summary.failed_check_id = @($checks | Where-Object { $_.result -ceq 'Fail' })[0].id
}
elseif ($result -ceq 'Blocked') {
    $summary.blocking_category = 'ManualPackageGateIncomplete'
}
$summaryText = ($summary | ConvertTo-Json -Depth 8) + [Environment]::NewLine
$manifestText = @(
    'schema_version: 1'
    'gate_id: "PKG-001"'
    "commit: `"$Commit`""
    "package_sha256: `"$packageSha256`""
    "windows_build: `"$windowsBuild`""
    'architecture: "x64"'
    'server_family: "NotApplicable"'
    'server_version: "NotApplicable"'
    'route: "NotApplicable"'
    'authentication: "NotApplicable"'
    'expected_host_fingerprint: "NotRecorded"'
    "result: `"$result`""
    "started_at_utc: `"$StartedAtUtc`""
    "duration_seconds: $DurationSeconds"
    'evidence_files:'
    '  - "summary.json"'
    'redaction_reviewed: false'
    ''
) -join [Environment]::NewLine

if (-not [IO.Path]::IsPathFullyQualified($EvidenceOutputRoot)) {
    throw 'EvidenceOutputRoot must be an absolute path.'
}
$resolvedOutputRoot = [IO.Path]::GetFullPath($EvidenceOutputRoot)
if ($resolvedOutputRoot.TrimEnd('\', '/') -ceq
    ([IO.Path]::GetPathRoot($resolvedOutputRoot)).TrimEnd('\', '/')) {
    throw 'EvidenceOutputRoot must not be a filesystem root.'
}
$repositoryEvidenceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\docs\evidence'))
$repositoryEvidencePrefix = $repositoryEvidenceRoot.TrimEnd('\', '/') +
    [IO.Path]::DirectorySeparatorChar
if ($resolvedOutputRoot.Equals(
        $repositoryEvidenceRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedOutputRoot.StartsWith($repositoryEvidencePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unreviewed PKG-001 output must remain outside the committed docs/evidence tree.'
}
[IO.Directory]::CreateDirectory($resolvedOutputRoot) | Out-Null
$resolvedOutputRoot = (Get-Item -LiteralPath $resolvedOutputRoot -Force).FullName
Assert-PhysicalAncestors -Path $resolvedOutputRoot -Description 'EvidenceOutputRoot'

$timestampSegment = $parsedStartedAt.UtcDateTime.ToString(
    'yyyyMMddTHHmmssZ', [Globalization.CultureInfo]::InvariantCulture).ToLowerInvariant()
$bundleName = "pkg-001-$timestampSegment-$($packageSha256.Substring(0, 12))"
$finalPath = Join-Path $resolvedOutputRoot $bundleName
$stagingPath = Join-Path $resolvedOutputRoot (
    ".sutty-pkg-evidence-staging-$([Guid]::NewGuid().ToString('N'))")
if (Test-Path -LiteralPath $finalPath) {
    throw 'The PKG-001 source bundle already exists; evidence is write-once.'
}
[IO.Directory]::CreateDirectory($stagingPath) | Out-Null
try {
    Write-DurableText -Path (Join-Path $stagingPath 'manifest.yml') -Content $manifestText
    Write-DurableText -Path (Join-Path $stagingPath 'summary.json') -Content $summaryText
    [IO.Directory]::Move($stagingPath, $finalPath)
}
catch {
    if (Test-Path -LiteralPath $stagingPath -PathType Container) {
        $stagingItem = Get-Item -LiteralPath $stagingPath -Force
        $expectedPrefix = $resolvedOutputRoot.TrimEnd('\', '/') +
            [IO.Path]::DirectorySeparatorChar + '.sutty-pkg-evidence-staging-'
        if (($stagingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
            $stagingItem.FullName.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            [IO.Directory]::Delete($stagingItem.FullName, $true)
        }
    }
    throw
}

Write-Host "Unreviewed PKG-001 source bundle created: $finalPath"
Write-Output $finalPath
