[CmdletBinding(DefaultParameterSetName = 'Event')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Body')]
    [AllowEmptyString()]
    [string]$Body,

    [Parameter(ParameterSetName = 'Event')]
    [string]$EventPath = $env:GITHUB_EVENT_PATH,

    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$requiredSections = @(
    '사용자 문제 / User problem',
    '이번 PR의 범위 / Scope',
    '의도적으로 제외한 범위 / Deliberately excluded',
    '상태·취소·종료 동작 / State, cancellation, and shutdown',
    'Secret과 기존 데이터 영향 / Secrets and existing data',
    '정상·실패·취소 테스트 / Normal, failure, and cancellation tests',
    '실제 환경 검증 / Live validation',
    '문서와 요구사항 ID / Documentation and requirement IDs',
    '완료 확인 / Definition of Done'
)
$definitionOfDoneHeading = '완료 확인 / Definition of Done'
$requirementHeading = '문서와 요구사항 ID / Documentation and requirement IDs'
$definitionOfDoneItems = @(
    'Core 또는 저장 계약을 UI보다 먼저 구현했습니다.',
    '정상·실패·취소·종료·migration 중 해당하는 경로를 테스트했습니다.',
    'Timeout, cancellation, dispose, event 해제를 확인했습니다.',
    'Secret이 코드·fixture·log·설정·SQLite에 포함되지 않습니다.',
    'SFTP 변경은 staging·검증·승격과 기존 대상 보존을 확인했습니다.',
    'Multi 변경은 기본 대상 0개와 명시적 대상 확인을 유지합니다.',
    '사용자 표시 문구를 한국어와 영어로 제공했습니다.',
    '제품 범위 검사와 관련 빌드·self-test가 통과합니다.',
    '실환경 의존 항목은 증거를 기록했거나 미검증 상태로 남겼습니다.'
)
$violations = [System.Collections.Generic.List[string]]::new()

function Add-Violation {
    param([string]$Message)

    $violations.Add($Message)
}

function Test-SubstantiveText {
    param([AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    # Format controls include zero-width and bidi characters. They must not make
    # a placeholder or one-letter response look completed.
    return [regex]::Matches($Text, '[\p{L}\p{N}]').Count -ge 12
}

function Test-PlaceholderOnlyText {
    param([AllowEmptyString()][string]$Text)

    $visibleLines = @($Text.Replace("`r`n", "`n").Replace("`r", "`n").Split("`n") |
        ForEach-Object { [regex]::Replace($_, '\p{Cf}', '').Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($visibleLines.Count -ne 1) {
        return $false
    }

    # Ignore punctuation and common Markdown decoration so forms such as N/A,
    # **None**, and "해당 없음." cannot satisfy an explanation section alone.
    $normalized = [regex]::Replace(
        $visibleLines[0],
        '[\s\p{Cf}\p{P}\p{S}]',
        '').ToLowerInvariant()
    return @('na', 'none', 'notapplicable', '해당없음', '없음') -contains $normalized
}

function Remove-MarkdownFencedBlocks {
    param([AllowEmptyString()][string]$Text)

    $keptLines = [System.Collections.Generic.List[string]]::new()
    $fenceCharacter = $null
    $fenceLength = 0
    foreach ($line in $Text.Replace("`r`n", "`n").Replace("`r", "`n").Split("`n")) {
        if ($null -eq $fenceCharacter -and
            $line -match '^ {0,3}(?<marker>`{3,}|~{3,}).*$') {
            $fenceCharacter = $Matches.marker.Substring(0, 1)
            $fenceLength = $Matches.marker.Length
            continue
        }

        if ($null -ne $fenceCharacter) {
            $closingPattern = '^[ ]{0,3}{0}{{{1},}}[\t ]*$' -f `
                [regex]::Escape($fenceCharacter), $fenceLength
            if ($line -match $closingPattern) {
                $fenceCharacter = $null
                $fenceLength = 0
            }
            continue
        }

        $keptLines.Add($line)
    }

    return [string]::Join("`n", $keptLines)
}

function Get-PullRequestBody {
    if ($PSCmdlet.ParameterSetName -eq 'Body') {
        return $Body
    }

    if ([string]::IsNullOrWhiteSpace($EventPath) -or
        -not (Test-Path -LiteralPath $EventPath -PathType Leaf)) {
        throw 'Pull request event JSON is missing. Supply -EventPath or -Body.'
    }

    try {
        $event = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $EventPath).Path) |
            ConvertFrom-Json -Depth 32
    }
    catch {
        throw "Pull request event JSON is invalid: $($_.Exception.Message)"
    }

    $pullRequestProperty = $event.PSObject.Properties['pull_request']
    if ($null -eq $pullRequestProperty -or $null -eq $pullRequestProperty.Value) {
        throw 'The supplied event is not a pull_request event.'
    }

    $bodyProperty = $pullRequestProperty.Value.PSObject.Properties['body']
    if ($null -eq $bodyProperty -or $null -eq $bodyProperty.Value) {
        return ''
    }

    return [string]$bodyProperty.Value
}

function Get-MarkdownSections {
    param([AllowEmptyString()][string]$Markdown)

    $normalized = $Markdown.Replace("`r`n", "`n").Replace("`r", "`n")
    # Treat an unclosed comment as extending to EOF. Otherwise hidden comment text
    # could satisfy later sections even though GitHub does not render it.
    $withoutComments = [regex]::Replace(
        $normalized,
        '<!--[\s\S]*?(?:-->|$)',
        '')
    $sections = [System.Collections.Generic.List[object]]::new()
    $content = [System.Collections.Generic.List[string]]::new()
    $currentHeading = $null
    $fenceCharacter = $null
    $fenceLength = 0

    function Complete-Section {
        if ($null -eq $currentHeading) {
            return
        }

        $sections.Add([pscustomobject]@{
            Heading = $currentHeading
            Content = [string]::Join("`n", $content)
        })
        $content.Clear()
    }

    foreach ($line in $withoutComments.Split("`n")) {
        if ($null -eq $fenceCharacter -and
            $line -match '^ {0,3}(?<marker>`{3,}|~{3,}).*$') {
            $fenceCharacter = $Matches.marker.Substring(0, 1)
            $fenceLength = $Matches.marker.Length
            if ($null -ne $currentHeading) {
                $content.Add($line)
            }
            continue
        }

        if ($null -ne $fenceCharacter) {
            $closingPattern = '^[ ]{0,3}{0}{{{1},}}[\t ]*$' -f `
                [regex]::Escape($fenceCharacter), $fenceLength
            if ($line -match $closingPattern) {
                $fenceCharacter = $null
                $fenceLength = 0
            }
            if ($null -ne $currentHeading) {
                $content.Add($line)
            }
            continue
        }

        if ($line -match '^##[ \t]+(?<heading>.+?)[ \t]*$') {
            Complete-Section
            $currentHeading = $Matches.heading.Trim()
            continue
        }

        if ($null -ne $currentHeading) {
            $content.Add($line)
        }
    }

    Complete-Section
    return @($sections)
}

$pullRequestBody = Get-PullRequestBody
$sections = @(Get-MarkdownSections -Markdown $pullRequestBody)
$sectionLookup = @{}
foreach ($section in $sections) {
    if (-not $sectionLookup.ContainsKey($section.Heading)) {
        $sectionLookup[$section.Heading] = [System.Collections.Generic.List[string]]::new()
    }
    $sectionLookup[$section.Heading].Add($section.Content)
}

$lastRequiredIndex = -1
foreach ($heading in $requiredSections) {
    if (-not $sectionLookup.ContainsKey($heading)) {
        Add-Violation "missing required section: $heading"
        continue
    }

    if ($sectionLookup[$heading].Count -ne 1) {
        Add-Violation "required section must appear exactly once: $heading"
        continue
    }

    $sectionIndex = [array]::FindIndex(
        [object[]]$sections,
        [Predicate[object]] { param($candidate) $candidate.Heading -ceq $heading })
    if ($sectionIndex -le $lastRequiredIndex) {
        Add-Violation "required section is out of template order: $heading"
    }
    else {
        $lastRequiredIndex = $sectionIndex
    }

    if ($heading -cne $definitionOfDoneHeading -and
        $heading -cne $requirementHeading) {
        $sectionContent = $sectionLookup[$heading][0]
        if (Test-PlaceholderOnlyText -Text $sectionContent) {
            Add-Violation "section cannot contain only a placeholder without an explanation: $heading"
        }
        elseif (-not (Test-SubstantiveText -Text $sectionContent)) {
            Add-Violation "section needs a substantive explanation after template comments are removed: $heading"
        }
    }
}

if ($sectionLookup.ContainsKey($requirementHeading) -and
    $sectionLookup[$requirementHeading].Count -eq 1) {
    if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
        Add-Violation "repository root is missing: $RepositoryRoot"
    }
    else {
        $requirementsPath = Join-Path $RepositoryRoot 'docs\REQUIREMENTS.md'
        if (-not (Test-Path -LiteralPath $requirementsPath -PathType Leaf)) {
            Add-Violation "requirements traceability is missing: $requirementsPath"
        }
        else {
            $requirementsText = [System.IO.File]::ReadAllText(
                (Resolve-Path -LiteralPath $requirementsPath).Path)
            $knownRequirementIds = [System.Collections.Generic.HashSet[string]]::new(
                [StringComparer]::Ordinal)
            foreach ($match in [regex]::Matches(
                $requirementsText,
                '(?m)^\|\s*(?<id>[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+)\s*\|')) {
                [void]$knownRequirementIds.Add($match.Groups['id'].Value)
            }

            $requirementContent = Remove-MarkdownFencedBlocks `
                -Text $sectionLookup[$requirementHeading][0]
            $declaredIds = @([regex]::Matches(
                $requirementContent,
                '(?<![A-Z0-9-])(?<id>[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+)(?![A-Z0-9-])') |
                ForEach-Object { $_.Groups['id'].Value } |
                Select-Object -Unique)
            $recognizedIds = @($declaredIds | Where-Object { $knownRequirementIds.Contains($_) })
            $unknownIds = @($declaredIds | Where-Object {
                -not $knownRequirementIds.Contains($_)
            })
            if ($unknownIds.Count -gt 0) {
                Add-Violation 'documentation section contains an unknown requirement ID.'
            }
            if ($recognizedIds.Count -eq 0) {
                Add-Violation 'documentation section must name at least one requirement ID from docs/REQUIREMENTS.md.'
            }
        }
    }
}

if ($sectionLookup.ContainsKey($definitionOfDoneHeading) -and
    $sectionLookup[$definitionOfDoneHeading].Count -eq 1) {
    $definitionOfDoneContent = Remove-MarkdownFencedBlocks `
        -Text $sectionLookup[$definitionOfDoneHeading][0]
    $checkboxes = @([regex]::Matches(
        $definitionOfDoneContent,
        '(?m)^\s*[-*]\s+\[(?<state>[ xX])\]\s+(?<label>.+?)\s*$'))
    if ($checkboxes.Count -eq 0) {
        Add-Violation 'Definition of Done must retain its checklist.'
    }
    elseif (@($checkboxes | Where-Object {
        ($_.Groups['state'].Value -ceq 'x' -or
         $_.Groups['state'].Value -ceq 'X') -and
        $definitionOfDoneItems -ccontains $_.Groups['label'].Value
    }).Count -eq 0) {
        Add-Violation 'Definition of Done must have at least one selected checkbox.'
    }
}

if ($violations.Count -gt 0) {
    throw "Pull request contract failed:`n - $($violations -join "`n - ")"
}

Write-Host 'Pull request body contract passed.'
