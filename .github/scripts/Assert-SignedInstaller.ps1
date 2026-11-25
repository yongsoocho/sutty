[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Descriptor,
    [Parameter(Mandatory)][uri]$ReleaseBase,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-XmlStream([IO.Stream]$Stream) {
    $document = New-Object Xml.XmlDocument
    $document.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Stream)
    try { $document.Load($reader) } finally { $reader.Dispose() }
    return ,$document
}

if (-not $ReleaseBase.IsAbsoluteUri -or $ReleaseBase.Scheme -cne 'https' -or
    $ReleaseBase.UserInfo -or $ReleaseBase.Query -or $ReleaseBase.Fragment) {
    throw 'The deployment origin must be an absolute HTTPS directory.'
}
$file = Get-Item -LiteralPath $Package
$appInstaller = Get-Item -LiteralPath $Descriptor
$stream = [IO.File]::OpenRead($appInstaller.FullName)
try { $descriptorXml = Read-XmlStream $stream } finally { $stream.Dispose() }
$container = [IO.Compression.ZipFile]::OpenRead($file.FullName)
try {
    if (-not $container.GetEntry('AppxSignature.p7x')) { throw 'The MSIX has no signature.' }
    $manifest = $container.GetEntry('AppxManifest.xml').Open()
    try { $packageXml = Read-XmlStream $manifest } finally { $manifest.Dispose() }
} finally { $container.Dispose() }

$identity = $packageXml.Package.Identity
$root = $descriptorXml.DocumentElement
$uri = $ReleaseBase.AbsoluteUri.TrimEnd('/')
$ns = New-Object Xml.XmlNamespaceManager($descriptorXml.NameTable)
$ns.AddNamespace('ai', 'http://schemas.microsoft.com/appx/appinstaller/2021')
$entries = $root.SelectNodes('ai:MainPackage', $ns)
if ($root.LocalName -cne 'AppInstaller' -or $root.NamespaceURI -cne $ns.LookupNamespace('ai') -or
    $entries.Count -ne 1 -or $root.Uri -cne "$uri/$($appInstaller.Name)" -or
    $appInstaller.Name -cne "Sutty-$Architecture.appinstaller") { throw 'Invalid App Installer descriptor or self URL.' }
$entry = $entries[0]
if ($identity.Name -cne '7f1b349a-3ed4-42cc-a98d-f68091d3cacf' -or $entry.Name -cne $identity.Name -or
    $identity.Publisher -cne $Publisher -or $entry.Publisher -cne $identity.Publisher -or
    $identity.ProcessorArchitecture -ine $Architecture -or $entry.ProcessorArchitecture -ine $Architecture -or
    $entry.Version -cne $identity.Version -or $file.Name -cne "Sutty-$($identity.Version)-$Architecture.msix" -or
    $entry.Uri -cne "$uri/$($file.Name)") { throw 'App Installer and the actual signed package do not match.' }
Write-Host "App Installer matches the signed $Architecture package and HTTPS deployment directory."
