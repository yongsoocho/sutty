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
    $body = @'
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

    # Source files are materialized as CRLF on Windows runners and LF on Linux.
    # Keep fixture mutation inputs deterministic on every checkout platform.
    return $body.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Replace-Required {
    param(
        [AllowEmptyString()][string]$Text,
        [string]$OldValue,
        [AllowEmptyString()][string]$NewValue,
        [string]$FixtureName
    )

    if ([string]::IsNullOrEmpty($OldValue) -or
        $Text.IndexOf($OldValue, [StringComparison]::Ordinal) -lt 0) {
        throw "Pull-request-contract self-test fixture is stale: $FixtureName did not contain its required source text."
    }

    return $Text.Replace($OldValue, $NewValue)
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

    $crlfValid = $valid.Replace("`n", "`r`n")
    Assert-Accepted -Body $crlfValid -Name 'complete CRLF body'

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

    $commentOnlyScope = Replace-Required `
        -Text $valid `
        -OldValue 'Add the pull request body contract guard and its focused fixtures.' `
        -NewValue '<!-- Add scope here. -->' `
        -FixtureName 'comment-only scope'
    Assert-Rejected `
        -Body $commentOnlyScope `
        -Name 'comment-only scope' `
        -ExpectedMessage '이번 PR의 범위 / Scope'

    $emptyScope = Replace-Required `
        -Text $valid `
        -OldValue 'Add the pull request body contract guard and its focused fixtures.' `
        -NewValue '' `
        -FixtureName 'empty scope'
    Assert-Rejected `
        -Body $emptyScope `
        -Name 'empty scope' `
        -ExpectedMessage '이번 PR의 범위 / Scope'

    $emptyLiveValidation = Replace-Required `
        -Text $valid `
        -OldValue 'Live validation: Not run. Reason: no runtime product behavior changed. Remaining status: Partial.' `
        -NewValue '' `
        -FixtureName 'empty live validation'
    Assert-Rejected `
        -Body $emptyLiveValidation `
        -Name 'empty live validation' `
        -ExpectedMessage '실제 환경 검증 / Live validation'

    foreach ($placeholder in @('Not applicable', 'N/A', 'None', '해당 없음')) {
        $placeholderScope = Replace-Required `
            -Text $valid `
            -OldValue 'Add the pull request body contract guard and its focused fixtures.' `
            -NewValue $placeholder `
            -FixtureName "placeholder-only scope: $placeholder"
        Assert-Rejected `
            -Body $placeholderScope `
            -Name "placeholder-only scope: $placeholder" `
            -ExpectedMessage 'placeholder without an explanation'
    }

    $explainedPlaceholder = Replace-Required `
        -Text $valid `
        -OldValue 'Add the pull request body contract guard and its focused fixtures.' `
        -NewValue 'N/A — Reason: this fixture changes only the pull request contract documentation.' `
        -FixtureName 'placeholder with a substantive reason'
    Assert-Accepted `
        -Body $explainedPlaceholder `
        -Name 'placeholder with a substantive reason'

    $missingRequirement = Replace-Required `
        -Text $valid `
        -OldValue 'NFR-009' `
        -NewValue 'Documentation was updated.' `
        -FixtureName 'missing requirement ID'
    Assert-Rejected `
        -Body $missingRequirement `
        -Name 'missing requirement ID' `
        -ExpectedMessage 'must name at least one requirement ID'

    $unknownRequirement = Replace-Required `
        -Text $valid `
        -OldValue 'NFR-009' `
        -NewValue 'FAKE-999' `
        -FixtureName 'unknown requirement ID'
    Assert-Rejected `
        -Body $unknownRequirement `
        -Name 'unknown requirement ID' `
        -ExpectedMessage 'must name at least one requirement ID'

    $mixedUnknownRequirement = Replace-Required `
        -Text $valid `
        -OldValue 'NFR-009' `
        -NewValue 'NFR-009 FAKE-999' `
        -FixtureName 'known and unknown requirement IDs mixed together'
    Assert-Rejected `
        -Body $mixedUnknownRequirement `
        -Name 'known and unknown requirement IDs mixed together' `
        -ExpectedMessage 'contains an unknown requirement ID'

    $commentedRequirement = Replace-Required `
        -Text $valid `
        -OldValue 'NFR-009' `
        -NewValue '<!-- NFR-009 --> Documentation was updated.' `
        -FixtureName 'comment-only requirement ID'
    Assert-Rejected `
        -Body $commentedRequirement `
        -Name 'comment-only requirement ID' `
        -ExpectedMessage 'must name at least one requirement ID'

    $fencedRequirementText = @'
```text
NFR-009
```
'@
    $fencedRequirement = Replace-Required `
        -Text $valid `
        -OldValue 'NFR-009' `
        -NewValue $fencedRequirementText `
        -FixtureName 'requirement ID only in fenced code'
    Assert-Rejected `
        -Body $fencedRequirement `
        -Name 'requirement ID only in fenced code' `
        -ExpectedMessage 'must name at least one requirement ID'

    $visibleRequirementAfterFenceText = @'
````text
FAKE-999
   ````
NFR-009
'@
    $visibleRequirementAfterFence = Replace-Required `
        -Text $valid `
        -OldValue 'NFR-009' `
        -NewValue $visibleRequirementAfterFenceText `
        -FixtureName 'requirement ID after matching indented closing fence'
    Assert-Accepted `
        -Body $visibleRequirementAfterFence `
        -Name 'requirement ID after matching indented closing fence'

    $uncheckedDefinition = Replace-Required `
        -Text $valid `
        -OldValue '- [x]' `
        -NewValue '- [ ]' `
        -FixtureName 'all Definition of Done boxes unchecked'
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
    $fencedCheckedDefinition = Replace-Required `
        -Text $uncheckedDefinition `
        -OldValue '- [ ] 정상·실패·취소·종료·migration 중 해당하는 경로를 테스트했습니다.' `
        -NewValue $fencedCheckedText `
        -FixtureName 'checked Definition of Done box only in fenced code'
    Assert-Rejected `
        -Body $fencedCheckedDefinition `
        -Name 'checked Definition of Done box only in fenced code' `
        -ExpectedMessage 'at least one selected checkbox'

    $unclosedComment = Replace-Required `
        -Text $valid `
        -OldValue 'Add the pull request body contract guard and its focused fixtures.' `
        -NewValue '<!-- hidden scope without a closing delimiter' `
        -FixtureName 'unclosed comment hides remaining sections'
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
        $oneLetterSections = Replace-Required `
            -Text $oneLetterSections `
            -OldValue $value `
            -NewValue 'x' `
            -FixtureName 'one-letter prose sections'
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

    $duplicateScope = Replace-Required `
        -Text $valid `
        -OldValue '## 의도적으로 제외한 범위 / Deliberately excluded' `
        -NewValue "## 이번 PR의 범위 / Scope`n`nDuplicate scope.`n`n## 의도적으로 제외한 범위 / Deliberately excluded" `
        -FixtureName 'duplicate required section'
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
    $fencedHeading = Replace-Required `
        -Text $valid `
        -OldValue "## 이번 PR의 범위 / Scope`n`nAdd the pull request body contract guard and its focused fixtures.`n`n" `
        -NewValue '' `
        -FixtureName 'heading inside fenced code section removal'
    $fencedHeading = Replace-Required `
        -Text $fencedHeading `
        -OldValue 'Users need reviewable change intent and validation boundaries.' `
        -NewValue $fencedReplacement `
        -FixtureName 'heading inside fenced code injection'
    Assert-Rejected `
        -Body $fencedHeading `
        -Name 'heading inside fenced code' `
        -ExpectedMessage 'missing required section: 이번 PR의 범위 / Scope'

    $closedLongFenceReplacement = @'
Users need reviewable change intent and validation boundaries.

````text
## 이번 PR의 범위 / Scope
This heading remains hidden inside the four-backtick fence.
   ````
'@
    $closedLongFenceBody = Replace-Required `
        -Text $valid `
        -OldValue 'Users need reviewable change intent and validation boundaries.' `
        -NewValue $closedLongFenceReplacement `
        -FixtureName 'matching indented closing fence exposes following headings'
    Assert-Accepted `
        -Body $closedLongFenceBody `
        -Name 'matching indented closing fence exposes following headings'

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
    $longFenceBody = Replace-Required `
        -Text $valid `
        -OldValue "## 사용자 문제 / User problem`n`nUsers need reviewable change intent and validation boundaries.`n`n## 이번 PR의 범위 / Scope`n`nAdd the pull request body contract guard and its focused fixtures.`n`n## 의도적으로 제외한 범위 / Deliberately excluded`n`nNo product runtime behavior or release evidence is changed." `
        -NewValue "## 사용자 문제 / User problem`n`n$longFenceReplacement" `
        -FixtureName 'shorter closing fence cannot expose hidden headings'
    Assert-Rejected `
        -Body $longFenceBody `
        -Name 'shorter closing fence cannot expose hidden headings' `
        -ExpectedMessage 'missing required section: 이번 PR의 범위 / Scope'

    $longFenceBodyCrlf = $longFenceBody.Replace("`n", "`r`n")
    Assert-Rejected `
        -Body $longFenceBodyCrlf `
        -Name 'shorter closing fence cannot expose hidden headings in CRLF body' `
        -ExpectedMessage 'missing required section: 이번 PR의 범위 / Scope'

    $longTildeFenceReplacement = @'
Users need reviewable change intent and validation boundaries.

~~~~text
## 이번 PR의 범위 / Scope
Fake scope
~~~
## 의도적으로 제외한 범위 / Deliberately excluded
Fake exclusion that remains inside the four-tilde fence.
~~~~
'@
    $longTildeFenceBody = Replace-Required `
        -Text $valid `
        -OldValue "## 사용자 문제 / User problem`n`nUsers need reviewable change intent and validation boundaries.`n`n## 이번 PR의 범위 / Scope`n`nAdd the pull request body contract guard and its focused fixtures.`n`n## 의도적으로 제외한 범위 / Deliberately excluded`n`nNo product runtime behavior or release evidence is changed." `
        -NewValue "## 사용자 문제 / User problem`n`n$longTildeFenceReplacement" `
        -FixtureName 'shorter tilde closing fence cannot expose hidden headings'
    Assert-Rejected `
        -Body $longTildeFenceBody `
        -Name 'shorter tilde closing fence cannot expose hidden headings' `
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
