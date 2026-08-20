[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$Commit,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$EvidenceOutputRoot,

    [switch]$RedactionReviewed
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-NativeRequired {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Get-FreeLoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$resolvedEvidenceRoot = [IO.Path]::GetFullPath($EvidenceOutputRoot)
if ([IO.Path]::GetFileName($resolvedPackage) -cne 'Sutty-v0.1.0-alpha.4-win-x64.zip') {
    throw 'SSH-LIVE-001 requires the exact Alpha 4 x64 candidate archive name.'
}

$headOutput = @(& git -C $repositoryRoot rev-parse --verify HEAD 2>$null)
if ($LASTEXITCODE -ne 0) {
    throw 'The candidate Git HEAD could not be resolved.'
}
$headCommit = ($headOutput -join '').Trim()
if ($headCommit -cne $Commit) {
    throw 'The supplied evidence commit does not equal the checked-out Git HEAD.'
}

$worktreeStatus = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'The candidate Git worktree status could not be verified.'
}
if ($worktreeStatus.Count -ne 0) {
    throw 'The formal SSH-LIVE-001 run requires a completely clean Git worktree.'
}

& git -C $repositoryRoot show-ref --verify --quiet refs/remotes/origin/main
$originMainStatus = $LASTEXITCODE
if ($originMainStatus -eq 0) {
    & git -C $repositoryRoot merge-base --is-ancestor $Commit refs/remotes/origin/main
    if ($LASTEXITCODE -ne 0) {
        throw 'The gate commit is not contained in the available origin/main reference.'
    }
}
elseif ($originMainStatus -ne 1) {
    throw 'The available origin/main reference could not be verified.'
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$runIdentifier = [Guid]::NewGuid().ToString('N')
$secretRoot = [IO.Path]::GetFullPath(
    (Join-Path $temporaryBase "sutty-alpha4-openssh-$runIdentifier"))
$secretPath = Join-Path $secretRoot 'password'
$containerName = "sutty-alpha4-gate-$runIdentifier"
$containerStarted = $false
$password = $null
$environmentNames = @(
    'SUTTY_TEST_SSH_HOST',
    'SUTTY_TEST_SSH_PORT',
    'SUTTY_TEST_SSH_USER',
    'SUTTY_TEST_SSH_PASSWORD',
    'SUTTY_TEST_SSH_AUTH',
    'SUTTY_TEST_HOST_KEY_SHA256',
    'SUTTY_TEST_REMOTE_ROOT',
    'SUTTY_TEST_MODES',
    'SUTTY_TEST_BLACKHOLE_HOST',
    'SUTTY_TEST_BLACKHOLE_PORT',
    'SUTTY_TEST_SERVER_AUDIT_COMMAND',
    'SUTTY_TEST_PACKAGE_PATH',
    'SUTTY_EVIDENCE_OUTPUT_DIR',
    'SUTTY_EVIDENCE_APPROVED',
    'SUTTY_EVIDENCE_GATE_ID',
    'SUTTY_EVIDENCE_COMMIT',
    'SUTTY_EVIDENCE_PACKAGE_SHA256',
    'SUTTY_EVIDENCE_SERVER_FAMILY',
    'SUTTY_EVIDENCE_SERVER_VERSION',
    'SUTTY_EVIDENCE_REDACTION_REVIEWED'
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    if (-not $secretRoot.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($secretRoot) -cnotmatch '^sutty-alpha4-openssh-[0-9a-f]{32}$') {
        throw 'The runtime secret directory did not resolve beneath the OS temporary directory.'
    }
    [IO.Directory]::CreateDirectory($secretRoot) | Out-Null
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    [IO.File]::WriteAllText($secretPath, $password, [Text.UTF8Encoding]::new($false))

    Push-Location $repositoryRoot
    try {
        Invoke-NativeRequired docker build --tag sutty-alpha4-openssh-lab:local `
            tests/live-server/openssh
        Invoke-NativeRequired dotnet build `
            tests/sutty.LiveServer.SelfTest/sutty.LiveServer.SelfTest.csproj `
            --configuration Release `
            --no-restore `
            '-p:Platform=x64'
    }
    finally {
        Pop-Location
    }

    $sshPort = Get-FreeLoopbackPort
    do {
        $blackholePort = Get-FreeLoopbackPort
    } while ($blackholePort -eq $sshPort)

    Invoke-NativeRequired docker run --detach --name $containerName `
        --mount "type=bind,source=$secretPath,target=/run/secrets/sutty_password,readonly" `
        --publish "127.0.0.1:${sshPort}:22" `
        --publish "127.0.0.1:${blackholePort}:2222" `
        sutty-alpha4-openssh-lab:local
    $containerStarted = $true

    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        & docker exec $containerName /usr/sbin/sshd -t -f /etc/ssh/sshd_config 2>$null
        if ($LASTEXITCODE -eq 0) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) {
        throw 'The disposable OpenSSH lab did not become ready.'
    }

    $fingerprintText = (& docker exec $containerName ssh-keygen -lf `
        /etc/ssh/ssh_host_ed25519_key.pub -E sha256) -join ' '
    if ($LASTEXITCODE -ne 0) {
        throw 'The disposable lab host-key fingerprint could not be provisioned.'
    }
    $fingerprint = [regex]::Match(
        $fingerprintText,
        'SHA256:[A-Za-z0-9+/]{43}').Value
    if ([string]::IsNullOrEmpty($fingerprint)) {
        throw 'The disposable lab host-key fingerprint was not canonical.'
    }

    $versionText = (& docker exec $containerName /usr/sbin/sshd -V 2>&1) -join ' '
    $version = [regex]::Match(
        $versionText,
        'OpenSSH_([A-Za-z0-9._+~-]{1,32})').Groups[1].Value
    if ([string]::IsNullOrEmpty($version)) {
        throw 'The disposable lab server version was not canonical.'
    }

    Invoke-NativeRequired docker exec $containerName /bin/sh -c ': > /run/sutty-audit/events'

    $packageSha256 = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $values = @{
        SUTTY_TEST_SSH_HOST = '127.0.0.1'
        SUTTY_TEST_SSH_PORT = $sshPort.ToString([Globalization.CultureInfo]::InvariantCulture)
        SUTTY_TEST_SSH_USER = 'sutty-live'
        SUTTY_TEST_SSH_PASSWORD = $password
        SUTTY_TEST_SSH_AUTH = 'Password'
        SUTTY_TEST_HOST_KEY_SHA256 = $fingerprint
        SUTTY_TEST_REMOTE_ROOT = '/tmp'
        SUTTY_TEST_MODES = 'direct-password-gate'
        SUTTY_TEST_BLACKHOLE_HOST = '127.0.0.1'
        SUTTY_TEST_BLACKHOLE_PORT = $blackholePort.ToString([Globalization.CultureInfo]::InvariantCulture)
        SUTTY_TEST_SERVER_AUDIT_COMMAND = 'sutty-lab-audit-summary'
        SUTTY_TEST_PACKAGE_PATH = $resolvedPackage
        SUTTY_EVIDENCE_OUTPUT_DIR = $resolvedEvidenceRoot
        SUTTY_EVIDENCE_APPROVED = '1'
        SUTTY_EVIDENCE_GATE_ID = 'SSH-LIVE-001'
        SUTTY_EVIDENCE_COMMIT = $Commit
        SUTTY_EVIDENCE_PACKAGE_SHA256 = $packageSha256
        SUTTY_EVIDENCE_SERVER_FAMILY = 'OpenSSH'
        SUTTY_EVIDENCE_SERVER_VERSION = $version
        SUTTY_EVIDENCE_REDACTION_REVIEWED = $(if ($RedactionReviewed) { '1' } else { '0' })
    }
    foreach ($entry in $values.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
    }

    Push-Location $repositoryRoot
    try {
        Invoke-NativeRequired dotnet `
            tests/sutty.LiveServer.SelfTest/bin/x64/Release/net10.0/sutty.LiveServer.SelfTest.dll
    }
    finally {
        Pop-Location
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    $password = $null
    if ($containerStarted) {
        & docker rm --force $containerName | Out-Null
    }
    if (Test-Path -LiteralPath $secretRoot) {
        if (-not $secretRoot.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($secretRoot) -cnotmatch '^sutty-alpha4-openssh-[0-9a-f]{32}$') {
            throw 'Refusing to remove an unverified runtime secret directory.'
        }
        Remove-Item -LiteralPath $secretRoot -Recurse -Force
    }
}
