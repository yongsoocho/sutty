# Run in a disposable Windows user profile. Trust the actual publisher through the normal OS policy.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousPackage,
    [Parameter(Mandatory)][string]$CandidatePackage,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PreviousSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CandidateSha256,
    [Parameter(Mandatory)][string]$ExpectedPublisher,
    [switch]$LaunchWindow,
    [string]$ProfileProbeDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageName = '7f1b349a-3ed4-42cc-a98d-f68091d3cacf'

function Read-VerifiedPackage([string]$Path, [string]$Digest) {
    $file = Get-Item -LiteralPath $Path
    if ($file.Extension -ne '.msix' -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $Digest) {
        throw 'The selected MSIX does not match the expected SHA-256.'
    }
    # Add-AppxPackage enforces the OS trust chain. Never install a certificate here.
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        if (-not $zip.GetEntry('AppxSignature.p7x')) { throw 'Package signature is missing.' }
        $stream = $zip.GetEntry('AppxManifest.xml').Open()
        try {
            $manifest = New-Object Xml.XmlDocument
            $manifest.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($stream)
            try { $manifest.Load($reader) } finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
    $identity = $manifest.Package.Identity
    if ($identity.Name -cne $packageName -or $identity.Publisher -cne $ExpectedPublisher -or
        $identity.ProcessorArchitecture -ne 'x64') { throw 'Unexpected package name, publisher or architecture.' }
    [pscustomobject]@{ Path = $file.FullName; Version = [version]$identity.Version; Hash = $Digest.ToLowerInvariant() }
}

function Assert-Installed([version]$Version) {
    $installed = @(Get-AppxPackage -Name $packageName)
    if ($installed.Count -ne 1 -or $installed[0].Publisher -cne $ExpectedPublisher -or
        $installed[0].Version -ne $Version -or $installed[0].Architecture -ne 'X64') {
        throw 'Windows did not register the expected package identity and version.'
    }
    return $installed[0]
}

function Test-HostShortcut($App) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $automation = [System.Windows.Automation.AutomationElement]
    $scope = [System.Windows.Automation.TreeScope]::Descendants
    $window = $automation::FromHandle($App.MainWindowHandle)
    $search = [System.Windows.Automation.PropertyCondition]::new($automation::AutomationIdProperty, 'SearchBox')
    $hostName = [System.Windows.Automation.PropertyCondition]::new($automation::NameProperty, 'MSIX retention fixture')
    $shell = New-Object -ComObject WScript.Shell
    try {
        if (-not $shell.AppActivate($App.Id)) { throw 'Cannot focus the installed Sutty window.' }
        $shell.SendKeys('%2')
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $box = $window.FindFirst($scope, $search)
            $card = $window.FindFirst($scope, $hostName)
            if ($box -and $card -and -not $box.Current.IsOffscreen -and -not $card.Current.IsOffscreen) {
                Write-Host 'Alt+2 opened the installed Hosts pane and exposed the saved host to UI Automation.'
                return
            }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'The installed Hosts pane did not expose the saved host after Alt+2.'
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

function Test-Window($Package, [switch]$CheckHostShortcut) {
    $exe = Join-Path $Package.InstallLocation 'sutty.UI.exe'
    $app = $null
    $ready = $false
    $start = Get-Date
    Start-Process explorer.exe -ArgumentList ("shell:AppsFolder\" + $Package.PackageFamilyName + '!App')
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        do {
            Start-Sleep -Milliseconds 250
            if (-not $app) {
                $app = Get-Process -Name 'sutty.UI' -ErrorAction SilentlyContinue | Where-Object {
                    $_.StartTime -ge $start -and $_.Path -eq $exe
                } | Select-Object -First 1
                # Explorer owns activation. Retain a handle while the app is alive so
                # Windows PowerShell can still read its exit status after the PID exits.
                if ($app) { [void]$app.get_Handle() }
            }
            if ($app) {
                $app.Refresh()
                if ($app.HasExited) { throw "Sutty exited before showing a window (exit $($app.get_ExitCode()))." }
                $ready = $app.MainWindowHandle -ne [IntPtr]::Zero -and $app.MainWindowTitle -eq 'Sutty'
            }
        } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
        if (-not $ready) { throw 'Sutty did not show its installed native window.' }
        if ($CheckHostShortcut) { Test-HostShortcut $app }
        if (-not $app.CloseMainWindow()) { throw 'Windows rejected the close request for the Sutty window.' }
        if (-not $app.WaitForExit(15000)) { throw 'Sutty was still running 15 seconds after the close request.' }
        $exitCode = $app.get_ExitCode()
        if ($exitCode -ne 0) { throw ('Sutty exited with code 0x{0:X8}.' -f $exitCode) }
        Write-Host "Installed Sutty $($Package.Version) opened its window and exited with code 0."
    } finally {
        if ($app) {
            if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
            $app.Dispose()
        }
    }
}

function Test-SavedProfile($Package, [string]$Phase) {
    if (-not $ProfileProbeDirectory) { return }
    # Run the installed version's store implementations inside its Windows package identity.
    foreach ($name in @('sutty.Command.dll', 'sutty.Core.dll')) {
        Copy-Item -LiteralPath (Join-Path $Package.InstallLocation $name) -Destination (Join-Path $probeRoot $name) -Force
    }
    $receipt = Join-Path $probeRoot 'receipt.json'
    $outcome = Join-Path $probeRoot "$Phase.json"
    $harness = Join-Path $probeRoot 'sutty.Command.SelfTest.dll'
    $arguments = '"{0}" --msix-profile {1} "{2}" "{3}" "{4}"' -f $harness, $Phase, $Package.PackageFamilyName, $receipt, $outcome
    Invoke-CommandInDesktopPackage -PackageFamilyName $Package.PackageFamilyName -AppId App `
        -Command (Get-Command dotnet.exe).Source -Args $arguments -PreventBreakaway
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath $outcome -PathType Leaf)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "Packaged saved-profile check timed out during $Phase." }
        Start-Sleep -Milliseconds 200
    }
    $result = Get-Content -Raw -LiteralPath $outcome | ConvertFrom-Json
    $worker = Get-Process -Id $result.pid -ErrorAction SilentlyContinue
    if ($worker) {
        try { if (-not $worker.WaitForExit(10000)) { throw "Saved-profile check did not exit after $Phase." } }
        finally { $worker.Dispose() }
    }
    if ($result.status -cne 'pass') { throw "Packaged saved-profile check failed during $Phase ($($result.status))." }
    Write-Host "Installed Sutty $($Package.Version) passed the saved host and credential check during $Phase."
}

$previous = Read-VerifiedPackage $PreviousPackage $PreviousSha256
$candidate = Read-VerifiedPackage $CandidatePackage $CandidateSha256
if ($candidate.Version -le $previous.Version) { throw 'The candidate must be newer than the previous package.' }
$data = Join-Path $env:LOCALAPPDATA 'sutty'
if ((Get-AppxPackage -Name $packageName) -or (Test-Path -LiteralPath $data)) {
    throw 'Use a clean test profile. It already contains Sutty or its local data.'
}
$sentinel = Join-Path $data ("canary-" + [Guid]::NewGuid().ToString('N') + '.txt')
$marker = [Guid]::NewGuid().ToString('N')
$probeRoot = $null

try {
    if ($ProfileProbeDirectory) {
        $source = Get-Item -LiteralPath $ProfileProbeDirectory
        if (-not (Test-Path -LiteralPath (Join-Path $source.FullName 'sutty.Command.SelfTest.dll') -PathType Leaf)) {
            throw 'The built x64 saved-profile test harness is required.'
        }
        $probeRoot = Join-Path $source.Parent.FullName ('retention-' + [Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath $source.FullName -Destination $probeRoot -Recurse
    }
    Add-AppxPackage -Path $previous.Path
    $initial = Assert-Installed $previous.Version
    if ($LaunchWindow) { Test-Window $initial }
    Test-SavedProfile $initial 'seed'
    [void][IO.Directory]::CreateDirectory($data)
    [IO.File]::WriteAllText($sentinel, $marker)
    Add-AppxPackage -Path $candidate.Path
    $updated = Assert-Installed $candidate.Version
    if ($LaunchWindow) { Test-Window $updated -CheckHostShortcut:([bool]$ProfileProbeDirectory) }
    Test-SavedProfile $updated 'upgrade'
    if ([IO.File]::ReadAllText($sentinel) -ne $marker) { throw 'Upgrade lost local data.' }
    Add-AppxPackage -Path $previous.Path -ForceUpdateFromAnyVersion
    $rolledBack = Assert-Installed $previous.Version
    if ($LaunchWindow) { Test-Window $rolledBack -CheckHostShortcut:([bool]$ProfileProbeDirectory) }
    Test-SavedProfile $rolledBack 'rollback'
    if ([IO.File]::ReadAllText($sentinel) -ne $marker) { throw 'Rollback lost local data.' }
    Remove-AppxPackage -Package $rolledBack.PackageFullName
    if (Get-AppxPackage -Name $packageName) { throw 'Uninstall left a registered package.' }
    if ([IO.File]::ReadAllText($sentinel) -ne $marker) { throw 'Uninstall unexpectedly removed the external data marker.' }
    [pscustomobject]@{
        PreviousVersion = $previous.Version.ToString(); CandidateVersion = $candidate.Version.ToString()
        PreviousSha256 = $previous.Hash; CandidateSha256 = $candidate.Hash
        Install = 'pass'; Upgrade = 'pass'; Rollback = 'pass'; Uninstall = 'pass'; ExternalDataMarker = 'retained'
        NativeWindow = $(if ($LaunchWindow) { 'opened and closed' } else { 'not tested' })
        SavedHostAndCredential = $(if ($ProfileProbeDirectory) { 'verified after upgrade and rollback' } else { 'not tested' })
    }
} finally {
    if ($probeRoot -and (Test-Path -LiteralPath $probeRoot)) { Remove-Item -LiteralPath $probeRoot -Recurse -Force }
    $left = Get-AppxPackage -Name $packageName
    if ($left -and $left.Publisher -ceq $ExpectedPublisher -and $left.Version -in @($previous.Version, $candidate.Version)) {
        Remove-AppxPackage -Package $left.PackageFullName
    }
    if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
    # Only remove our now-empty directory. Never erase other files created during the run.
    if ((Test-Path -LiteralPath $data) -and -not (Get-ChildItem -LiteralPath $data -Force)) {
        [IO.Directory]::Delete($data)
    }
}
