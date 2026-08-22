param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Tag,

    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,

    [string]$Commit,

    [string]$X64PayloadPath,

    [string]$Arm64PayloadPath,

    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$violations = [System.Collections.Generic.List[string]]::new()
$alphaTagPattern = 'v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+'
$tagIdentity = [regex]::Match(
    $Tag,
    '^v(?<version>[0-9]+\.[0-9]+\.[0-9]+)-alpha\.(?<alpha>[0-9]+)$')

function Add-Violation {
    param([string]$Message)

    $violations.Add($Message)
}

function Get-RequiredText {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Violation "$Description is missing: $Path"
        return $null
    }

    return [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).Path)
}

function Test-BuildInfo {
    param(
        [string]$Content,
        [string]$Architecture,
        [string]$Description
    )

    $requiredLines = @(
        "Sutty $Tag"
        "Commit: $Commit"
        'Channel: Alpha'
        'Signing: unsigned ZIP evaluation build'
        'Minimum OS: Windows 11 24H2'
        "Architecture: $Architecture"
    )
    $lines = @($Content -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    $schemaMatches = $lines.Count -eq $requiredLines.Count
    if ($schemaMatches) {
        for ($index = 0; $index -lt $requiredLines.Count; $index++) {
            if ($lines[$index] -cne $requiredLines[$index]) {
                $schemaMatches = $false
                break
            }
        }
    }
    if (-not $schemaMatches) {
        Add-Violation "$Description must contain exactly the six release identity lines in the required order."
    }

    foreach ($match in [regex]::Matches($Content, $alphaTagPattern)) {
        if ($match.Value -cne $Tag) {
            Add-Violation "$Description contains stale Alpha tag $($match.Value)."
        }
    }
}

function Get-ZipEntryText {
    param(
        [System.IO.Compression.ZipArchive]$Archive,
        [string]$EntryPath,
        [string]$Description
    )

    $entries = @($Archive.Entries | Where-Object {
        $_.FullName.Replace('\', '/') -ceq $EntryPath
    })
    if ($entries.Count -ne 1) {
        Add-Violation "$Description must contain exactly one root $EntryPath entry; found $($entries.Count)."
        return $null
    }

    $stream = $entries[0].Open()
    try {
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.UTF8Encoding]::new($false),
            $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

if (-not $tagIdentity.Success) {
    Add-Violation "unsupported Alpha tag: $Tag"
}

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    Add-Violation "repository root is missing: $RepositoryRoot"
}
else {
    $RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
}

$propsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
$propsText = Get-RequiredText -Path $propsPath -Description 'source version file'
if ($null -ne $propsText) {
    try {
        [xml]$versionDocument = $propsText
        $prefixNodes = @($versionDocument.SelectNodes('/Project/PropertyGroup/VersionPrefix'))
        $suffixNodes = @($versionDocument.SelectNodes('/Project/PropertyGroup/VersionSuffix'))
        $versionNodes = @($versionDocument.SelectNodes('/Project/PropertyGroup/Version'))
        $informationalVersionNodes = @($versionDocument.SelectNodes('/Project/PropertyGroup/InformationalVersion'))
        if ($prefixNodes.Count -ne 1 -or $suffixNodes.Count -ne 1 -or
            $versionNodes.Count -ne 1 -or $informationalVersionNodes.Count -ne 1) {
            Add-Violation 'Directory.Build.props must contain exactly one VersionPrefix, VersionSuffix, Version, and InformationalVersion.'
        }
        else {
            $expectedTag = "v$($prefixNodes[0].InnerText.Trim())-$($suffixNodes[0].InnerText.Trim())"
            if ($Tag -cne $expectedTag) {
                Add-Violation "tag $Tag does not match source version $expectedTag."
            }
            if ($versionNodes[0].InnerText.Trim() -cne '$(VersionPrefix)-$(VersionSuffix)') {
                Add-Violation 'Directory.Build.props Version must be exactly $(VersionPrefix)-$(VersionSuffix).'
            }
            if ($informationalVersionNodes[0].InnerText.Trim() -cne '$(Version)') {
                Add-Violation 'Directory.Build.props InformationalVersion must be exactly $(Version).'
            }
        }
    }
    catch {
        Add-Violation "Directory.Build.props is not valid XML: $($_.Exception.Message)"
    }
}

$readmePath = Join-Path $RepositoryRoot 'README.md'
$readmeText = Get-RequiredText -Path $readmePath -Description 'README'
if ($null -ne $readmeText) {
    $candidateLines = @([regex]::Matches(
        $readmeText,
        '^>\s+\*\*Latest published / 최신 공개본:\*\*.*\*\*Current candidate / 현재 후보:\*\*.*$',
        [System.Text.RegularExpressions.RegexOptions]::Multiline))
    if ($candidateLines.Count -ne 1) {
        Add-Violation "README must contain exactly one latest-published/current-candidate line; found $($candidateLines.Count)."
    }
    elseif ($tagIdentity.Success) {
        $candidateLine = $candidateLines[0].Value
        $alphaNumber = [int]$tagIdentity.Groups['alpha'].Value
        if ($alphaNumber -lt 2) {
            Add-Violation 'README candidate contract requires a previously published Alpha tag.'
        }
        else {
            $previousTag = "v$($tagIdentity.Groups['version'].Value)-alpha.$($alphaNumber - 1)"
            $expectedLatestUrl = "https://github.com/yongsoocho/sutty/releases/tag/$previousTag"
            $expectedCandidateTarget = "docs/releases/$Tag.md"
            $lineTags = @([regex]::Matches($candidateLine, $alphaTagPattern) | ForEach-Object Value)
            if ($lineTags.Count -ne 4 -or
                @($lineTags | Where-Object { $_ -ceq $previousTag }).Count -ne 2 -or
                @($lineTags | Where-Object { $_ -ceq $Tag }).Count -ne 2) {
                Add-Violation "README candidate line must identify exactly latest $previousTag and candidate $Tag."
            }
            if (-not $candidateLine.Contains($expectedLatestUrl, [StringComparison]::Ordinal) -or
                -not $candidateLine.Contains($expectedCandidateTarget, [StringComparison]::Ordinal)) {
                Add-Violation 'README latest-published/current-candidate links do not match the release contract.'
            }
        }
    }
}

$releaseNotesPath = Join-Path $RepositoryRoot "docs\releases\$Tag.md"
$releaseNotesText = Get-RequiredText -Path $releaseNotesPath -Description 'release notes'
$expectedArchives = @(
    "Sutty-$Tag-win-x64.zip"
    "Sutty-$Tag-win-arm64.zip"
)
if ($null -ne $releaseNotesText) {
    foreach ($archiveName in $expectedArchives) {
        if (-not $releaseNotesText.Contains($archiveName, [StringComparison]::Ordinal)) {
            Add-Violation "release notes do not list $archiveName."
        }
    }
    foreach ($provenanceAsset in @(
        'SHA256SUMS.txt',
        'CANDIDATE-MANIFEST.json',
        'RELEASE-ATTESTATION.json')) {
        if (-not $releaseNotesText.Contains($provenanceAsset, [StringComparison]::Ordinal)) {
            Add-Violation "release notes do not list $provenanceAsset."
        }
    }

    $listedArchives = @([regex]::Matches(
        $releaseNotesText,
        "Sutty-$alphaTagPattern-win-(?:x64|arm64)\.zip"))
    foreach ($listedArchive in $listedArchives) {
        if ($listedArchive.Value -cnotin $expectedArchives) {
            Add-Violation "release notes list an archive for a different release: $($listedArchive.Value)."
        }
    }
}

$installGuidePath = Join-Path $RepositoryRoot 'docs\ALPHA_INSTALL.md'
$installGuideText = Get-RequiredText -Path $installGuidePath -Description 'Alpha installation guide'
if ($null -ne $installGuideText) {
    $staleTags = @([regex]::Matches($installGuideText, $alphaTagPattern))
    foreach ($staleTag in $staleTags) {
        Add-Violation "Alpha installation guide must be version-neutral; found $($staleTag.Value)."
    }

    foreach ($neutralArchivePattern in @('Sutty-*-win-x64.zip', 'Sutty-*-win-arm64.zip')) {
        if (-not $installGuideText.Contains($neutralArchivePattern, [StringComparison]::Ordinal)) {
            Add-Violation "Alpha installation guide does not contain version-neutral archive pattern $neutralArchivePattern."
        }
    }
}

$artifactArguments = @($Commit, $X64PayloadPath, $Arm64PayloadPath, $PackageDirectory)
$artifactArgumentCount = @($artifactArguments | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
if ($artifactArgumentCount -ne 0 -and $artifactArgumentCount -ne $artifactArguments.Count) {
    Add-Violation 'Commit, X64PayloadPath, Arm64PayloadPath, and PackageDirectory must be supplied together.'
}

if ($artifactArgumentCount -eq $artifactArguments.Count) {
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') {
        Add-Violation 'Commit must be a 40-character lowercase Git object ID.'
    }

    $payloads = @(
        @{ Architecture = 'x64'; Path = $X64PayloadPath; Archive = $expectedArchives[0] }
        @{ Architecture = 'arm64'; Path = $Arm64PayloadPath; Archive = $expectedArchives[1] }
    )

    foreach ($payload in $payloads) {
        $payloadPath = $payload.Path
        $description = "$($payload.Architecture) payload"
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Container)) {
            Add-Violation "$description is missing: $payloadPath"
            continue
        }

        # ZIP paths are normalized to '/', then compared with ordinal case sensitivity.
        # Directory entries are ignored; every payload file must have one exact archive entry.
        $resolvedPayloadPath = (Resolve-Path -LiteralPath $payloadPath).Path
        $payloadInventory = [System.Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::Ordinal)
        foreach ($payloadFile in Get-ChildItem -LiteralPath $resolvedPayloadPath -File -Recurse -Force) {
            $relativePath = [System.IO.Path]::GetRelativePath(
                $resolvedPayloadPath,
                $payloadFile.FullName).Replace('\', '/')
            $payloadInventory.Add($relativePath, [pscustomobject]@{
                Length = [long]$payloadFile.Length
                Sha256 = (Get-FileHash -LiteralPath $payloadFile.FullName -Algorithm SHA256).
                    Hash.ToLowerInvariant()
            })
        }
        $payload.Inventory = $payloadInventory

        $buildInfoText = Get-RequiredText `
            -Path (Join-Path $payloadPath 'BUILDINFO.txt') `
            -Description "$description BUILDINFO.txt"
        if ($null -ne $buildInfoText) {
            Test-BuildInfo `
                -Content $buildInfoText `
                -Architecture $payload.Architecture `
                -Description "$description BUILDINFO.txt"
        }

        $payloadGuideText = Get-RequiredText `
            -Path (Join-Path $payloadPath 'ALPHA_INSTALL.md') `
            -Description "$description ALPHA_INSTALL.md"
        if ($null -ne $payloadGuideText -and $null -ne $installGuideText -and
            $payloadGuideText -cne $installGuideText) {
            Add-Violation "$description ALPHA_INSTALL.md does not match the version-neutral source guide."
        }
    }

    if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
        Add-Violation "package directory is missing: $PackageDirectory"
    }
    else {
        $expectedPackageFiles = @($expectedArchives + 'SHA256SUMS.txt') | Sort-Object
        $actualPackageFiles = @(
            Get-ChildItem -LiteralPath $PackageDirectory -File |
                ForEach-Object { $_.Name } |
                Sort-Object
        )
        if ([string]::Join('|', $actualPackageFiles) -cne [string]::Join('|', $expectedPackageFiles)) {
            Add-Violation "package directory files do not match the release contract. Expected: $($expectedPackageFiles -join ', '). Actual: $($actualPackageFiles -join ', ')."
        }

        $checksumPath = Join-Path $PackageDirectory 'SHA256SUMS.txt'
        $checksumText = Get-RequiredText -Path $checksumPath -Description 'SHA256SUMS.txt'
        if ($null -ne $checksumText) {
            $checksumLines = @($checksumText -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($checksumLines.Count -ne $expectedArchives.Count) {
                Add-Violation "SHA256SUMS.txt must contain exactly $($expectedArchives.Count) non-empty entries; found $($checksumLines.Count)."
            }

            $checksumEntries = @{}
            foreach ($line in $checksumLines) {
                if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^\\/]+)$') {
                    Add-Violation "invalid SHA256SUMS.txt entry: $line"
                    continue
                }
                if ($checksumEntries.ContainsKey($Matches.name)) {
                    Add-Violation "duplicate SHA256SUMS.txt entry: $($Matches.name)"
                    continue
                }
                $checksumEntries[$Matches.name] = $Matches.hash
            }

            foreach ($archiveName in $expectedArchives) {
                $archivePath = Join-Path $PackageDirectory $archiveName
                if (-not $checksumEntries.ContainsKey($archiveName)) {
                    Add-Violation "SHA256SUMS.txt does not contain $archiveName."
                }
                elseif (Test-Path -LiteralPath $archivePath -PathType Leaf) {
                    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
                    if ($actualHash -cne $checksumEntries[$archiveName]) {
                        Add-Violation "SHA-256 mismatch for $archiveName."
                    }
                }
            }
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        foreach ($payload in $payloads) {
            $archivePath = Join-Path $PackageDirectory $payload.Archive
            if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
                Add-Violation "release archive is missing: $archivePath"
                continue
            }

            try {
                $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $archivePath).Path)
                try {
                    $archiveInventory = [System.Collections.Generic.Dictionary[string, object]]::new(
                        [StringComparer]::Ordinal)
                    foreach ($entry in $archive.Entries) {
                        $entryPath = $entry.FullName.Replace('\', '/')
                        if ($entryPath.EndsWith('/', [StringComparison]::Ordinal)) {
                            continue
                        }

                        $segments = @($entryPath.Split('/'))
                        if ($entryPath.StartsWith('/', [StringComparison]::Ordinal) -or
                            @($segments | Where-Object {
                                [string]::IsNullOrEmpty($_) -or $_ -eq '.' -or $_ -eq '..'
                            }).Count -gt 0) {
                            Add-Violation "$($payload.Archive) contains an invalid entry path: $entryPath"
                            continue
                        }
                        if ($archiveInventory.ContainsKey($entryPath)) {
                            Add-Violation "$($payload.Archive) contains a duplicate file entry: $entryPath"
                            continue
                        }
                        $archiveInventory.Add($entryPath, [pscustomobject]@{
                            Length = [long]$entry.Length
                            Entry = $entry
                        })
                    }

                    $rootExecutableCount = @($archiveInventory.Keys | Where-Object {
                        $_ -ceq 'sutty.UI.exe'
                    }).Count
                    if ($rootExecutableCount -ne 1) {
                        Add-Violation "$($payload.Archive) must contain exactly one root sutty.UI.exe entry; found $rootExecutableCount."
                    }

                    if ($payload.ContainsKey('Inventory')) {
                        $payloadInventory = $payload.Inventory
                        if ($archiveInventory.Count -ne $payloadInventory.Count) {
                            Add-Violation "$($payload.Archive) file count $($archiveInventory.Count) does not match payload file count $($payloadInventory.Count)."
                        }
                        foreach ($payloadEntry in $payloadInventory.GetEnumerator()) {
                            if (-not $archiveInventory.ContainsKey($payloadEntry.Key)) {
                                Add-Violation "$($payload.Archive) is missing payload file: $($payloadEntry.Key)"
                            }
                            elseif ($archiveInventory[$payloadEntry.Key].Length -ne $payloadEntry.Value.Length) {
                                Add-Violation "$($payload.Archive) entry length does not match payload file $($payloadEntry.Key)."
                            }
                            else {
                                $entryStream = $archiveInventory[$payloadEntry.Key].Entry.Open()
                                try {
                                    $archiveHashBytes = [System.Security.Cryptography.SHA256]::HashData($entryStream)
                                    $archiveHash = [Convert]::ToHexString($archiveHashBytes).ToLowerInvariant()
                                }
                                finally {
                                    $entryStream.Dispose()
                                }
                                if ($archiveHash -cne $payloadEntry.Value.Sha256) {
                                    Add-Violation "$($payload.Archive) entry content does not match payload file $($payloadEntry.Key)."
                                }
                            }
                        }
                        foreach ($archiveEntry in $archiveInventory.GetEnumerator()) {
                            if (-not $payloadInventory.ContainsKey($archiveEntry.Key)) {
                                Add-Violation "$($payload.Archive) contains a file not present in the payload: $($archiveEntry.Key)"
                            }
                        }
                    }

                    $archiveBuildInfo = Get-ZipEntryText `
                        -Archive $archive `
                        -EntryPath 'BUILDINFO.txt' `
                        -Description $payload.Archive
                    if ($null -ne $archiveBuildInfo) {
                        Test-BuildInfo `
                            -Content $archiveBuildInfo `
                            -Architecture $payload.Architecture `
                            -Description "$($payload.Archive) BUILDINFO.txt"
                    }

                    $archiveGuide = Get-ZipEntryText `
                        -Archive $archive `
                        -EntryPath 'ALPHA_INSTALL.md' `
                        -Description $payload.Archive
                    if ($null -ne $archiveGuide -and $null -ne $installGuideText -and
                        $archiveGuide -cne $installGuideText) {
                        Add-Violation "$($payload.Archive) ALPHA_INSTALL.md does not match the version-neutral source guide."
                    }
                }
                finally {
                    $archive.Dispose()
                }
            }
            catch {
                Add-Violation "$($payload.Archive) is not a readable ZIP archive: $($_.Exception.Message)"
            }
        }
    }
}

if ($violations.Count -gt 0) {
    $details = ($violations | Sort-Object -Unique | ForEach-Object { " - $_" }) -join [Environment]::NewLine
    throw "Release metadata validation failed with $($violations.Count) violation(s):$([Environment]::NewLine)$details"
}

if ($artifactArgumentCount -eq $artifactArguments.Count) {
    Write-Host "Release metadata validation passed for $Tag, both payloads, and immutable package assets."
}
else {
    Write-Host "Source release metadata validation passed for $Tag."
}
