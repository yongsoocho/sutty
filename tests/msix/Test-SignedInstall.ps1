# Run in a disposable Windows user profile. Trust the actual publisher through the normal OS policy.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousPackage,
    [Parameter(Mandatory)][string]$CandidatePackage,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PreviousSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CandidateSha256,
    [Parameter(Mandatory)][string]$ExpectedPublisher,
    [switch]$LaunchWindow
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

function Test-Window($Package) {
    $exe = Join-Path $Package.InstallLocation 'sutty.UI.exe'
    $app = $null
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
            }
            if ($app) {
                $app.Refresh()
                if ($app.HasExited) { throw 'Sutty exited before showing a window.' }
            }
        } while ((-not $app -or $app.MainWindowHandle -eq [IntPtr]::Zero) -and [DateTime]::UtcNow -lt $deadline)
        if (-not $app -or $app.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Sutty did not show its installed native window.' }
        if (-not $app.CloseMainWindow() -or -not $app.WaitForExit(15000) -or $app.ExitCode -ne 0) {
            throw 'The Sutty window did not close cleanly.'
        }
    } finally {
        if ($app) {
            if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
            $app.Dispose()
        }
    }
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

try {
    Add-AppxPackage -Path $previous.Path
    $initial = Assert-Installed $previous.Version
    if ($LaunchWindow) { Test-Window $initial }
    [void][IO.Directory]::CreateDirectory($data)
    [IO.File]::WriteAllText($sentinel, $marker)
    Add-AppxPackage -Path $candidate.Path
    $updated = Assert-Installed $candidate.Version
    if ($LaunchWindow) { Test-Window $updated }
    if ([IO.File]::ReadAllText($sentinel) -ne $marker) { throw 'Upgrade lost local data.' }
    Add-AppxPackage -Path $previous.Path -ForceUpdateFromAnyVersion
    $rolledBack = Assert-Installed $previous.Version
    if ($LaunchWindow) { Test-Window $rolledBack }
    if ([IO.File]::ReadAllText($sentinel) -ne $marker) { throw 'Rollback lost local data.' }
    Remove-AppxPackage -Package $rolledBack.PackageFullName
    if (Get-AppxPackage -Name $packageName) { throw 'Uninstall left a registered package.' }
    if ([IO.File]::ReadAllText($sentinel) -ne $marker) { throw 'Uninstall unexpectedly removed the external data marker.' }
    [pscustomobject]@{
        PreviousVersion = $previous.Version.ToString(); CandidateVersion = $candidate.Version.ToString()
        PreviousSha256 = $previous.Hash; CandidateSha256 = $candidate.Hash
        Install = 'pass'; Upgrade = 'pass'; Rollback = 'pass'; Uninstall = 'pass'; ExternalDataMarker = 'retained'
        NativeWindow = $(if ($LaunchWindow) { 'opened and closed' } else { 'not tested' })
    }
} finally {
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
