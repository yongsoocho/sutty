param(
    [string]$Validator = (Resolve-Path (
        Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-EvidenceHistory.ps1')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = Join-Path $temporaryBase "sutty-evidence-history-tests-$([Guid]::NewGuid().ToString('N'))"
$caseCount = 0

function Set-Utf8Text {
    param([string]$Path, [string]$Content)

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-FixtureGit {
    param([string]$Repository, [string[]]$Arguments)

    $output = @(& git -C $Repository @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture Git command failed: $($output -join [Environment]::NewLine)"
    }
    $global:LASTEXITCODE = 0
    return @($output | ForEach-Object { [string]$_ })
}

function New-HistoryFixture {
    param([string]$Name)

    $repository = Join-Path $scratch $Name
    New-Item -ItemType Directory -Path $repository -Force | Out-Null
    Invoke-FixtureGit -Repository $repository -Arguments @('init', '-b', 'main') | Out-Null
    Invoke-FixtureGit -Repository $repository -Arguments @('config', 'user.name', 'Sutty Fixture') | Out-Null
    Invoke-FixtureGit -Repository $repository -Arguments @('config', 'user.email', 'fixture@example.invalid') | Out-Null

    Set-Utf8Text -Path (Join-Path $repository 'docs/evidence/EVIDENCE_SCHEMA.md') -Content '# Schema'
    Set-Utf8Text -Path (Join-Path $repository 'docs/evidence/alpha4/README.md') -Content '# Alpha 4'
    Set-Utf8Text -Path (Join-Path $repository 'docs/evidence/alpha4/ssh-auth/README.md') -Content '# SSH auth'
    $bundle = Join-Path $repository 'docs/evidence/alpha4/ssh-auth/existing-bundle'
    Set-Utf8Text -Path (Join-Path $bundle 'manifest.yml') -Content 'schema_version: 1'
    Set-Utf8Text -Path (Join-Path $bundle 'summary.json') -Content '{}'
    Invoke-FixtureGit -Repository $repository -Arguments @('add', '--all') | Out-Null
    Invoke-FixtureGit -Repository $repository -Arguments @('commit', '-m', 'base evidence') | Out-Null
    $base = @(Invoke-FixtureGit -Repository $repository -Arguments @('rev-parse', 'HEAD'))[0]
    return @{
        Repository = $repository
        Base = $base
        Bundle = $bundle
    }
}

function Get-HistoryFailure {
    param(
        [hashtable]$Fixture,
        [string]$BaseRef,
        [string]$HeadRef,
        [switch]$WorkingTree
    )

    if ([string]::IsNullOrWhiteSpace($BaseRef)) {
        $BaseRef = $Fixture.Base
    }
    try {
        $arguments = @{
            RepositoryRoot = $Fixture.Repository
            BaseRef = $BaseRef
        }
        if ($WorkingTree) {
            $arguments.WorkingTree = $true
        }
        elseif (-not [string]::IsNullOrWhiteSpace($HeadRef)) {
            $arguments.HeadRef = $HeadRef
        }
        else {
            $arguments.WorkingTree = $true
        }
        & $Validator @arguments *> $null
        return $null
    }
    catch {
        return $_.Exception.Message
    }
}

function Assert-Accepted {
    param(
        [string]$Name,
        [hashtable]$Fixture,
        [scriptblock]$Arrange,
        [string]$BaseRef,
        [string]$HeadRef
    )

    $script:caseCount++
    if ($null -ne $Arrange) {
        & $Arrange $Fixture
    }
    $failure = Get-HistoryFailure `
        -Fixture $Fixture `
        -BaseRef $BaseRef `
        -HeadRef $HeadRef `
        -WorkingTree:([string]::IsNullOrWhiteSpace($HeadRef))
    if ($null -ne $failure) {
        throw "Evidence-history fixture should pass ($Name): $failure"
    }
}

function Assert-Rejected {
    param(
        [string]$Name,
        [hashtable]$Fixture,
        [scriptblock]$Arrange,
        [string]$BaseRef,
        [string]$HeadRef
    )

    $script:caseCount++
    if ($null -ne $Arrange) {
        & $Arrange $Fixture
    }
    $failure = Get-HistoryFailure `
        -Fixture $Fixture `
        -BaseRef $BaseRef `
        -HeadRef $HeadRef `
        -WorkingTree:([string]::IsNullOrWhiteSpace($HeadRef))
    if ($null -eq $failure) {
        throw "Evidence-history fixture should be rejected: $Name"
    }
}

try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    Assert-Accepted -Name 'unchanged working tree' -Fixture (New-HistoryFixture 'unchanged')

    Assert-Accepted `
        -Name 'tracker documentation modification' `
        -Fixture (New-HistoryFixture 'tracker') `
        -Arrange { param($fixture)
            Set-Utf8Text `
                -Path (Join-Path $fixture.Repository 'docs/evidence/alpha4/ssh-auth/README.md') `
                -Content '# Updated tracker'
        }

    Assert-Accepted `
        -Name 'new canonical bundle' `
        -Fixture (New-HistoryFixture 'new-bundle') `
        -Arrange { param($fixture)
            $newBundle = Join-Path $fixture.Repository 'docs/evidence/alpha4/ssh-auth/new-bundle'
            Set-Utf8Text -Path (Join-Path $newBundle 'manifest.yml') -Content 'schema_version: 1'
            Set-Utf8Text -Path (Join-Path $newBundle 'summary.json') -Content '{}'
        }

    Assert-Rejected `
        -Name 'existing bundle modification' `
        -Fixture (New-HistoryFixture 'modify') `
        -Arrange { param($fixture)
            Set-Utf8Text -Path (Join-Path $fixture.Bundle 'summary.json') -Content '{"changed":true}'
        }

    Assert-Rejected `
        -Name 'existing bundle deletion' `
        -Fixture (New-HistoryFixture 'delete') `
        -Arrange { param($fixture)
            Remove-Item -LiteralPath $fixture.Bundle -Recurse -Force
        }

    Assert-Rejected `
        -Name 'existing bundle rename' `
        -Fixture (New-HistoryFixture 'rename') `
        -Arrange { param($fixture)
            Move-Item -LiteralPath $fixture.Bundle -Destination (
                Join-Path (Split-Path -Parent $fixture.Bundle) 'renamed-bundle')
        }

    Assert-Rejected `
        -Name 'file added to existing bundle' `
        -Fixture (New-HistoryFixture 'extend-existing') `
        -Arrange { param($fixture)
            Set-Utf8Text -Path (Join-Path $fixture.Bundle 'extra.json') -Content '{}'
        }

    Assert-Rejected `
        -Name 'orphan evidence-root file' `
        -Fixture (New-HistoryFixture 'orphan') `
        -Arrange { param($fixture)
            Set-Utf8Text -Path (Join-Path $fixture.Repository 'docs/evidence/orphan.txt') -Content 'orphan'
        }

    $committed = New-HistoryFixture 'committed-new-bundle'
    $newBundle = Join-Path $committed.Repository 'docs/evidence/alpha4/ssh-auth/committed-bundle'
    Set-Utf8Text -Path (Join-Path $newBundle 'manifest.yml') -Content 'schema_version: 1'
    Set-Utf8Text -Path (Join-Path $newBundle 'summary.json') -Content '{}'
    Invoke-FixtureGit -Repository $committed.Repository -Arguments @('add', '--all') | Out-Null
    Invoke-FixtureGit -Repository $committed.Repository -Arguments @('commit', '-m', 'add bundle') | Out-Null
    $committedHead = @(Invoke-FixtureGit -Repository $committed.Repository -Arguments @('rev-parse', 'HEAD'))[0]
    Assert-Accepted `
        -Name 'committed new bundle range' `
        -Fixture $committed `
        -BaseRef $committed.Base `
        -HeadRef $committedHead

    $allZero = New-HistoryFixture 'all-zero'
    Assert-Accepted `
        -Name 'all-zero first-push base' `
        -Fixture $allZero `
        -BaseRef ('0' * 40) `
        -HeadRef $allZero.Base

    Assert-Rejected `
        -Name 'missing base ref' `
        -Fixture (New-HistoryFixture 'missing-ref') `
        -BaseRef 'refs/heads/does-not-exist'

    $nonAncestor = New-HistoryFixture 'non-ancestor'
    Set-Utf8Text -Path (Join-Path $nonAncestor.Repository 'ordinary.txt') -Content 'ordinary'
    Invoke-FixtureGit -Repository $nonAncestor.Repository -Arguments @('add', '--all') | Out-Null
    Invoke-FixtureGit -Repository $nonAncestor.Repository -Arguments @('commit', '-m', 'later') | Out-Null
    $laterCommit = @(Invoke-FixtureGit -Repository $nonAncestor.Repository -Arguments @('rev-parse', 'HEAD'))[0]
    Assert-Rejected `
        -Name 'base is not an ancestor of head' `
        -Fixture $nonAncestor `
        -BaseRef $laterCommit `
        -HeadRef $nonAncestor.Base

    Write-Host "Evidence-history guard self-tests passed ($caseCount accepted/rejected fixture cases)."
}
finally {
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ((Test-Path -LiteralPath $resolvedScratch) -and
        $resolvedScratch.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith(
            'sutty-evidence-history-tests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
