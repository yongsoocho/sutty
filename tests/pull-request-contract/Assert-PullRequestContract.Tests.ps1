param(
    [string]$ContractScript = (Resolve-Path (
        Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-PullRequestContract.ps1')).Path,

    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$caseCount = 0
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = Join-Path $temporaryBase "sutty-pull-request-contract-tests-$([Guid]::NewGuid().ToString('N'))"

function New-ValidBody {
    return @'
## 사용자 문제 / User problem

Users need reviewable change intent and validation boundaries.

## 이번 PR의 범위 / Scope

Add the pull request body contract guard and its focused fixtures.

## 의도적으로 제외한 범위 / Deliberately excluded

No product runtime behavior or release evidence is changed.

## 상태·취소·종료 동작 / State, cancellation, and shutdown

The bounded validation process exits successfully or reports all contract violations.

## Secret과 기존 데이터 영향 / Secrets and existing data

The guard reads public pull request text and writes no product data.

## 정상·실패·취소 테스트 / Normal, failure, and cancellation tests

Focused fixtures cover accepted and rejected bodies without network access.

## 실제 환경 검증 / Live validation

Live validation: Not run. Reason: no runtime product behavior changed. Remaining status: Partial.

## 문서와 요구사항 ID / Documentation and requirement IDs

NFR-009

## 완료 확인 / Definition of Done

- [x] 정상·실패·취소·종료·migration 중 해당하는 경로를 테스트했습니다.
- [ ] 실환경 의존 항목은 증거를 기록했거나 미검증 상태로 남겼습니다.
'@
}

function Get-ValidationFailure {
    param([AllowEmptyString()][string]$Body)

    try {
        & $ContractScript -Body $Body -RepositoryRoot $RepositoryRoot *> $null
        return $null
    }
    catch {
        return $_.Exception.Message
    }
}

function Assert-Accepted {
    param(
        [string]$Body,
        [string]$Name
    )

    $script:caseCount++
    $failure = Get-ValidationFailure -Body $Body
    if ($null -ne $failure) {
        throw "Pull-request-contract self-test failed: $Name was rejected: $failure"
    }
}

function Assert-Rejected {
    param(
        [string]$Body,
        [string]$Name,
        [string]$ExpectedMessage
    )

    $script:caseCount++
    $failure = Get-ValidationFailure -Body $Body
    if ($null -eq $failure) {
        throw "Pull-request-contract self-test failed: $Name was accepted."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedMessage) -and
        -not $failure.Contains($ExpectedMessage, [StringComparison]::Ordinal)) {
        throw "Pull-request-contract self-test failed: $Name returned the wrong failure: $failure"
    }
}

try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    $valid = New-ValidBody
    Assert-Accepted -Body $valid -Name 'complete body'

    $script:caseCount++
    $eventPath = Join-Path $scratch 'pull-request-event.json'
    $eventJson = @{ pull_request = @{ body = $valid } } | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText(
        $eventPath,
        $eventJson,
        [System.Text.UTF8Encoding]::new($false))
    try {
        & $ContractScript -EventPath $eventPath -RepositoryRoot $RepositoryRoot *> $null
    }
    catch {
        throw "Pull-request-contract self-test failed: event body was rejected: $($_.Exception.Message)"
    }

    $template = [System.IO.File]::ReadAllText(
        (Resolve-Path (Join-Path $RepositoryRoot '.github\pull_request_template.md')).Path)
    Assert-Rejected `
        -Body $template `
        -Name 'unchanged template' `
        -ExpectedMessage 'section needs a substantive explanation'

    $commentOnlyScope = $valid.Replace(
        'Add the pull request body contract guard and its focused fixtures.',
        '<!-- Add scope here. -->')
    Assert-Rejected `
        -Body $commentOnlyScope `
        -Name 'comment-only scope' `
        -ExpectedMessage '이번 PR의 범위 / Scope'

    $emptyScope = $valid.Replace(
        'Add the pull request body contract guard and its focused fixtures.',
        '')
    Assert-Rejected `
        -Body $emptyScope `
        -Name 'empty scope' `
        -ExpectedMessage '이번 PR의 범위 / Scope'

    $emptyLiveValidation = $valid.Replace(
        'Live validation: Not run. Reason: no runtime product behavior changed. Remaining status: Partial.',
        '')
    Assert-Rejected `
        -Body $emptyLiveValidation `
        -Name 'empty live validation' `
        -ExpectedMessage '실제 환경 검증 / Live validation'

    foreach ($placeholder in @('Not applicable', 'N/A', 'None', '해당 없음')) {
        $placeholderScope = $valid.Replace(
            'Add the pull request body contract guard and its focused fixtures.',
            $placeholder)
        Assert-Rejected `
            -Body $placeholderScope `
            -Name "placeholder-only scope: $placeholder" `
            -ExpectedMessage 'placeholder without an explanation'
    }

    $explainedPlaceholder = $valid.Replace(
        'Add the pull request body contract guard and its focused fixtures.',
        'N/A — Reason: this fixture changes only the pull request contract documentation.')
    Assert-Accepted `
        -Body $explainedPlaceholder `
        -Name 'placeholder with a substantive reason'

    $missingRequirement = $valid.Replace('NFR-009', 'Documentation was updated.')
    Assert-Rejected `
        -Body $missingRequirement `
        -Name 'missing requirement ID' `
        -ExpectedMessage 'must name at least one requirement ID'

    $unknownRequirement = $valid.Replace('NFR-009', 'FAKE-999')
    Assert-Rejected `
        -Body $unknownRequirement `
        -Name 'unknown requirement ID' `
        -ExpectedMessage 'must name at least one requirement ID'

    $mixedUnknownRequirement = $valid.Replace('NFR-009', 'NFR-009 FAKE-999')
    Assert-Rejected `
        -Body $mixedUnknownRequirement `
        -Name 'known and unknown requirement IDs mixed together' `
        -ExpectedMessage 'contains an unknown requirement ID'

    $commentedRequirement = $valid.Replace('NFR-009', '<!-- NFR-009 --> Documentation was updated.')
    Assert-Rejected `
        -Body $commentedRequirement `
        -Name 'comment-only requirement ID' `
        -ExpectedMessage 'must name at least one requirement ID'

    $fencedRequirementText = @'
```text
NFR-009
```
'@
    $fencedRequirement = $valid.Replace('NFR-009', $fencedRequirementText)
    Assert-Rejected `
        -Body $fencedRequirement `
        -Name 'requirement ID only in fenced code' `
        -ExpectedMessage 'must name at least one requirement ID'

    $uncheckedDefinition = $valid.Replace('- [x]', '- [ ]')
    Assert-Rejected `
        -Body $uncheckedDefinition `
        -Name 'all Definition of Done boxes unchecked' `
        -ExpectedMessage 'at least one selected checkbox'

    $fencedCheckedText = @'
- [ ] 정상·실패·취소·종료·migration 중 해당하는 경로를 테스트했습니다.

```text
- [x] hidden code sample
```
'@
    $fencedCheckedDefinition = $uncheckedDefinition.Replace(
        '- [ ] 정상·실패·취소·종료·migration 중 해당하는 경로를 테스트했습니다.',
        $fencedCheckedText)
    Assert-Rejected `
        -Body $fencedCheckedDefinition `
        -Name 'checked Definition of Done box only in fenced code' `
        -ExpectedMessage 'at least one selected checkbox'

    $unclosedComment = $valid.Replace(
        'Add the pull request body contract guard and its focused fixtures.',
        '<!-- hidden scope without a closing delimiter')
    Assert-Rejected `
        -Body $unclosedComment `
        -Name 'unclosed comment hides remaining sections' `
        -ExpectedMessage '이번 PR의 범위 / Scope'

    $oneLetterSections = $valid
    foreach ($value in @(
        'Users need reviewable change intent and validation boundaries.',
        'Add the pull request body contract guard and its focused fixtures.',
        'No product runtime behavior or release evidence is changed.',
        'The bounded validation process exits successfully or reports all contract violations.',
        'The guard reads public pull request text and writes no product data.',
        'Focused fixtures cover accepted and rejected bodies without network access.',
        'Live validation: Not run. Reason: no runtime product behavior changed. Remaining status: Partial.')) {
        $oneLetterSections = $oneLetterSections.Replace($value, 'x')
    }
    Assert-Rejected `
        -Body $oneLetterSections `
        -Name 'one-letter prose sections' `
        -ExpectedMessage 'section needs a substantive explanation'

    $arbitraryCheckedDefinition = $uncheckedDefinition + "`n- [x] arbitrary"
    Assert-Rejected `
        -Body $arbitraryCheckedDefinition `
        -Name 'arbitrary checked Definition of Done item' `
        -ExpectedMessage 'at least one selected checkbox'

    $duplicateScope = $valid.Replace(
        '## 의도적으로 제외한 범위 / Deliberately excluded',
        "## 이번 PR의 범위 / Scope`n`nDuplicate scope.`n`n## 의도적으로 제외한 범위 / Deliberately excluded")
    Assert-Rejected `
        -Body $duplicateScope `
        -Name 'duplicate required section' `
        -ExpectedMessage 'must appear exactly once'

    $fencedReplacement = @'
Users need reviewable change intent and validation boundaries.

```text
## 이번 PR의 범위 / Scope
Fake scope
```
'@
    $fencedHeading = $valid.Replace(
        "## 이번 PR의 범위 / Scope`n`nAdd the pull request body contract guard and its focused fixtures.`n`n",
        '').Replace(
        'Users need reviewable change intent and validation boundaries.',
        $fencedReplacement)
    Assert-Rejected `
        -Body $fencedHeading `
        -Name 'heading inside fenced code' `
        -ExpectedMessage 'missing required section: 이번 PR의 범위 / Scope'

    $longFenceReplacement = @'
Users need reviewable change intent and validation boundaries.

````text
## 이번 PR의 범위 / Scope
Fake scope
```
## 의도적으로 제외한 범위 / Deliberately excluded
Fake exclusion that remains inside the four-backtick fence.
````
'@
    $longFenceBody = $valid.Replace(
        "## 사용자 문제 / User problem`n`nUsers need reviewable change intent and validation boundaries.`n`n## 이번 PR의 범위 / Scope`n`nAdd the pull request body contract guard and its focused fixtures.`n`n## 의도적으로 제외한 범위 / Deliberately excluded`n`nNo product runtime behavior or release evidence is changed.",
        "## 사용자 문제 / User problem`n`n$longFenceReplacement")
    Assert-Rejected `
        -Body $longFenceBody `
        -Name 'shorter closing fence cannot expose hidden headings' `
        -ExpectedMessage 'missing required section: 이번 PR의 범위 / Scope'

    $script:caseCount++
    $workflow = [System.IO.File]::ReadAllText(
        (Resolve-Path (Join-Path $RepositoryRoot '.github\workflows\ci.yml')).Path)
    if ($workflow -notmatch '(?ms)^  pull_request:\s*\r?\n\s+types:\s*\[[^\]]*edited[^\]]*\]') {
        throw 'Pull-request-contract self-test failed: CI does not rerun for edited pull request bodies.'
    }
    if ($workflow -notmatch 'tests\\pull-request-contract\\Assert-PullRequestContract\.Tests\.ps1') {
        throw 'Pull-request-contract self-test failed: CI does not run the contract fixtures.'
    }
    if ($workflow -notmatch '(?s)if:\s*github\.event_name == ''pull_request''.*Assert-PullRequestContract\.ps1') {
        throw 'Pull-request-contract self-test failed: CI does not enforce the body only for pull_request events.'
    }

    $script:caseCount++
    $trustedWorkflow = [System.IO.File]::ReadAllText(
        (Resolve-Path (Join-Path $RepositoryRoot '.github\workflows\pull-request-contract.yml')).Path)
    if ($trustedWorkflow -notmatch '(?m)^  pull_request_target:\s*$' -or
        $trustedWorkflow -notmatch '(?ms)^permissions:\s*\r?\n  contents:\s*read\s*$' -or
        $trustedWorkflow -notmatch 'ref:\s*\$\{\{ github\.event\.pull_request\.base\.sha \}\}' -or
        $trustedWorkflow -notmatch 'persist-credentials:\s*false' -or
        $trustedWorkflow -notmatch 'Assert-PullRequestContract\.ps1\s+-EventPath\s+\$env:GITHUB_EVENT_PATH') {
        throw 'Pull-request-contract self-test failed: the trusted-base workflow contract is incomplete.'
    }
    if ($trustedWorkflow -match 'pull_request\.head' -or
        $trustedWorkflow -match 'github\.head_ref' -or
        $trustedWorkflow -match '(?i)checkout[^\r\n]*head') {
        throw 'Pull-request-contract self-test failed: the trusted-base workflow references pull request head code.'
    }

    Write-Host "Pull request contract guard self-tests passed ($caseCount cases)."
}
finally {
    if ((Test-Path -LiteralPath $scratch) -and
        $scratch.StartsWith(
            (Join-Path $temporaryBase 'sutty-pull-request-contract-tests-'),
            [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
