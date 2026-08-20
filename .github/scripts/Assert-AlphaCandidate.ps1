param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ManifestPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Repository,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Tag,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Commit,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateRunId,

    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$CandidateRunAttempt,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ArtifactName,

    [switch]$WriteManifest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$schemaVersion = 1
$workflowFile = '.github/workflows/alpha-candidate.yml'
$alphaTagPattern = '^v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+$'

function Read-StrictUtf8 {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }

    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "$Description must be UTF-8 without a byte-order mark."
    }

    try {
        return [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    }
    catch {
        throw "$Description is not valid UTF-8."
    }
}

function Assert-ExactJsonProperties {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)]
        [string[]]$Expected,
        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "$Description must be a JSON object."
    }

    $properties = @($Element.EnumerateObject())
    $actualNames = @($properties | ForEach-Object { $_.Name })
    if (($actualNames | Select-Object -Unique).Count -ne $actualNames.Count) {
        throw "$Description contains duplicate property names."
    }

    if ([string]::Join('|', ($actualNames | Sort-Object)) -cne
        [string]::Join('|', ($Expected | Sort-Object))) {
        throw "$Description properties do not match the candidate schema."
    }
}

function Get-RequiredJsonString {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Description
    )

    $value = $Object.GetProperty($Name)
    if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
        throw "$Description.$Name must be a JSON string."
    }
    return $value.GetString()
}

if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must be an exact GitHub owner/name slug.'
}
if ($Tag -cnotmatch $alphaTagPattern) {
    throw "Unsupported Alpha tag: $Tag"
}
if ($Commit -cnotmatch '^[0-9a-f]{40}$') {
    throw 'Commit must be a 40-character lowercase Git object ID.'
}
if ($CandidateRunId -cnotmatch '^[1-9][0-9]*$') {
    throw 'CandidateRunId must be a positive decimal workflow run ID.'
}

$expectedArtifactName = "sutty-$Tag-candidate-$CandidateRunId-attempt-$CandidateRunAttempt"
if ($ArtifactName -cne $expectedArtifactName) {
    throw "ArtifactName must be exactly $expectedArtifactName."
}

if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Candidate package directory is missing: $PackageDirectory"
}
$resolvedPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
if ((Get-Item -LiteralPath $resolvedPackageDirectory -Force).Attributes.HasFlag(
        [System.IO.FileAttributes]::ReparsePoint)) {
    throw 'Candidate package directory must not be a reparse point.'
}

$archiveNames = @(
    "Sutty-$Tag-win-x64.zip"
    "Sutty-$Tag-win-arm64.zip"
) | Sort-Object
$expectedPackageNames = @($archiveNames + 'SHA256SUMS.txt') | Sort-Object
$packageItems = @(Get-ChildItem -LiteralPath $resolvedPackageDirectory -Force)
if (@($packageItems | Where-Object { -not $_.PSIsContainer }).Count -ne $packageItems.Count) {
    throw 'Candidate package directory must contain files only.'
}
if (@($packageItems | Where-Object {
            $_.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)
        }).Count -gt 0) {
    throw 'Candidate package files must not be reparse points.'
}
$actualPackageNames = @($packageItems | ForEach-Object { $_.Name } | Sort-Object)
if ([string]::Join('|', $actualPackageNames) -cne [string]::Join('|', $expectedPackageNames)) {
    throw "Candidate package files do not match the contract. Expected: $($expectedPackageNames -join ', '). Actual: $($actualPackageNames -join ', ')."
}

$checksumPath = Join-Path $resolvedPackageDirectory 'SHA256SUMS.txt'
$checksumText = Read-StrictUtf8 -Path $checksumPath -Description 'SHA256SUMS.txt'
$checksumLines = @($checksumText -split '\r?\n' | Where-Object { $_.Length -gt 0 })
if ($checksumLines.Count -ne $archiveNames.Count) {
    throw "SHA256SUMS.txt must contain exactly $($archiveNames.Count) entries."
}

$packageRecords = [System.Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $archiveNames.Count; $index++) {
    $archiveName = $archiveNames[$index]
    $archivePath = Join-Path $resolvedPackageDirectory $archiveName
    if ($checksumLines[$index] -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^\\/]+)$' -or
        $Matches.name -cne $archiveName) {
        throw "SHA256SUMS.txt entry $($index + 1) must identify $archiveName with a lowercase SHA-256 digest."
    }

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Matches.hash -cne $actualHash) {
        throw "SHA-256 mismatch for $archiveName."
    }
    $archiveItem = Get-Item -LiteralPath $archivePath
    $packageRecords.Add([ordered]@{
        name = $archiveName
        sha256 = $actualHash
        size_bytes = [long]$archiveItem.Length
    })
}

$checksumItem = Get-Item -LiteralPath $checksumPath
$packageRecords.Add([ordered]@{
    name = 'SHA256SUMS.txt'
    sha256 = (Get-FileHash -LiteralPath $checksumPath -Algorithm SHA256).Hash.ToLowerInvariant()
    size_bytes = [long]$checksumItem.Length
})

if ($WriteManifest) {
    if (Test-Path -LiteralPath $ManifestPath) {
        throw "Candidate manifest already exists: $ManifestPath"
    }
    $manifestParent = Split-Path -Parent ([System.IO.Path]::GetFullPath($ManifestPath))
    if (-not (Test-Path -LiteralPath $manifestParent -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($manifestParent) | Out-Null
    }

    $manifest = [ordered]@{
        schema_version = $schemaVersion
        repository = $Repository
        tag = $Tag
        commit = $Commit
        source_ref = 'refs/heads/main'
        candidate_run_id = $CandidateRunId
        candidate_run_attempt = $CandidateRunAttempt
        artifact_name = $ArtifactName
        workflow_file = $workflowFile
        files = @($packageRecords)
    }
    $json = $manifest | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText(
        [System.IO.Path]::GetFullPath($ManifestPath),
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

$resolvedManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
$candidateRoot = Split-Path -Parent $resolvedManifestPath
if ([System.IO.Path]::GetFileName($resolvedManifestPath) -cne 'CANDIDATE-MANIFEST.json' -or
    [System.IO.Path]::GetFileName($resolvedPackageDirectory) -cne 'packages' -or
    (Split-Path -Parent $resolvedPackageDirectory) -cne $candidateRoot) {
    throw 'Candidate layout must be CANDIDATE-MANIFEST.json plus a sibling packages directory.'
}
if ((Get-Item -LiteralPath $candidateRoot -Force).Attributes.HasFlag(
        [System.IO.FileAttributes]::ReparsePoint)) {
    throw 'Candidate root must not be a reparse point.'
}
$candidateItems = @(Get-ChildItem -LiteralPath $candidateRoot -Force)
$candidateItemNames = @($candidateItems | ForEach-Object { $_.Name } | Sort-Object)
if ([string]::Join('|', $candidateItemNames) -cne 'CANDIDATE-MANIFEST.json|packages' -or
    @($candidateItems | Where-Object {
            $_.Name -ceq 'packages' -and -not $_.PSIsContainer
        }).Count -ne 0 -or
    @($candidateItems | Where-Object {
            $_.Name -ceq 'CANDIDATE-MANIFEST.json' -and $_.PSIsContainer
        }).Count -ne 0 -or
    @($candidateItems | Where-Object {
            $_.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)
        }).Count -gt 0) {
    throw 'Candidate root contains an unexpected item or reparse point.'
}

$manifestText = Read-StrictUtf8 -Path $ManifestPath -Description 'candidate manifest'
try {
    $jsonOptions = [System.Text.Json.JsonDocumentOptions]::new()
    $jsonOptions.AllowTrailingCommas = $false
    $jsonOptions.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    $document = [System.Text.Json.JsonDocument]::Parse($manifestText, $jsonOptions)
}
catch {
    throw "Candidate manifest is not strict JSON: $($_.Exception.Message)"
}

try {
    $root = $document.RootElement
    Assert-ExactJsonProperties -Element $root -Expected @(
        'schema_version'
        'repository'
        'tag'
        'commit'
        'source_ref'
        'candidate_run_id'
        'candidate_run_attempt'
        'artifact_name'
        'workflow_file'
        'files'
    ) -Description 'candidate manifest'

    $schemaElement = $root.GetProperty('schema_version')
    $schemaValue = 0
    if ($schemaElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $schemaElement.TryGetInt32([ref]$schemaValue) -or
        $schemaValue -ne $schemaVersion) {
        throw "candidate manifest.schema_version must be the integer $schemaVersion."
    }

    $expectedStrings = [ordered]@{
        repository = $Repository
        tag = $Tag
        commit = $Commit
        source_ref = 'refs/heads/main'
        candidate_run_id = $CandidateRunId
        artifact_name = $ArtifactName
        workflow_file = $workflowFile
    }
    foreach ($entry in $expectedStrings.GetEnumerator()) {
        $actual = Get-RequiredJsonString -Object $root -Name $entry.Key -Description 'candidate manifest'
        if ($actual -cne $entry.Value) {
            throw "candidate manifest.$($entry.Key) does not match the expected identity."
        }
    }

    $attemptElement = $root.GetProperty('candidate_run_attempt')
    $attemptValue = 0
    if ($attemptElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $attemptElement.TryGetInt32([ref]$attemptValue) -or
        $attemptValue -ne $CandidateRunAttempt) {
        throw 'candidate manifest.candidate_run_attempt does not match the expected identity.'
    }

    $filesElement = $root.GetProperty('files')
    if ($filesElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
        throw 'candidate manifest.files must be a JSON array.'
    }
    $manifestFiles = @($filesElement.EnumerateArray())
    if ($manifestFiles.Count -ne $packageRecords.Count) {
        throw "candidate manifest.files must contain exactly $($packageRecords.Count) records."
    }

    for ($index = 0; $index -lt $packageRecords.Count; $index++) {
        $manifestFile = $manifestFiles[$index]
        $expectedFile = $packageRecords[$index]
        Assert-ExactJsonProperties -Element $manifestFile -Expected @(
            'name'
            'sha256'
            'size_bytes'
        ) -Description "candidate manifest.files[$index]"

        foreach ($field in @('name', 'sha256')) {
            $actual = Get-RequiredJsonString `
                -Object $manifestFile `
                -Name $field `
                -Description "candidate manifest.files[$index]"
            if ($actual -cne $expectedFile[$field]) {
                throw "candidate manifest.files[$index].$field does not match the exact package file."
            }
        }

        $sizeElement = $manifestFile.GetProperty('size_bytes')
        $sizeValue = [long]0
        if ($sizeElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            -not $sizeElement.TryGetInt64([ref]$sizeValue) -or
            $sizeValue -ne $expectedFile.size_bytes) {
            throw "candidate manifest.files[$index].size_bytes does not match the exact package file."
        }
    }
}
finally {
    $document.Dispose()
}

Write-Host "Alpha candidate contract passed for $Tag at $Commit (run $CandidateRunId, attempt $CandidateRunAttempt)."
