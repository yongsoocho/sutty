[CmdletBinding(DefaultParameterSetName = 'Json')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Json')]
    [ValidateNotNullOrEmpty()]
    [string]$RulesetsJsonPath,

    [Parameter(Mandatory, ParameterSetName = 'GitHub')]
    [switch]$QueryGitHub,

    [Parameter(Mandatory, ParameterSetName = 'GitHub')]
    [ValidateNotNullOrEmpty()]
    [string]$Repository,

    [Parameter(ParameterSetName = 'GitHub')]
    [switch]$AllowOmittedBypassActors,

    [string]$ContractsDirectory = (Join-Path $PSScriptRoot '..\rulesets')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$contractNames = @('main', 'release-tags')
$requiredStatusContexts = @(
    'Governance guards'
    'x64 Debug'
    'x64 Release'
    'ARM64 compile'
    'Validate unsigned x64 release artifact'
)
$githubActionsIntegrationId = 15368

function Read-StrictUtf8 {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        throw "$Description must not be a reparse point."
    }
    $bytes = [System.IO.File]::ReadAllBytes($item.FullName)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "$Description must be UTF-8 without a byte-order mark."
    }
    try {
        return [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    }
    catch {
        throw "$Description is not valid UTF-8."
    }
}

function Read-StrictJsonText {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Description
    )

    try {
        $options = [System.Text.Json.JsonDocumentOptions]::new()
        $options.AllowTrailingCommas = $false
        $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
        return [System.Text.Json.JsonDocument]::Parse($Text, $options)
    }
    catch {
        throw "$Description is not strict JSON: $($_.Exception.Message)"
    }
}

function Read-StrictJsonFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    return Read-StrictJsonText `
        -Text (Read-StrictUtf8 -Path $Path -Description $Description) `
        -Description $Description
}

function Assert-NoDuplicateJsonProperties {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw "$Description contains duplicate property '$($property.Name)'."
            }
            Assert-NoDuplicateJsonProperties `
                -Element $property.Value `
                -Description "$Description.$($property.Name)"
        }
    }
    elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($item in $Element.EnumerateArray()) {
            Assert-NoDuplicateJsonProperties -Element $item -Description "$Description[$index]"
            $index++
        }
    }
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "$Description must be a JSON object."
    }
    $actual = @($Element.EnumerateObject() | ForEach-Object { $_.Name })
    if ([string]::Join('|', ($actual | Sort-Object)) -cne
        [string]::Join('|', ($Expected | Sort-Object))) {
        throw "$Description properties do not match the exact governance contract."
    }
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Object.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "$Description must be a JSON object."
    }
    $matches = @($Object.EnumerateObject() | Where-Object { $_.Name -ceq $Name })
    if ($matches.Count -ne 1) {
        throw "$Description must contain exactly one $Name property."
    }
    return $matches[0].Value
}

function Get-RequiredString {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description
    )

    $value = Get-RequiredProperty -Object $Object -Name $Name -Description $Description
    if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
        throw "$Description.$Name must be a JSON string."
    }
    return $value.GetString()
}

function Get-RequiredBoolean {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description
    )

    $value = Get-RequiredProperty -Object $Object -Name $Name -Description $Description
    if ($value.ValueKind -notin @(
            [System.Text.Json.JsonValueKind]::True,
            [System.Text.Json.JsonValueKind]::False)) {
        throw "$Description.$Name must be a JSON boolean."
    }
    return $value.GetBoolean()
}

function Get-RequiredInt32 {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description
    )

    $element = Get-RequiredProperty -Object $Object -Name $Name -Description $Description
    $value = 0
    if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        $element.GetRawText() -cnotmatch '^(?:0|[1-9][0-9]*)$' -or
        -not $element.TryGetInt32([ref]$value)) {
        throw "$Description.$Name must be a canonical nonnegative JSON integer."
    }
    return $value
}

function Assert-ExactStringArray {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Expected,
        [Parameter(Mandatory)][string]$Description,
        [switch]$OrderIndependent
    )

    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
        throw "$Description must be a JSON array."
    }
    $values = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $Element.EnumerateArray()) {
        if ($item.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            throw "$Description entries must be JSON strings."
        }
        $values.Add($item.GetString())
    }
    $actual = [string[]]@($values)
    $comparisonExpected = [string[]]@($Expected)
    if ($OrderIndependent) {
        [Array]::Sort($actual, [StringComparer]::Ordinal)
        [Array]::Sort($comparisonExpected, [StringComparer]::Ordinal)
    }
    if ([string]::Join('|', $actual) -cne [string]::Join('|', $comparisonExpected)) {
        throw "$Description does not match the exact governance contract."
    }
}

function Assert-RuleWithoutParameters {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Rule,
        [Parameter(Mandatory)][string]$ExpectedType,
        [Parameter(Mandatory)][string]$Description
    )

    Assert-ExactProperties -Element $Rule -Expected @('type') -Description $Description
    if ((Get-RequiredString -Object $Rule -Name type -Description $Description) -cne $ExpectedType) {
        throw "$Description has the wrong rule type."
    }
}

function Assert-UpdateRule {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Rule,
        [Parameter(Mandatory)][string]$Description
    )

    Assert-ExactProperties -Element $Rule -Expected @('type', 'parameters') -Description $Description
    if ((Get-RequiredString -Object $Rule -Name type -Description $Description) -cne 'update') {
        throw "$Description has the wrong rule type."
    }
    $parameters = Get-RequiredProperty -Object $Rule -Name parameters -Description $Description
    Assert-ExactProperties `
        -Element $parameters `
        -Expected @('update_allows_fetch_and_merge') `
        -Description "$Description.parameters"
    if (Get-RequiredBoolean `
            -Object $parameters `
            -Name update_allows_fetch_and_merge `
            -Description "$Description.parameters") {
        throw 'release-tags update rule must not allow fetch-and-merge updates.'
    }
}

function Assert-PullRequestRule {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Rule,
        [Parameter(Mandatory)][string]$Description
    )

    Assert-ExactProperties -Element $Rule -Expected @('type', 'parameters') -Description $Description
    $parameters = Get-RequiredProperty -Object $Rule -Name parameters -Description $Description
    Assert-ExactProperties -Element $parameters -Expected @(
        'required_approving_review_count'
        'dismiss_stale_reviews_on_push'
        'require_code_owner_review'
        'require_last_push_approval'
        'required_review_thread_resolution'
        'allowed_merge_methods'
    ) -Description "$Description.parameters"

    if ((Get-RequiredInt32 `
            -Object $parameters `
            -Name required_approving_review_count `
            -Description "$Description.parameters") -ne 0) {
        throw 'main pull_request must require zero approvals for the solo repository.'
    }
    $expectedBooleans = [ordered]@{
        dismiss_stale_reviews_on_push = $true
        require_code_owner_review = $false
        require_last_push_approval = $false
        required_review_thread_resolution = $true
    }
    foreach ($entry in $expectedBooleans.GetEnumerator()) {
        if ((Get-RequiredBoolean `
                -Object $parameters `
                -Name $entry.Key `
                -Description "$Description.parameters") -ne $entry.Value) {
            throw "main pull_request $($entry.Key) does not match the governance contract."
        }
    }
    Assert-ExactStringArray `
        -Element (Get-RequiredProperty `
            -Object $parameters -Name allowed_merge_methods -Description "$Description.parameters") `
        -Expected @('squash', 'rebase') `
        -Description "$Description.parameters.allowed_merge_methods" `
        -OrderIndependent
}

function Assert-StatusChecksRule {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Rule,
        [Parameter(Mandatory)][string]$Description,
        [switch]$StrictContract
    )

    Assert-ExactProperties -Element $Rule -Expected @('type', 'parameters') -Description $Description
    $parameters = Get-RequiredProperty -Object $Rule -Name parameters -Description $Description
    Assert-ExactProperties -Element $parameters -Expected @(
        'required_status_checks'
        'strict_required_status_checks_policy'
        'do_not_enforce_on_create'
    ) -Description "$Description.parameters"
    if (-not (Get-RequiredBoolean `
            -Object $parameters `
            -Name strict_required_status_checks_policy `
            -Description "$Description.parameters")) {
        throw 'main required status checks must use the strict up-to-date policy.'
    }
    if (Get-RequiredBoolean `
            -Object $parameters `
            -Name do_not_enforce_on_create `
            -Description "$Description.parameters") {
        throw 'main required status checks must not be skipped on branch creation.'
    }

    $checksElement = Get-RequiredProperty `
        -Object $parameters -Name required_status_checks -Description "$Description.parameters"
    if ($checksElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
        throw "$Description.parameters.required_status_checks must be a JSON array."
    }
    $contexts = [System.Collections.Generic.List[string]]::new()
    $index = 0
    foreach ($check in $checksElement.EnumerateArray()) {
        $properties = @($check.EnumerateObject() | ForEach-Object { $_.Name })
        $allowedProperties = @('context', 'integration_id')
        if ($check.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or
            @($properties | Where-Object { $_ -cnotin $allowedProperties }).Count -gt 0 -or
            'context' -cnotin $properties -or 'integration_id' -cnotin $properties -or
            $properties.Count -ne 2) {
            throw "$Description.parameters.required_status_checks[$index] has unsupported properties."
        }
        $integrationId = Get-RequiredInt32 `
            -Object $check `
            -Name integration_id `
            -Description "$Description.parameters.required_status_checks[$index]"
        if ($integrationId -ne $githubActionsIntegrationId) {
            throw "$Description status context must be pinned to the GitHub Actions integration."
        }
        $contexts.Add((Get-RequiredString `
            -Object $check `
            -Name context `
            -Description "$Description.parameters.required_status_checks[$index]"))
        $index++
    }
    $actualContexts = [string[]]@($contexts)
    $expectedContexts = [string[]]@($requiredStatusContexts)
    [Array]::Sort($actualContexts, [StringComparer]::Ordinal)
    [Array]::Sort($expectedContexts, [StringComparer]::Ordinal)
    if ([string]::Join('|', $actualContexts) -cne [string]::Join('|', $expectedContexts)) {
        throw 'main required status contexts do not match the exact five-check contract.'
    }
}

function Assert-RulesetContract {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Root,
        [Parameter(Mandatory)][ValidateSet('main', 'release-tags')][string]$ContractName,
        [Parameter(Mandatory)][string]$Description,
        [switch]$StrictRoot,
        [switch]$AllowOmittedBypassActors
    )

    Assert-NoDuplicateJsonProperties -Element $Root -Description $Description
    if ($StrictRoot) {
        Assert-ExactProperties -Element $Root -Expected @(
            'name'
            'target'
            'enforcement'
            'bypass_actors'
            'conditions'
            'rules'
        ) -Description $Description
    }
    if ($Root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "$Description must be a JSON object."
    }

    if ((Get-RequiredString -Object $Root -Name name -Description $Description) -cne $ContractName) {
        throw "$Description does not have the required ruleset name $ContractName."
    }
    $expectedTarget = if ($ContractName -ceq 'main') { 'branch' } else { 'tag' }
    if ((Get-RequiredString -Object $Root -Name target -Description $Description) -cne
        $expectedTarget) {
        throw "$ContractName ruleset has the wrong target."
    }
    if ((Get-RequiredString -Object $Root -Name enforcement -Description $Description) -cne
        'active') {
        throw "$ContractName ruleset must be active."
    }

    $bypassProperties = @(
        $Root.EnumerateObject() | Where-Object { $_.Name -ceq 'bypass_actors' })
    if ($bypassProperties.Count -eq 0) {
        if (-not $AllowOmittedBypassActors) {
            throw "$Description is missing required property bypass_actors."
        }
    }
    elseif ($bypassProperties.Count -ne 1 -or
        $bypassProperties[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or
        @($bypassProperties[0].Value.EnumerateArray()).Count -ne 0) {
        throw "$ContractName ruleset must have no bypass actors."
    }

    $conditions = Get-RequiredProperty -Object $Root -Name conditions -Description $Description
    Assert-ExactProperties -Element $conditions -Expected @('ref_name') -Description "$Description.conditions"
    $refName = Get-RequiredProperty -Object $conditions -Name ref_name -Description "$Description.conditions"
    Assert-ExactProperties -Element $refName -Expected @('include', 'exclude') -Description "$Description.conditions.ref_name"
    $expectedRef = if ($ContractName -ceq 'main') { 'refs/heads/main' } else { 'refs/tags/v*' }
    Assert-ExactStringArray `
        -Element (Get-RequiredProperty `
            -Object $refName -Name include -Description "$Description.conditions.ref_name") `
        -Expected @($expectedRef) `
        -Description "$Description.conditions.ref_name.include"
    Assert-ExactStringArray `
        -Element (Get-RequiredProperty `
            -Object $refName -Name exclude -Description "$Description.conditions.ref_name") `
        -Expected @() `
        -Description "$Description.conditions.ref_name.exclude"

    $rulesElement = Get-RequiredProperty -Object $Root -Name rules -Description $Description
    if ($rulesElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
        throw "$Description.rules must be a JSON array."
    }
    $rules = @($rulesElement.EnumerateArray())
    $expectedTypes = if ($ContractName -ceq 'main') {
        @('deletion', 'non_fast_forward', 'pull_request', 'required_status_checks')
    }
    else {
        @('deletion', 'update')
    }
    if ($rules.Count -ne $expectedTypes.Count) {
        throw "$ContractName ruleset does not contain the exact required rule count."
    }
    $rulesByType = @{}
    for ($index = 0; $index -lt $rules.Count; $index++) {
        $type = Get-RequiredString `
            -Object $rules[$index] -Name type -Description "$Description.rules[$index]"
        if ($type -cnotin $expectedTypes -or $rulesByType.ContainsKey($type)) {
            throw "$ContractName ruleset contains an unexpected or duplicate rule type."
        }
        $rulesByType[$type] = $rules[$index]
    }
    foreach ($type in $expectedTypes) {
        if (-not $rulesByType.ContainsKey($type)) {
            throw "$ContractName ruleset is missing rule $type."
        }
    }
    Assert-RuleWithoutParameters `
        -Rule $rulesByType.deletion `
        -ExpectedType deletion `
        -Description "$Description.rules.deletion"
    if ($ContractName -ceq 'main') {
        Assert-RuleWithoutParameters `
            -Rule $rulesByType.non_fast_forward `
            -ExpectedType non_fast_forward `
            -Description "$Description.rules.non_fast_forward"
        Assert-PullRequestRule `
            -Rule $rulesByType.pull_request `
            -Description "$Description.rules.pull_request"
        Assert-StatusChecksRule `
            -Rule $rulesByType.required_status_checks `
            -Description "$Description.rules.required_status_checks" `
            -StrictContract:$StrictRoot
    }
    else {
        Assert-UpdateRule `
            -Rule $rulesByType.update `
            -Description "$Description.rules.update"
    }
}

function Invoke-GitHubApi {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$Description)

    $previousNativeErrorPreference = $PSNativeCommandUseErrorActionPreference
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        $output = @(& gh @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference
        $global:LASTEXITCODE = 0
    }
    if ($exitCode -ne 0) {
        throw "$Description failed: $($output -join [Environment]::NewLine)"
    }
    return $output -join [Environment]::NewLine
}

if (-not (Test-Path -LiteralPath $ContractsDirectory -PathType Container)) {
    throw "Ruleset contracts directory is missing: $ContractsDirectory"
}
$resolvedContracts = (Resolve-Path -LiteralPath $ContractsDirectory).Path
foreach ($contractName in $contractNames) {
    $contractPath = Join-Path $resolvedContracts "$contractName.json"
    $contractDocument = Read-StrictJsonFile `
        -Path $contractPath -Description "$contractName tracked ruleset contract"
    try {
        Assert-RulesetContract `
            -Root $contractDocument.RootElement `
            -ContractName $contractName `
            -Description "$contractName tracked ruleset contract" `
            -StrictRoot
    }
    finally {
        $contractDocument.Dispose()
    }
}

if ($PSCmdlet.ParameterSetName -ceq 'Json') {
    $actualDocument = Read-StrictJsonFile `
        -Path $RulesetsJsonPath -Description 'actual repository rulesets'
    try {
        $root = $actualDocument.RootElement
        Assert-NoDuplicateJsonProperties -Element $root -Description 'actual repository rulesets'
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw 'Actual repository rulesets JSON must be an array of detailed rulesets.'
        }
        $matches = @{}
        $index = 0
        foreach ($ruleset in $root.EnumerateArray()) {
            if ($ruleset.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                throw "actual repository rulesets[$index] must be a JSON object."
            }
            $name = Get-RequiredString `
                -Object $ruleset -Name name -Description "actual repository rulesets[$index]"
            if ($name -cin $contractNames) {
                if ($matches.ContainsKey($name)) {
                    throw "Actual repository rulesets contain duplicate named contract $name."
                }
                $matches[$name] = $ruleset
            }
            $index++
        }
        foreach ($contractName in $contractNames) {
            if (-not $matches.ContainsKey($contractName)) {
                throw "Actual repository rulesets are missing named contract $contractName."
            }
            Assert-RulesetContract `
                -Root $matches[$contractName] `
                -ContractName $contractName `
                -Description "$contractName actual ruleset"
        }
    }
    finally {
        $actualDocument.Dispose()
    }
}
else {
    if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw 'Repository must be an exact GitHub owner/name slug.'
    }
    if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'GitHub CLI is required for -QueryGitHub.'
    }
    $listText = Invoke-GitHubApi `
        -Arguments @(
            'api', "repos/$Repository/rulesets?per_page=100", '--method', 'GET',
            '--paginate', '--slurp', '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2026-03-10') `
        -Description 'GitHub ruleset list query'
    $listDocument = Read-StrictJsonText -Text $listText -Description 'GitHub ruleset list response'
    try {
        $root = $listDocument.RootElement
        Assert-NoDuplicateJsonProperties -Element $root -Description 'GitHub ruleset list response'
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw 'GitHub ruleset list response must be a page array.'
        }
        $ids = @{}
        foreach ($page in $root.EnumerateArray()) {
            if ($page.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
                throw 'GitHub ruleset list response contains a non-array page.'
            }
            foreach ($summary in $page.EnumerateArray()) {
                $name = Get-RequiredString -Object $summary -Name name -Description 'GitHub ruleset summary'
                if ($name -cin $contractNames) {
                    if ($ids.ContainsKey($name)) {
                        throw "GitHub contains duplicate named ruleset $name."
                    }
                    $idElement = Get-RequiredProperty `
                        -Object $summary -Name id -Description 'GitHub ruleset summary'
                    $id = [long]0
                    if ($idElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
                        $idElement.GetRawText() -cnotmatch '^[1-9][0-9]*$' -or
                        -not $idElement.TryGetInt64([ref]$id)) {
                        throw "GitHub ruleset $name has an invalid ID."
                    }
                    $ids[$name] = $id.ToString([Globalization.CultureInfo]::InvariantCulture)
                }
            }
        }
        foreach ($contractName in $contractNames) {
            if (-not $ids.ContainsKey($contractName)) {
                throw "GitHub is missing named ruleset $contractName."
            }
            $detailText = Invoke-GitHubApi `
                -Arguments @(
                    'api', "repos/$Repository/rulesets/$($ids[$contractName])", '--method', 'GET',
                    '-H', 'Accept: application/vnd.github+json',
                    '-H', 'X-GitHub-Api-Version: 2026-03-10') `
                -Description "GitHub $contractName ruleset query"
            $detailDocument = Read-StrictJsonText `
                -Text $detailText -Description "GitHub $contractName ruleset response"
            try {
                Assert-RulesetContract `
                    -Root $detailDocument.RootElement `
                    -ContractName $contractName `
                    -Description "GitHub $contractName ruleset" `
                    -AllowOmittedBypassActors:$AllowOmittedBypassActors
            }
            finally {
                $detailDocument.Dispose()
            }
        }
    }
    finally {
        $listDocument.Dispose()
    }
}

if ($PSCmdlet.ParameterSetName -ceq 'GitHub' -and $AllowOmittedBypassActors) {
    Write-Warning (
        'The GitHub token may omit bypass_actors. Observable rule semantics were checked, ' +
        'but this invocation is not a complete bypass-actor audit.')
}
Write-Host 'Repository governance ruleset contract passed for main and release-tags.'
