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
    [string]$EvidenceOutputRoot
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

function Test-SafeZipEntryName {
    param([Parameter(Mandatory)][string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 512 -or
        $Name.StartsWith('/', [StringComparison]::Ordinal) -or
        $Name.Contains('\', [StringComparison]::Ordinal) -or
        $Name.Contains(':', [StringComparison]::Ordinal) -or
        $Name.IndexOfAny([char[]](0..31 + 127)) -ge 0) {
        return $false
    }

    $path = if ($Name.EndsWith('/', [StringComparison]::Ordinal)) {
        $Name.Substring(0, $Name.Length - 1)
    }
    else {
        $Name
    }
    if ([string]::IsNullOrEmpty($path)) {
        return $false
    }

    $reservedNames = 'CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]'
    foreach ($segment in $path.Split('/')) {
        if ($segment.Length -lt 1 -or $segment.Length -gt 128 -or
            $segment -cin @('.', '..') -or
            $segment.EndsWith(' ', [StringComparison]::Ordinal) -or
            $segment.EndsWith('.', [StringComparison]::Ordinal) -or
            $segment.IndexOfAny([char[]]'<>"|?*') -ge 0 -or
            $segment.Split('.')[0] -cmatch "^(?:$reservedNames)$") {
            return $false
        }
    }
    return $true
}

function Copy-PhysicalDirectory {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $sourceItem = Get-Item -LiteralPath $Source -Force
    if (-not $sourceItem.PSIsContainer -or
        ($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The harness build-output root must be a physical directory.'
    }
    $resolvedSource = (Resolve-Path -LiteralPath $sourceItem.FullName).Path
    $resolvedDestination = [IO.Path]::GetFullPath($Destination)
    if (Test-Path -LiteralPath $resolvedDestination) {
        throw 'The verified staging directory must not already exist.'
    }
    [IO.Directory]::CreateDirectory($resolvedDestination) | Out-Null
    $destinationItem = Get-Item -LiteralPath $resolvedDestination -Force
    if (($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The verified staging directory must be physical.'
    }
    $destinationPrefix = $resolvedDestination.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    foreach ($item in Get-ChildItem -LiteralPath $resolvedSource -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The built harness output must not contain reparse points.'
        }
        $relative = [IO.Path]::GetRelativePath($resolvedSource, $item.FullName)
        $target = [IO.Path]::GetFullPath((Join-Path $resolvedDestination $relative))
        if (-not $target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A built harness path escaped the verified staging directory.'
        }
        if ($item.PSIsContainer) {
            [IO.Directory]::CreateDirectory($target) | Out-Null
        }
        else {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.File]::Copy($item.FullName, $target, $false)
        }
    }
}

function Stage-CandidateHarness {
    param(
        [Parameter(Mandatory)][string]$HarnessOutput,
        [Parameter(Mandatory)][string]$CandidatePackage,
        [Parameter(Mandatory)][string]$StageRoot
    )

    Copy-PhysicalDirectory -Source $HarnessOutput -Destination $StageRoot
    $stagedHarness = Join-Path $StageRoot 'sutty.LiveServer.SelfTest.dll'
    if (-not (Test-Path -LiteralPath $stagedHarness -PathType Leaf)) {
        throw 'The staged harness entry assembly is missing.'
    }
    $harnessHash = (Get-FileHash -LiteralPath $stagedHarness -Algorithm SHA256).Hash

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($CandidatePackage)
    try {
        if ($archive.Entries.Count -lt 1 -or $archive.Entries.Count -gt 10000) {
            throw 'The candidate ZIP entry count is outside the review boundary.'
        }
        $allNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        $rootDlls = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            if (-not (Test-SafeZipEntryName -Name $entry.FullName) -or
                -not $allNames.Add($entry.FullName)) {
                throw 'The candidate ZIP contains an unsafe or duplicate entry.'
            }
            $unixFileType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            $dosAttributes = $entry.ExternalAttributes -band 0xFFFF
            if ($unixFileType -eq 0xA000 -or
                ($dosAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'The candidate ZIP contains a symbolic-link or reparse-point entry.'
            }
            if ($entry.FullName.IndexOf('/') -lt 0 -and
                $entry.FullName.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
                if ($entry.Length -le 0 -or $entry.Length -gt 536870912) {
                    throw 'A candidate root DLL is outside the bounded size policy.'
                }
                $rootDlls.Add($entry.FullName, $entry)
            }
        }
        if ($rootDlls.ContainsKey('sutty.LiveServer.SelfTest.dll')) {
            throw 'The candidate ZIP must not replace the commit-owned test harness assembly.'
        }

        $stagedDlls = @(Get-ChildItem -LiteralPath $StageRoot -File -Filter '*.dll')
        $stagedNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($stagedDll in $stagedDlls) {
            if (-not $stagedNames.Add($stagedDll.Name)) {
                throw 'The staged harness contains duplicate case-insensitive root DLL names.'
            }
        }

        $replacedNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($stagedDll in $stagedDlls) {
            if ($stagedDll.Name -ieq 'sutty.LiveServer.SelfTest.dll') {
                continue
            }
            $packageEntry = $null
            if (-not $rootDlls.TryGetValue($stagedDll.Name, [ref]$packageEntry)) {
                throw 'A staged root runtime DLL is absent from the exact candidate ZIP.'
            }
            $temporaryTarget = Join-Path $StageRoot (
                ".candidate-dll-$([Guid]::NewGuid().ToString('N')).tmp")
            try {
                $source = $packageEntry.Open()
                try {
                    $destination = [IO.FileStream]::new(
                        $temporaryTarget,
                        [IO.FileMode]::CreateNew,
                        [IO.FileAccess]::Write,
                        [IO.FileShare]::None)
                    try {
                        $source.CopyTo($destination)
                        $destination.Flush($true)
                    }
                    finally {
                        $destination.Dispose()
                    }
                }
                finally {
                    $source.Dispose()
                }
                [IO.File]::Move($temporaryTarget, $stagedDll.FullName, $true)
            }
            finally {
                if (Test-Path -LiteralPath $temporaryTarget) {
                    Remove-Item -LiteralPath $temporaryTarget -Force
                }
            }
            $stagedHash = (Get-FileHash -LiteralPath $stagedDll.FullName -Algorithm SHA256).Hash
            $entryStream = $packageEntry.Open()
            try {
                $entryHash = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData($entryStream))
            }
            finally {
                $entryStream.Dispose()
            }
            if ($stagedHash -cne $entryHash -or -not $replacedNames.Add($stagedDll.Name)) {
                throw 'A staged runtime DLL is not byte-identical to its candidate ZIP entry.'
            }
        }
        if ($replacedNames.Count -ne ($stagedNames.Count - 1)) {
            throw 'The exact candidate did not replace every staged root runtime DLL.'
        }

        $requiredRuntimeDlls = @(
            'sutty.Core.dll',
            'sutty.SshAgent.dll',
            'Renci.SshNet.dll',
            'BouncyCastle.Cryptography.dll',
            'Microsoft.Extensions.Logging.Abstractions.dll'
        )
        foreach ($requiredName in $requiredRuntimeDlls) {
            if (-not $stagedNames.Contains($requiredName) -or
                -not $replacedNames.Contains($requiredName)) {
                throw 'A required SSH runtime DLL was not supplied by the exact candidate ZIP.'
            }
        }
        if (-not $replacedNames.Contains('sutty.Core.dll')) {
            throw 'The exact candidate sutty.Core.dll was not staged.'
        }
        $stagedHarnessHash = (Get-FileHash -LiteralPath $stagedHarness -Algorithm SHA256).Hash
        if ($stagedHarnessHash -cne $harnessHash) {
            throw 'The commit-owned test harness assembly changed during candidate staging.'
        }
    }
    finally {
        $archive.Dispose()
    }
    return $stagedHarness
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
$artifactsRoot = Join-Path $secretRoot 'artifacts'
$buildRoot = Join-Path $secretRoot 'build'
$stageRoot = Join-Path $secretRoot 'harness'
$containerName = "sutty-alpha4-gate-$runIdentifier"
$containerStarted = $false
$password = $null
$stagedHarnessPath = $null
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
    'SUTTY_EVIDENCE_SERVER_VERSION'
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
    if (Test-Path -LiteralPath $secretRoot) {
        throw 'The runtime root must be fresh before the formal gate starts.'
    }
    [IO.Directory]::CreateDirectory($secretRoot) | Out-Null
    $secretRootItem = Get-Item -LiteralPath $secretRoot -Force
    if (($secretRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The runtime root must be a physical directory.'
    }

    Push-Location $repositoryRoot
    try {
        Invoke-NativeRequired dotnet restore `
            tests/sutty.LiveServer.SelfTest/sutty.LiveServer.SelfTest.csproj `
            --locked-mode `
            --artifacts-path $artifactsRoot `
            '-p:Platform=x64'
        $artifactsRootItem = Get-Item -LiteralPath $artifactsRoot -Force
        if (-not $artifactsRootItem.PSIsContainer -or
            ($artifactsRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The fresh restore artifacts root must be a physical directory.'
        }
        foreach ($projectName in @(
                     'sutty.LiveServer.SelfTest',
                     'sutty.Core',
                     'sutty.SshAgent')) {
            $assetsPath = Join-Path $artifactsRoot "obj\$projectName\project.assets.json"
            if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
                throw 'The locked restore did not create every required fresh assets file.'
            }
        }
        Invoke-NativeRequired dotnet build `
            tests/sutty.LiveServer.SelfTest/sutty.LiveServer.SelfTest.csproj `
            --configuration Release `
            --no-restore `
            --no-incremental `
            --artifacts-path $artifactsRoot `
            --output $buildRoot `
            '-p:Platform=x64'
    }
    finally {
        Pop-Location
    }

    $packageSha256 = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $stagedHarnessPath = Stage-CandidateHarness `
        -HarnessOutput $buildRoot `
        -CandidatePackage $resolvedPackage `
        -StageRoot $stageRoot
    [IO.File]::Copy(
        (Join-Path $repositoryRoot 'tests\sutty.LiveServer.SelfTest\Program.cs'),
        (Join-Path $stageRoot 'Program.cs'),
        $false)
    [IO.File]::Copy(
        (Join-Path $repositoryRoot `
            'tests\sutty.LiveServer.SelfTest\sutty.LiveServer.SelfTest.csproj'),
        (Join-Path $stageRoot 'sutty.LiveServer.SelfTest.csproj'),
        $false)

    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    [IO.File]::WriteAllText($secretPath, $password, [Text.UTF8Encoding]::new($false))

    Push-Location $repositoryRoot
    try {
        Invoke-NativeRequired docker build --tag sutty-alpha4-openssh-lab:local `
            tests/live-server/openssh
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
    }
    foreach ($entry in $values.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
    }

    Push-Location $repositoryRoot
    try {
        Invoke-NativeRequired dotnet $stagedHarnessPath
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
        $secretRootItem = Get-Item -LiteralPath $secretRoot -Force
        if (($secretRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Refusing to remove a reparse-point runtime secret directory.'
        }
        Remove-Item -LiteralPath $secretRoot -Recurse -Force
    }
}
