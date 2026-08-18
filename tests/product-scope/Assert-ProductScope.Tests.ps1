param(
    [string]$ScopeScript = (Resolve-Path (Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-ProductScope.ps1')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = Join-Path $temporaryBase "sutty-product-scope-tests-$([Guid]::NewGuid().ToString('N'))"
$allowedRoot = Join-Path $PSScriptRoot 'allowed'
$rejectedRoot = Join-Path $PSScriptRoot 'rejected'

function New-FixtureRepository {
    param([string]$Name)

    $root = Join-Path $scratch $Name
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Get-ChildItem -LiteralPath $allowedRoot -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $root -Recurse -Force
    }
    return $root
}

function Test-ScopeValidation {
    param([string]$Root)

    try {
        & $ScopeScript -RepositoryRoot $Root *> $null
        return $true
    }
    catch {
        return $false
    }
}

function Assert-Result {
    param(
        [bool]$Condition,
        [string]$Name
    )

    if (-not $Condition) {
        throw "Product-scope self-test failed: $Name."
    }
}

try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    $allowed = New-FixtureRepository 'allowed'
    Assert-Result (Test-ScopeValidation $allowed) 'neutral docs and allowlisted migration references pass'

    $rejectedCases = @(
        @{ Name = 'competitor-branding'; Fixture = 'competitor-branding.md'; Target = 'README.md' },
        @{ Name = 'overclaim-en'; Fixture = 'overclaim-en.md'; Target = 'README.md' },
        @{ Name = 'overclaim-ko'; Fixture = 'overclaim-ko.md'; Target = 'README.md' },
        @{ Name = 'organization-positioning'; Fixture = 'organization-positioning.md'; Target = 'README.md' },
        @{ Name = 'organization-positioning-ko'; Fixture = 'organization-positioning-ko.md'; Target = 'README.md' },
        @{ Name = 'placeholder-ui'; Fixture = 'placeholder.xaml'; Target = 'src\sutty.UI\Views\Shell.xaml' },
        @{ Name = 'placeholder-ui-ko'; Fixture = 'placeholder-ko.xaml'; Target = 'src\sutty.UI\Views\Shell.xaml' },
        @{ Name = 'legacy-docx'; Fixture = 'Sutty_Windows_Enterprise_Product_Plan.docx'; Target = 'docs\Sutty_Windows_Enterprise_Product_Plan.docx' }
    )

    foreach ($case in $rejectedCases) {
        $root = New-FixtureRepository $case.Name
        Copy-Item `
            -LiteralPath (Join-Path $rejectedRoot $case.Fixture) `
            -Destination (Join-Path $root $case.Target) `
            -Force
        Assert-Result (-not (Test-ScopeValidation $root)) "$($case.Name) is rejected"
    }

    Write-Host "Product-scope guard self-tests passed ($($rejectedCases.Count + 1) cases)."
}
finally {
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ((Test-Path -LiteralPath $resolvedScratch) -and
        $resolvedScratch.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith(
            'sutty-product-scope-tests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
