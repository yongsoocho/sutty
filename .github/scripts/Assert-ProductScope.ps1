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
    Get-Item -LiteralPath (Join-Path $RepositoryRoot 'README.md')
    Get-Item -LiteralPath (Join-Path $RepositoryRoot 'SECURITY.md')
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'docs') -Filter '*.md' -File -Recurse
)

$uiSources = @(
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src\sutty.UI') -File -Recurse |
        Where-Object { $_.Name -like '*.xaml' -or $_.Name -like '*.xaml.cs' }
)

Add-PatternViolations -Files $documentation -Pattern '(?i)\b(?:putty|filezilla|termius|mobaxterm|securecrt)\b' -Rule 'product-facing documentation must describe Sutty without competitor branding'

Add-PatternViolations -Files @($documentation + $uiSources) -Pattern '(?i)\benterprise\b|기업\s*(?:모드|정책|배포|릴리스)|기업용' -Rule 'organization-scale positioning is outside the current product scope'

Add-PatternViolations -Files $uiSources -Pattern '(?i)coming\s+soon|준비\s*중' -Rule 'production UI must not expose placeholder features'

if ($violations.Count -gt 0) {
    $violations | Sort-Object | ForEach-Object { Write-Error $_ }
    throw "Product-scope validation failed with $($violations.Count) violation(s)."
}

Write-Host "Product-scope validation passed for $($documentation.Count) documentation files and $($uiSources.Count) UI files."
