param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactPath
)

$ErrorActionPreference = 'Stop'
$resolvedArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath).Path
$files = @(Get-ChildItem -LiteralPath $resolvedArtifactPath -Recurse -File)

if ($files.Count -eq 0) {
    throw "Release artifact is empty: $resolvedArtifactPath"
}

$namePattern = '(?i)(mock|demo|seed)'
$forbiddenFileNames = @(
    'settings.json',
    'workspace.json',
    'sutty.db',
    'known-hosts.json',
    'sftp-transfer-checkpoints.json',
    'sftp-transfer-queue.json',
    'vault.json',
    'vault.key',
    'crash.log'
)
$forbiddenExtensions = @(
    '.bak', '.key', '.log', '.pem', '.pfx', '.p12', '.ppk', '.sqlite', '.tmp', '.user'
)
$contentPatterns = [ordered]@{
    'Mock/Demo/Seed marker' = '(?i)(mock|demo|seed)'
    '10/8 test host IP literal' = '(?<![0-9])10(?:\.[0-9]{1,3}){2}\.(?:[1-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-4])(?![0-9])'
    'TEST-NET host IP literal' = '(?<![0-9])(?:192\.0\.2|198\.51\.100|203\.0\.113)\.(?:[1-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-4])(?![0-9])'
}
$textExtensions = @('.config', '.db', '.json', '.sql', '.txt', '.xml', '.yaml', '.yml')
$firstPartyBinaryExtensions = @('.dll', '.exe', '.pdb', '.winmd')
$violations = [System.Collections.Generic.List[string]]::new()

foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($resolvedArtifactPath.Length).TrimStart('\', '/')

    if ($file.Name -match $namePattern) {
        $violations.Add("forbidden file name: $relativePath")
    }
    if ($forbiddenFileNames -contains $file.Name.ToLowerInvariant()) {
        $violations.Add("local user data file: $relativePath")
    }
    if ($forbiddenExtensions -contains $file.Extension.ToLowerInvariant()) {
        $violations.Add("forbidden release extension: $relativePath")
    }
    if ($file.Extension.Equals('.pdb', [System.StringComparison]::OrdinalIgnoreCase)) {
        $violations.Add("debug symbols are not allowed in public Alpha archives: $relativePath")
    }

    $isTextArtifact = $textExtensions -contains $file.Extension.ToLowerInvariant()
    $isFirstPartyBinary =
        $file.BaseName.StartsWith('sutty', [System.StringComparison]::OrdinalIgnoreCase) -and
        $firstPartyBinaryExtensions -contains $file.Extension.ToLowerInvariant()

    if (-not ($isTextArtifact -or $isFirstPartyBinary)) {
        continue
    }

    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $asciiContent = [System.Text.Encoding]::ASCII.GetString($bytes)
    $unicodeContent = [System.Text.Encoding]::Unicode.GetString($bytes)

    foreach ($entry in $contentPatterns.GetEnumerator()) {
        if ($asciiContent -match $entry.Value -or $unicodeContent -match $entry.Value) {
            $violations.Add("$($entry.Key): $relativePath")
        }
    }
}

if ($violations.Count -gt 0) {
    $details = ($violations | Sort-Object -Unique) -join [Environment]::NewLine
    throw "Release artifact contains forbidden development data:$([Environment]::NewLine)$details"
}

Write-Host "Release artifact policy passed for $($files.Count) files."
