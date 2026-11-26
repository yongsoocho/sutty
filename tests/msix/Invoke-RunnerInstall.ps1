# Test-only signer and installer exercise on a fresh, disposable GitHub-hosted Windows VM.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This test changes certificate trust for one GitHub-hosted Windows runner only.'
}
$testRoot = Join-Path $env:RUNNER_TEMP ('sutty-msix-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object { $_.Name -as [version] -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $kit) { throw 'The Windows SDK packaging tools are required.' }
$makeAppx = Join-Path $kit.FullName 'x64\makeappx.exe'
$signTool = Join-Path $kit.FullName 'x64\signtool.exe'
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Command)) failed ($LASTEXITCODE)." }
}

$cert = $null
try {
    $output = Join-Path $testRoot 'unsigned'
    Invoke-Checked dotnet @('build', '.\src\sutty.UI\sutty.UI.csproj', '-c', 'Release', '--no-restore',
        '-p:Platform=x64', '-p:RuntimeIdentifier=win-x64', '-p:GenerateAppxPackageOnBuild=true',
        '-p:AppxPackageSigningEnabled=false', '-p:AppxBundle=Never', '-p:UapAppxPackageBuildMode=SideloadOnly',
        "-p:AppxPackageDir=$output\", '-p:PublishReadyToRun=false', '-p:PublishTrimmed=false',
        '-p:DebugSymbols=false', '-p:DebugType=None')
    $packages = @(Get-ChildItem -LiteralPath $output -File -Recurse -Filter '*.msix' |
        Where-Object { $_.DirectoryName -notmatch '\\Dependencies(\\|$)' })
    if ($packages.Count -ne 1) { throw "Expected exactly one Sutty MSIX, found $($packages.Count)." }
    $payload = Join-Path $testRoot 'payload'
    Invoke-Checked $makeAppx @('unpack', '/p', $packages[0].FullName, '/d', $payload, '/o')
    foreach ($name in @('AppxBlockMap.xml', '[Content_Types].xml', 'AppxSignature.p7x')) {
        $path = Join-Path $payload $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }

    $subject = 'CN=Sutty disposable CI ' + [Guid]::NewGuid().ToString('N')
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(1)
    $cer = Join-Path $testRoot 'public.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null

    $manifestPath = Join-Path $payload 'AppxManifest.xml'
    [xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath
    foreach ($version in @('1.0.0.0', '1.0.1.0')) {
        $manifest.Package.Identity.Publisher = $cert.Subject
        $manifest.Package.Identity.Version = $version
        $manifest.Save($manifestPath)
        $out = Join-Path $testRoot ("Sutty-" + $version + "-x64.msix")
        Invoke-Checked $makeAppx @('pack', '/d', $payload, '/p', $out, '/o')
        Invoke-Checked $signTool @('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint, $out)
    }
    $previous = Join-Path $testRoot 'Sutty-1.0.0.0-x64.msix'
    $candidate = Join-Path $testRoot 'Sutty-1.0.1.0-x64.msix'
    # The same check runs on the real signed bundle before it becomes an artifact.
    $base = 'https://example.invalid/sutty'
    $descriptor = Join-Path $testRoot 'Sutty-x64.appinstaller'
    [xml]$xml = @"
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2021" Version="1.0.1.0" Uri="$base/Sutty-x64.appinstaller">
  <MainPackage Name="$($manifest.Package.Identity.Name)" Publisher="$subject" ProcessorArchitecture="x64"
      Version="1.0.1.0" Uri="$base/Sutty-1.0.1.0-x64.msix"/>
</AppInstaller>
"@
    $xml.Save($descriptor)
    $check = Join-Path $PSScriptRoot '..\..\.github\scripts\Assert-SignedInstaller.ps1'
    & $check -Package $candidate -Descriptor $descriptor -ReleaseBase $base -Publisher $subject -Architecture x64
    $xml.AppInstaller.MainPackage.Version = '1.0.0.0'
    $xml.Save($descriptor)
    $mismatchRejected = $false
    try { & $check -Package $candidate -Descriptor $descriptor -ReleaseBase $base -Publisher $subject -Architecture x64 }
    catch { $mismatchRejected = $_.Exception.Message -eq 'App Installer and the actual signed package do not match.' }
    if (-not $mismatchRejected) { throw 'An outdated descriptor must be rejected.' }
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $desktop = @(Get-Process explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sessionId }).Count -gt 0
    if (-not $desktop) { Write-Host 'Native window requires an interactive Windows desktop; running package registration checks.' }
    $profileProbe = Join-Path $testRoot 'profile-probe'
    Invoke-Checked dotnet @('build', '.\tests\sutty.Command.SelfTest\sutty.Command.SelfTest.csproj', '-c', 'Release',
        '--no-restore', '-p:Platform=x64', '--output', $profileProbe)
    & "$PSScriptRoot\Test-SignedInstall.ps1" -PreviousPackage $previous -CandidatePackage $candidate `
        -PreviousSha256 (Get-FileHash -LiteralPath $previous).Hash -CandidateSha256 (Get-FileHash -LiteralPath $candidate).Hash `
        -ExpectedPublisher $cert.Subject -LaunchWindow:$desktop -ProfileProbeDirectory $profileProbe | Format-List
} finally {
    if ($cert) {
        foreach ($store in @('Cert:\LocalMachine\TrustedPeople', 'Cert:\CurrentUser\My')) {
            $entry = Join-Path $store $cert.Thumbprint
            if (Test-Path -LiteralPath $entry) {
                if ($store -eq 'Cert:\CurrentUser\My') { Remove-Item -LiteralPath $entry -DeleteKey }
                else { Remove-Item -LiteralPath $entry }
            }
        }
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
