param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$violations = [System.Collections.Generic.List[string]]::new()

function Add-PatternViolations {
    param(
        [System.IO.FileInfo[]]$Files,
        [string]$Pattern,
        [string]$Rule
    )

    foreach ($file in $Files) {
        foreach ($match in Select-String -LiteralPath $file.FullName -Pattern $Pattern -AllMatches) {
            $relative = [System.IO.Path]::GetRelativePath($RepositoryRoot, $file.FullName)
            $violations.Add("${relative}:$($match.LineNumber): $Rule")
        }
    }
}

$documentation = @(
    Get-ChildItem -LiteralPath $RepositoryRoot -Filter '*.md' -File
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'docs') -Filter '*.md' -File -Recurse
)

$competitorReferenceAllowlist = @(
    'docs\MIGRATION.md'
    'docs\IMPORT_COMPATIBILITY.md'
)
$vendorNeutralDocumentation = @(
    $documentation | Where-Object {
        $relative = [System.IO.Path]::GetRelativePath($RepositoryRoot, $_.FullName)
        $relative -notin $competitorReferenceAllowlist
    }
)

$uiSources = @(
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src\sutty.UI') -File -Recurse |
        Where-Object { $_.Name -like '*.xaml' -or $_.Name -like '*.xaml.cs' }
)

Add-PatternViolations -Files $vendorNeutralDocumentation -Pattern '(?i)\b(?:putty|filezilla|termius|mobaxterm|securecrt)\b' -Rule 'competitor names are allowed only in migration or import-compatibility documentation'

Add-PatternViolations -Files $documentation -Pattern '(?i)\b(?:complete|full)\s+(?:replacement|parity)\b|\breplaces?\s+every\s+feature\b|완벽\s*대체|전체\s*기능\s*동등' -Rule 'product documentation must not claim complete replacement or feature parity'

Add-PatternViolations -Files @($documentation + $uiSources) -Pattern '(?i)\benterprise\b|기업\s*(?:모드|정책|배포|릴리스)|기업용' -Rule 'organization-scale positioning is outside the current product scope'

Add-PatternViolations -Files $uiSources -Pattern '(?i)coming\s+soon|준비\s*중' -Rule 'production UI must not expose placeholder features'

$legacyPlanningDocuments = @(
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'docs') -Filter '*.docx' -File -Recurse |
        Where-Object {
            $_.Name -match '(?i)enterprise.*product.*plan' -or
            $_.Name -match '(?i)multi.*ssh.*sftp.*product.*plan.*v2'
        }
)
foreach ($file in $legacyPlanningDocuments) {
    $relative = [System.IO.Path]::GetRelativePath($RepositoryRoot, $file.FullName)
    $violations.Add("${relative}:1: superseded binary product plans must not remain in the active documentation tree")
}

if ($violations.Count -gt 0) {
    $violations | Sort-Object | ForEach-Object { Write-Error $_ }
    throw "Product-scope validation failed with $($violations.Count) violation(s)."
}

Write-Host "Product-scope validation passed for $($documentation.Count) documentation files and $($uiSources.Count) UI files."
