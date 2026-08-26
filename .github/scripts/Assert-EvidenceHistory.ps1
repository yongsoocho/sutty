[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [Alias('BaseCommit')]
    [ValidateNotNullOrEmpty()]
    [string]$BaseRef,

    [Alias('HeadCommit')]
    [string]$HeadRef,

    [switch]$WorkingTree,

    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot = '.'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$zeroCommit = '0000000000000000000000000000000000000000'
$emptyTree = '4b825dc642cb6eb9a060e54bf8d69288fbee4904'
$evidencePath = 'docs/evidence'
$approvedScopes = [System.Collections.Generic.Dictionary[string, string[]]]::new(
    [StringComparer]::Ordinal)
$approvedScopes.Add('alpha4', @(
    'connection-info'
    'package'
    'ssh-auth'
    'ssh-routes'
    'ssh-transport'
))

function Invoke-GitCommand {
    param(
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $previousNativeErrorPreference = $PSNativeCommandUseErrorActionPreference
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        # Git can emit checkout-conversion warnings on stderr while returning a
        # valid machine-readable stdout record. Disable only the safe-CRLF
        # warning for this read-only invocation so stderr cannot be mistaken
        # for a name-status record.
        $output = @(& git -c core.safecrlf=false -C $script:resolvedRepository @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference
        $global:LASTEXITCODE = 0
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $message = ($output | Select-Object -First 10) -join [Environment]::NewLine
        throw "Git command failed while validating evidence history: $message"
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = @($output | ForEach-Object { [string]$_ })
    }
}

function Resolve-CommitRef {
    param(
        [string]$Ref,
        [string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($Ref) -or
        $Ref.Length -gt 256 -or
        $Ref.StartsWith('-', [StringComparison]::Ordinal) -or
        $Ref -match '[\x00-\x20\x7f\\:]' -or
        $Ref.Contains('..', [StringComparison]::Ordinal) -or
        $Ref.Contains('@{', [StringComparison]::Ordinal)) {
        throw "$Description is missing or is not a bounded Git revision."
    }

    $result = Invoke-GitCommand -Arguments @(
        'rev-parse', '--verify', '--end-of-options', "$Ref^{commit}")
    if ($result.Output.Count -ne 1 -or
        $result.Output[0] -cnotmatch '^[0-9a-f]{40}$' -or
        $result.Output[0] -cmatch '^0{40}$') {
        throw "$Description did not resolve to exactly one lowercase 40-character commit."
    }
    return $result.Output[0]
}

function Get-CanonicalBundleRoot {
    param([string]$Path)

    $match = [regex]::Match(
        $Path,
        '^docs/evidence/(?<release>alpha[0-9]+)/(?<scope>[a-z0-9][a-z0-9-]{0,63})/(?<bundle>[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?)/(?<leaf>.+)$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return $null
    }

    $release = $match.Groups['release'].Value
    $scope = $match.Groups['scope'].Value
    if (-not $approvedScopes.ContainsKey($release) -or
        $scope -cnotin $approvedScopes[$release]) {
        return $null
    }
    return "docs/evidence/$release/$scope/$($match.Groups['bundle'].Value)"
}

function Get-ChangedEntries {
    param(
        [string]$DiffBase,
        [string]$DiffHead,
        [switch]$AgainstWorkingTree
    )

    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @('diff', '--name-status', '--no-renames', '--diff-filter=ACDMRTUXB')) {
        $arguments.Add($argument)
    }
    $arguments.Add($DiffBase)
    if (-not $AgainstWorkingTree) {
        $arguments.Add($DiffHead)
    }
    $arguments.Add('--')
    $arguments.Add($evidencePath)

    $result = Invoke-GitCommand -Arguments $arguments.ToArray()
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $result.Output) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -cnotmatch '^(?<status>[A-Z])\t(?<path>.+)$') {
            throw 'Git returned an unparseable evidence-history change record.'
        }
        $entries.Add([pscustomobject]@{
            Status = $Matches.status
            Path = $Matches.path.Replace('\', '/')
        })
    }

    if ($AgainstWorkingTree) {
        $untracked = Invoke-GitCommand -Arguments @(
            'ls-files', '--others', '--exclude-standard', '--', $evidencePath)
        foreach ($path in $untracked.Output) {
            if ([string]::IsNullOrWhiteSpace($path)) {
                continue
            }
            $entries.Add([pscustomobject]@{
                Status = 'A'
                Path = $path.Replace('\', '/')
            })
        }
    }
    return $entries
}

if ($WorkingTree -and -not [string]::IsNullOrWhiteSpace($HeadRef)) {
    throw 'Specify either HeadRef or WorkingTree, not both.'
}

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw 'RepositoryRoot is missing.'
}
$resolvedRepository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$topLevelResult = Invoke-GitCommand -Arguments @('rev-parse', '--show-toplevel')
if ($topLevelResult.Output.Count -ne 1) {
    throw 'RepositoryRoot did not resolve to exactly one Git worktree.'
}
$topLevel = [System.IO.Path]::GetFullPath($topLevelResult.Output[0])
if ($topLevel -cne [System.IO.Path]::GetFullPath($resolvedRepository)) {
    throw 'RepositoryRoot must be the exact Git worktree root.'
}

$useWorkingTree = $WorkingTree -or [string]::IsNullOrWhiteSpace($HeadRef)
$resolvedHead = if ($useWorkingTree) {
    Resolve-CommitRef -Ref 'HEAD' -Description 'Working-tree HEAD'
}
else {
    Resolve-CommitRef -Ref $HeadRef -Description 'HeadRef'
}

$baseIsEmpty = $BaseRef -ceq $zeroCommit
$resolvedBase = if ($baseIsEmpty) {
    $emptyTree
}
else {
    Resolve-CommitRef -Ref $BaseRef -Description 'BaseRef'
}

if (-not $baseIsEmpty) {
    $ancestry = Invoke-GitCommand `
        -Arguments @('merge-base', '--is-ancestor', $resolvedBase, $resolvedHead) `
        -AllowFailure
    if ($ancestry.ExitCode -ne 0) {
        throw 'BaseRef must be an ancestor of HeadRef or working-tree HEAD.'
    }
}

$trackerPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$trackerPaths.Add('docs/evidence/EVIDENCE_SCHEMA.md') | Out-Null
foreach ($release in $approvedScopes.Keys) {
    $trackerPaths.Add("docs/evidence/$release/README.md") | Out-Null
    foreach ($scope in $approvedScopes[$release]) {
        $trackerPaths.Add("docs/evidence/$release/$scope/README.md") | Out-Null
    }
}

$baseBundleRoots = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if (-not $baseIsEmpty) {
    $basePaths = Invoke-GitCommand -Arguments @(
        'ls-tree', '-r', '--name-only', $resolvedBase, '--', $evidencePath)
    foreach ($path in $basePaths.Output) {
        if ($path -cmatch '^docs/evidence/[^/]+/[^/]+/[^/]+/') {
            $segments = @($path.Split('/'))
            $baseBundleRoots.Add(($segments[0..4] -join '/')) | Out-Null
        }
    }
}

$violations = [System.Collections.Generic.List[string]]::new()
$changedEntries = @(Get-ChangedEntries `
    -DiffBase $resolvedBase `
    -DiffHead $resolvedHead `
    -AgainstWorkingTree:$useWorkingTree)
foreach ($entry in $changedEntries) {
    if ($trackerPaths.Contains($entry.Path)) {
        continue
    }

    $bundleRoot = Get-CanonicalBundleRoot -Path $entry.Path
    if ($null -eq $bundleRoot) {
        $violations.Add("$($entry.Status) $($entry.Path) is outside the exact evidence-history allowlist.")
        continue
    }
    if ($baseBundleRoots.Contains($bundleRoot)) {
        $violations.Add("$($entry.Status) $($entry.Path) changes an existing immutable evidence bundle.")
        continue
    }
    if ($entry.Status -cne 'A') {
        $violations.Add("$($entry.Status) $($entry.Path) is not an additive file in a new evidence bundle.")
    }
}

if ($violations.Count -gt 0) {
    $details = @($violations | Sort-Object -Unique | ForEach-Object { " - $_" }) -join [Environment]::NewLine
    throw "Evidence-history validation failed with $(@($violations | Sort-Object -Unique).Count) violation(s):$([Environment]::NewLine)$details"
}

$targetDescription = if ($useWorkingTree) { 'working tree' } else { $resolvedHead }
Write-Host "Evidence-history validation passed from $resolvedBase to $targetDescription ($($changedEntries.Count) changed evidence file(s))."
