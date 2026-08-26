param(
    [string]$GovernanceScript = (Resolve-Path (
        Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-RepositoryGovernance.ps1')).Path,
    [string]$ContractsDirectory = (Resolve-Path (
        Join-Path $PSScriptRoot '..\..\.github\rulesets')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = Join-Path $temporaryBase "sutty-repository-governance-tests-$([guid]::NewGuid().ToString('N'))"
$caseCount = 0

function Set-Utf8Text {
    param([string]$Path, [string]$Content)

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-FreshRulesets {
    $main = Get-Content -LiteralPath (Join-Path $ContractsDirectory 'main.json') -Raw |
        ConvertFrom-Json
    $tags = Get-Content -LiteralPath (Join-Path $ContractsDirectory 'release-tags.json') -Raw |
        ConvertFrom-Json
    return @($main, $tags)
}

function New-Fixture {
    param(
        [string]$Name,
        [scriptblock]$Mutate
    )

    $rulesets = @(Get-FreshRulesets)
    if ($null -ne $Mutate) {
        & $Mutate $rulesets
    }
    $path = Join-Path $scratch "$Name.json"
    Set-Utf8Text `
        -Path $path `
        -Content (($rulesets | ConvertTo-Json -Depth 15) + [Environment]::NewLine)
    return $path
}

function Invoke-Validation {
    param([string]$Path)

    & $GovernanceScript `
        -RulesetsJsonPath $Path `
        -ContractsDirectory $ContractsDirectory *> $null
}

function Assert-Accepted {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Name
    )

    $script:caseCount++
    try {
        & $Action
    }
    catch {
        throw "Repository-governance self-test failed: $Name was rejected: $($_.Exception.Message)"
    }
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Name
    )

    $script:caseCount++
    $rejected = $false
    try {
        & $Action
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Repository-governance self-test failed: $Name was accepted."
    }
}

function Get-Main {
    param([object[]]$Rulesets)
    return @($Rulesets | Where-Object { $_.name -ceq 'main' })[0]
}

function Get-Tags {
    param([object[]]$Rulesets)
    return @($Rulesets | Where-Object { $_.name -ceq 'release-tags' })[0]
}

function Get-Rule {
    param([object]$Ruleset, [string]$Type)
    return @($Ruleset.rules | Where-Object { $_.type -ceq $Type })[0]
}

try {
    [System.IO.Directory]::CreateDirectory($scratch) | Out-Null

    $valid = New-Fixture 'valid'
    Assert-Accepted { Invoke-Validation -Path $valid } 'canonical tracked contracts as actual rulesets'

    $apiMetadata = New-Fixture 'api-metadata' -Mutate {
        param($rulesets)
        foreach ($ruleset in $rulesets) {
            $ruleset | Add-Member -NotePropertyName id -NotePropertyValue 42
            $ruleset | Add-Member -NotePropertyName source_type -NotePropertyValue Repository
            $ruleset | Add-Member -NotePropertyName source -NotePropertyValue 'example/sutty'
        }
    }
    Assert-Accepted { Invoke-Validation -Path $apiMetadata } 'read API metadata with pinned GitHub Actions checks'

    $normalizedPullRequest = New-Fixture 'normalized-pull-request' -Mutate {
        param($rulesets)
        $parameters = (Get-Rule (Get-Main $rulesets) pull_request).parameters
        $parameters | Add-Member -NotePropertyName required_reviewers -NotePropertyValue @()
        $parameters | Add-Member `
            -NotePropertyName require_extra_approval_for_unattributed_changes `
            -NotePropertyValue $true
    }
    Assert-Accepted { Invoke-Validation -Path $normalizedPullRequest } 'GitHub-normalized pull-request response'

    $normalizedReviewer = New-Fixture 'normalized-required-reviewer' -Mutate {
        param($rulesets)
        $parameters = (Get-Rule (Get-Main $rulesets) pull_request).parameters
        $parameters | Add-Member -NotePropertyName required_reviewers -NotePropertyValue @(
            [pscustomobject]@{ reviewer = @{ id = 1; type = 'Team' }; minimum_approvals = 1; file_patterns = @('*') })
        $parameters | Add-Member `
            -NotePropertyName require_extra_approval_for_unattributed_changes `
            -NotePropertyValue $true
    }
    Assert-Rejected { Invoke-Validation -Path $normalizedReviewer } 'unexpected beta required reviewer'

    $normalizedApprovalOff = New-Fixture 'normalized-extra-approval-off' -Mutate {
        param($rulesets)
        $parameters = (Get-Rule (Get-Main $rulesets) pull_request).parameters
        $parameters | Add-Member -NotePropertyName required_reviewers -NotePropertyValue @()
        $parameters | Add-Member `
            -NotePropertyName require_extra_approval_for_unattributed_changes `
            -NotePropertyValue $false
    }
    Assert-Rejected { Invoke-Validation -Path $normalizedApprovalOff } 'disabled unattributed-change approval'

    $normalizedUnknown = New-Fixture 'normalized-unknown-property' -Mutate {
        param($rulesets)
        $parameters = (Get-Rule (Get-Main $rulesets) pull_request).parameters
        $parameters | Add-Member -NotePropertyName required_reviewers -NotePropertyValue @()
        $parameters | Add-Member `
            -NotePropertyName require_extra_approval_for_unattributed_changes `
            -NotePropertyValue $true
        $parameters | Add-Member -NotePropertyName unknown_default -NotePropertyValue $true
    }
    Assert-Rejected { Invoke-Validation -Path $normalizedUnknown } 'unknown normalized pull-request property'

    $missingMain = New-Fixture 'missing-main' -Mutate {
        param($rulesets)
        $rulesets[0] = $rulesets[1]
        $rulesets[1] = $rulesets[1]
    }
    Assert-Rejected { Invoke-Validation -Path $missingMain } 'missing named main ruleset'

    $missingTags = New-Fixture 'missing-tags' -Mutate {
        param($rulesets)
        $rulesets[1] = $rulesets[0]
    }
    Assert-Rejected { Invoke-Validation -Path $missingTags } 'missing named release-tags ruleset'

    $bypass = New-Fixture 'bypass' -Mutate {
        param($rulesets)
        (Get-Main $rulesets).bypass_actors = @([ordered]@{
            actor_id = 1
            actor_type = 'User'
            bypass_mode = 'always'
        })
    }
    Assert-Rejected { Invoke-Validation -Path $bypass } 'main bypass actor'

    $missingBypass = New-Fixture 'missing-bypass' -Mutate {
        param($rulesets)
        (Get-Main $rulesets).PSObject.Properties.Remove('bypass_actors')
    }
    Assert-Rejected { Invoke-Validation -Path $missingBypass } 'missing bypass actor inventory in strict audit'

    $disabled = New-Fixture 'disabled' -Mutate {
        param($rulesets)
        (Get-Tags $rulesets).enforcement = 'disabled'
    }
    Assert-Rejected { Invoke-Validation -Path $disabled } 'disabled tag ruleset'

    $wrongMainTarget = New-Fixture 'wrong-main-target' -Mutate {
        param($rulesets)
        (Get-Main $rulesets).target = 'tag'
    }
    Assert-Rejected { Invoke-Validation -Path $wrongMainTarget } 'wrong main target'

    $wrongTagTarget = New-Fixture 'wrong-tag-target' -Mutate {
        param($rulesets)
        (Get-Tags $rulesets).target = 'branch'
    }
    Assert-Rejected { Invoke-Validation -Path $wrongTagTarget } 'wrong release-tag target'

    $wrongMainRef = New-Fixture 'wrong-main-ref' -Mutate {
        param($rulesets)
        (Get-Main $rulesets).conditions.ref_name.include = @('refs/heads/master')
    }
    Assert-Rejected { Invoke-Validation -Path $wrongMainRef } 'wrong main ref condition'

    $wrongTagRef = New-Fixture 'wrong-tag-ref' -Mutate {
        param($rulesets)
        (Get-Tags $rulesets).conditions.ref_name.include = @('refs/tags/v0.*')
    }
    Assert-Rejected { Invoke-Validation -Path $wrongTagRef } 'narrowed release-tag ref condition'

    $excludedMain = New-Fixture 'excluded-main' -Mutate {
        param($rulesets)
        (Get-Main $rulesets).conditions.ref_name.exclude = @('refs/heads/main')
    }
    Assert-Rejected { Invoke-Validation -Path $excludedMain } 'excluded protected main ref'

    $missingDeletion = New-Fixture 'missing-deletion' -Mutate {
        param($rulesets)
        $main = Get-Main $rulesets
        $main.rules = @($main.rules | Where-Object { $_.type -cne 'deletion' })
    }
    Assert-Rejected { Invoke-Validation -Path $missingDeletion } 'missing deletion protection'

    $missingUpdate = New-Fixture 'missing-update' -Mutate {
        param($rulesets)
        $tags = Get-Tags $rulesets
        $tags.rules = @($tags.rules | Where-Object { $_.type -cne 'update' })
    }
    Assert-Rejected { Invoke-Validation -Path $missingUpdate } 'missing tag update protection'

    $normalizedTagUpdate = New-Fixture 'normalized-tag-update' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Tags $rulesets) update).PSObject.Properties.Remove('parameters')
    }
    Assert-Accepted { Invoke-Validation -Path $normalizedTagUpdate } 'GitHub-normalized type-only tag update rule'

    $nonFastForwardOnly = New-Fixture 'non-fast-forward-only' -Mutate {
        param($rulesets)
        $tags = Get-Tags $rulesets
        $tags.rules = @($tags.rules | Where-Object { $_.type -cne 'update' }) +
            [pscustomobject]@{ type = 'non_fast_forward' }
    }
    Assert-Rejected { Invoke-Validation -Path $nonFastForwardOnly } 'non-fast-forward-only tag protection'

    $fetchAndMergeUpdate = New-Fixture 'fetch-and-merge-update' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Tags $rulesets) update).parameters.update_allows_fetch_and_merge = $true
    }
    Assert-Rejected { Invoke-Validation -Path $fetchAndMergeUpdate } 'tag fetch-and-merge update allowed'

    $stringUpdateParameter = New-Fixture 'string-update-parameter' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Tags $rulesets) update).parameters.update_allows_fetch_and_merge = 'false'
    }
    Assert-Rejected { Invoke-Validation -Path $stringUpdateParameter } 'non-boolean tag update parameter'

    $extraUpdateParameter = New-Fixture 'extra-update-parameter' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Tags $rulesets) update).parameters |
            Add-Member -NotePropertyName update_mode -NotePropertyValue 'restricted'
    }
    Assert-Rejected { Invoke-Validation -Path $extraUpdateParameter } 'extra tag update parameter'

    $tagCreationBlocked = New-Fixture 'tag-creation-blocked' -Mutate {
        param($rulesets)
        $tags = Get-Tags $rulesets
        $tags.rules = @($tags.rules) + [pscustomobject]@{ type = 'creation' }
    }
    Assert-Rejected { Invoke-Validation -Path $tagCreationBlocked } 'creation rule blocks first release tag'

    $approvalCount = New-Fixture 'approval-count' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Main $rulesets) pull_request).parameters.required_approving_review_count = 1
    }
    Assert-Rejected { Invoke-Validation -Path $approvalCount } 'nonzero solo-repository approval count'

    $staleReviews = New-Fixture 'stale-reviews' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Main $rulesets) pull_request).parameters.dismiss_stale_reviews_on_push = $false
    }
    Assert-Rejected { Invoke-Validation -Path $staleReviews } 'stale reviews not dismissed'

    $threads = New-Fixture 'threads' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Main $rulesets) pull_request).parameters.required_review_thread_resolution = $false
    }
    Assert-Rejected { Invoke-Validation -Path $threads } 'unresolved review threads allowed'

    $mergeMethod = New-Fixture 'merge-method' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Main $rulesets) pull_request).parameters.allowed_merge_methods = @(
            'merge', 'squash', 'rebase')
    }
    Assert-Rejected { Invoke-Validation -Path $mergeMethod } 'merge commits added to allowed methods'

    $missingCheck = New-Fixture 'missing-check' -Mutate {
        param($rulesets)
        $status = Get-Rule (Get-Main $rulesets) required_status_checks
        $status.parameters.required_status_checks = @(
            $status.parameters.required_status_checks |
                Where-Object { $_.context -cne 'Pull request body contract' })
    }
    Assert-Rejected { Invoke-Validation -Path $missingCheck } 'missing exact status context'

    $extraCheck = New-Fixture 'extra-check' -Mutate {
        param($rulesets)
        $status = Get-Rule (Get-Main $rulesets) required_status_checks
        $status.parameters.required_status_checks = @($status.parameters.required_status_checks) +
            [pscustomobject]@{
                context = 'Untracked optional check'
                integration_id = 15368
            }
    }
    Assert-Rejected { Invoke-Validation -Path $extraCheck } 'extra status context'

    $strictOff = New-Fixture 'strict-off' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Main $rulesets) required_status_checks).
            parameters.strict_required_status_checks_policy = $false
    }
    Assert-Rejected { Invoke-Validation -Path $strictOff } 'strict up-to-date policy disabled'

    $createBypass = New-Fixture 'create-bypass' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Main $rulesets) required_status_checks).
            parameters.do_not_enforce_on_create = $true
    }
    Assert-Rejected { Invoke-Validation -Path $createBypass } 'status checks skipped on creation'

    $integrationPin = New-Fixture 'integration-pin' -Mutate {
        param($rulesets)
        $status = Get-Rule (Get-Main $rulesets) required_status_checks
        $status.parameters.required_status_checks[0].integration_id = 1234
    }
    Assert-Rejected { Invoke-Validation -Path $integrationPin } 'unexpected status integration pin'

    $missingIntegration = New-Fixture 'missing-integration' -Mutate {
        param($rulesets)
        $status = Get-Rule (Get-Main $rulesets) required_status_checks
        $status.parameters.required_status_checks[0].PSObject.Properties.Remove('integration_id')
    }
    Assert-Rejected { Invoke-Validation -Path $missingIntegration } 'missing GitHub Actions integration pin'

    $wrongType = New-Fixture 'wrong-type' -Mutate {
        param($rulesets)
        (Get-Rule (Get-Main $rulesets) pull_request).
            parameters.dismiss_stale_reviews_on_push = 'true'
    }
    Assert-Rejected { Invoke-Validation -Path $wrongType } 'boolean encoded as string'

    $duplicateProperty = New-Fixture 'duplicate-property'
    $text = Get-Content -LiteralPath $duplicateProperty -Raw
    $text = $text.Replace('"name": "main",', '"name": "main", "name": "main",')
    Set-Utf8Text -Path $duplicateProperty -Content $text
    Assert-Rejected { Invoke-Validation -Path $duplicateProperty } 'duplicate ruleset property'

    $invalidRoot = Join-Path $scratch 'invalid-root.json'
    Set-Utf8Text -Path $invalidRoot -Content '{"name":"main"}'
    Assert-Rejected { Invoke-Validation -Path $invalidRoot } 'non-array actual ruleset response'

    $invalidJson = Join-Path $scratch 'invalid-json.json'
    Set-Utf8Text -Path $invalidJson -Content '[not-json]'
    Assert-Rejected { Invoke-Validation -Path $invalidJson } 'invalid actual ruleset JSON'

    $bom = New-Fixture 'bom'
    $text = Get-Content -LiteralPath $bom -Raw
    [System.IO.File]::WriteAllText($bom, $text, [System.Text.UTF8Encoding]::new($true))
    Assert-Rejected { Invoke-Validation -Path $bom } 'actual ruleset UTF-8 BOM'

    Write-Host "Repository-governance guard self-tests passed ($caseCount cases)."
}
finally {
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ((Test-Path -LiteralPath $resolvedScratch) -and
        $resolvedScratch.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith(
            'sutty-repository-governance-tests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
