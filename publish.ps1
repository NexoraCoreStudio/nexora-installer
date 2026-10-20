# Nexora 1.1 - publication de l'application et de l'installateur Avalonia autonome.
# -MakeInstaller produit dist\Nexora-Setup.exe sans dependance Inno Setup.
# -UseInnoLegacy conserve la procedure Inno Setup historique si necessaire.
[CmdletBinding()]
param(
    [switch]$MakeInstaller,
    [switch]$UseInnoLegacy
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\nexora')).Path
$appProject = Join-Path $root 'Nexora.Gui\Nexora.Gui.csproj'
$installerProject = Join-Path $PSScriptRoot 'Nexora.Installer\Nexora.Installer.csproj'
$dist = Join-Path $root 'dist'
$appOutput = Join-Path $dist 'Nexora'

if (!(Test-Path -LiteralPath $appProject)) { throw 'Nexora.Gui.csproj est introuvable.' }
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'SDK .NET 8 requis.' }
if ($MakeInstaller -and $UseInnoLegacy) { throw 'Choisir -MakeInstaller ou -UseInnoLegacy, pas les deux.' }
if (Get-Process -Name Nexora -ErrorAction SilentlyContinue) {
    throw "Quittez Nexora depuis la zone de notification avant de le publier."
}

# A fresh staging directory prevents stale files, user configuration or keys from
# accidentally being embedded in Nexora-Setup.exe.
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('NexoraBuild-' + [Guid]::NewGuid().ToString('N'))
$cleanPublish = Join-Path $tempRoot 'App'
$setupOutput = Join-Path $tempRoot 'Setup'
New-Item -ItemType Directory -Force -Path $cleanPublish, $setupOutput, $dist | Out-Null

try {
    Write-Host 'Publication Nexora Windows x64...' -ForegroundColor Cyan
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:Version=1.1.0 -o $cleanPublish
    if ($LASTEXITCODE -ne 0) { throw 'Echec de la publication Nexora.' }
    if (!(Test-Path -LiteralPath (Join-Path $cleanPublish 'Nexora.exe'))) {
        throw 'Nexora.exe est manquant dans les fichiers publies.'
    }

    # Preserve backwards-compatible dist\Nexora artifacts without clearing this folder.
    New-Item -ItemType Directory -Force -Path $appOutput | Out-Null
    Copy-Item -Path (Join-Path $cleanPublish '*') -Destination $appOutput -Recurse -Force
    Write-Host "Nexora publie : $appOutput" -ForegroundColor Green

    if ($UseInnoLegacy) {
        $innoLocations = @()
        if (${env:ProgramFiles(x86)}) { $innoLocations += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe') }
        if ($env:ProgramFiles) { $innoLocations += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe') }
        $iscc = $innoLocations | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (!$iscc) { throw 'Inno Setup 6 introuvable.' }
        & $iscc '/FNexora-Setup-Legacy' (Join-Path $PSScriptRoot 'installer\Nexora.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Echec Inno Setup.' }
        return
    }
    if (!$MakeInstaller) { return }
    if (!(Test-Path -LiteralPath $installerProject)) {
        throw "Le projet d'installateur Avalonia est introuvable : $installerProject"
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $manifestName = 'NexoraPayload.manifest.json'
    $manifestPath = Join-Path $tempRoot $manifestName
    $hashes = [ordered]@{}
    $publishedFiles = @(Get-ChildItem -LiteralPath $cleanPublish -File -Recurse | Sort-Object FullName)
    foreach ($file in $publishedFiles) {
        $relative = $file.FullName.Substring($cleanPublish.Length + 1).Replace('\', '/')
        if ($relative -eq $manifestName) { throw 'Conflit avec le fichier de manifeste du Setup.' }
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    @{ Files = $hashes } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    $resourceDir = Join-Path $PSScriptRoot 'Nexora.Installer\Resources'
    New-Item -ItemType Directory -Force -Path $resourceDir | Out-Null
    $payloadZip = Join-Path $resourceDir 'NexoraPayload.zip'
    if (Test-Path -LiteralPath $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }

    # Create a zip containing files only (no extra directory entries).
    $stream = [IO.File]::Open($payloadZip, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $publishedFiles) {
                $relative = $file.FullName.Substring($cleanPublish.Length + 1).Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $manifestPath, $manifestName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }

    Write-Host 'Compilation de Nexora-Setup.exe (Avalonia)...' -ForegroundColor Cyan
    & dotnet publish $installerProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false -p:Version=1.1.0 -o $setupOutput
    if ($LASTEXITCODE -ne 0) { throw 'Echec de compilation de Nexora-Setup.exe.' }

    $setup = Join-Path $setupOutput 'Nexora-Setup.exe'
    if (!(Test-Path -LiteralPath $setup)) { throw 'Nexora-Setup.exe manquant.' }
    $dest = Join-Path $dist 'Nexora-Setup.exe'
    Copy-Item -LiteralPath $setup -Destination $dest -Force
    Write-Host "Installateur personnalise : $dest" -ForegroundColor Green
    Write-Host 'Ce setup ne modifie pas USB/IP et ne necessite pas Inno Setup.' -ForegroundColor Cyan
}
finally {
    # Avoid keeping a second huge embedded copy of the published application in sources.
    $resourceZip = Join-Path $PSScriptRoot 'Nexora.Installer\Resources\NexoraPayload.zip'
    if (Test-Path -LiteralPath $resourceZip) { Remove-Item -LiteralPath $resourceZip -Force }
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
