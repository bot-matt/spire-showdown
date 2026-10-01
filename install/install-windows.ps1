[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Iso,
    [string]$SlippiBundle
)

$ErrorActionPreference = 'Stop'

function Find-Sts2Game {
    $roots = [System.Collections.Generic.List[string]]::new()
    $steamPath = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if ($steamPath) { $roots.Add((Join-Path $steamPath 'steamapps')) }
    $defaultSteam = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps'
    if ($defaultSteam) { $roots.Add($defaultSteam) }

    foreach ($root in $roots | Select-Object -Unique) {
        $candidate = Join-Path $root 'common\Slay the Spire 2'
        if (Test-Path (Join-Path $candidate 'SlayTheSpire2.exe')) { return $candidate }
        $libraries = Join-Path $root 'libraryfolders.vdf'
        if (Test-Path $libraries) {
            foreach ($line in Get-Content $libraries) {
                if ($line -match '"path"\s+"([^"]+)"') {
                    $library = $Matches[1] -replace '\\\\', '\'
                    $candidate = Join-Path $library 'steamapps\common\Slay the Spire 2'
                    if (Test-Path (Join-Path $candidate 'SlayTheSpire2.exe')) { return $candidate }
                }
            }
        }
    }
    return $null
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $GameDir) { $GameDir = Find-Sts2Game }
if (-not $GameDir) { $GameDir = Read-Host 'Paste the Slay the Spire 2 game folder' }
if (-not (Test-Path (Join-Path $GameDir 'SlayTheSpire2.exe'))) {
    throw "Game executable not found in $GameDir"
}

$payloadCandidates = @(
    (Join-Path $ScriptDir 'SpireShowdown'),
    (Join-Path (Split-Path -Parent $ScriptDir) 'SpireShowdown')
)
$PayloadDir = $payloadCandidates | Where-Object {
    (Test-Path (Join-Path $_ 'SpireShowdown.dll')) -and
    (Test-Path (Join-Path $_ 'spire-showdown-bridge.exe'))
} | Select-Object -First 1
if (-not $PayloadDir) { throw 'Run this script from the extracted Windows mod artifact.' }

if (-not $SlippiBundle) {
    $SlippiBundle = Get-ChildItem (Join-Path $HOME 'Downloads') -Filter 'spire-showdown-slippi-windows-x86_64*.zip' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $SlippiBundle) {
    $SlippiBundle = Join-Path $HOME 'Downloads\spire-showdown-slippi-windows-x86_64.zip'
    Write-Host 'Downloading the public patched Slippi release...'
    try {
        Invoke-WebRequest -Uri 'https://github.com/bot-matt/spire-showdown/releases/latest/download/spire-showdown-slippi-windows-x86_64.zip' -OutFile $SlippiBundle
    } catch {
        $SlippiBundle = $null
    }
}
if (-not $SlippiBundle) { $SlippiBundle = Read-Host 'Paste the patched Windows Slippi artifact ZIP' }
if (-not (Test-Path $SlippiBundle)) { throw "Slippi artifact not found: $SlippiBundle" }

if (-not $Iso) {
    $settingsPath = Join-Path $env:APPDATA 'Slippi Launcher\Settings'
    if (Test-Path $settingsPath) {
        try { $Iso = (Get-Content $settingsPath -Raw | ConvertFrom-Json).settings.isoPath } catch {}
    }
}
if (-not $Iso -or -not (Test-Path $Iso)) {
    $candidate = Join-Path $HOME 'Downloads\Melee.iso'
    if (Test-Path $candidate) { $Iso = $candidate }
}
if (-not $Iso -or -not (Test-Path $Iso)) { $Iso = Read-Host 'Paste the path to your legally dumped Melee ISO' }
if (-not (Test-Path $Iso)) { throw "Melee ISO not found: $Iso" }

$InstallDir = Join-Path $GameDir 'mods\SpireShowdown'
Write-Host "Installing mod to $InstallDir"
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $PayloadDir 'SpireShowdown.dll') $InstallDir -Force
Copy-Item (Join-Path $PayloadDir 'SpireShowdown.json') $InstallDir -Force
Copy-Item (Join-Path $PayloadDir 'spire-showdown-bridge.exe') $InstallDir -Force

$tempDir = Join-Path ([IO.Path]::GetTempPath()) ("spire-showdown-" + [guid]::NewGuid())
New-Item -ItemType Directory $tempDir | Out-Null
try {
    Expand-Archive -Path $SlippiBundle -DestinationPath $tempDir -Force
    $innerZip = Get-ChildItem $tempDir -Recurse -Filter '*.zip' -File | Select-Object -First 1
    $slippiDir = Join-Path $InstallDir 'Slippi'
    if ($innerZip) {
        New-Item -ItemType Directory -Force $slippiDir | Out-Null
        Expand-Archive -Path $innerZip.FullName -DestinationPath $slippiDir -Force
    } else {
        Copy-Item (Join-Path $tempDir '*') $slippiDir -Recurse -Force
    }
} finally {
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

$SlippiExe = Get-ChildItem (Join-Path $InstallDir 'Slippi') -Recurse -File |
    Where-Object { $_.Name -match '(Slippi.*Dolphin|Dolphin).*\.exe$' -and $_.Name -notmatch 'Updater' } |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $SlippiExe) { throw 'Could not find Dolphin.exe in the patched Slippi artifact.' }

$configDir = Join-Path $env:APPDATA 'SlayTheSpire2'
$configPath = Join-Path $configDir 'spire-showdown.json'
New-Item -ItemType Directory -Force $configDir | Out-Null
$config = @{}
if (Test-Path $configPath) {
    try {
        $old = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($old.connect_code) { $config.connect_code = $old.connect_code }
    } catch {}
}
if (-not $config.ContainsKey('connect_code')) { $config.connect_code = $null }
$config.slippi_path = $SlippiExe
$config.melee_iso_path = (Resolve-Path $Iso).Path
$config | ConvertTo-Json | Set-Content $configPath -Encoding utf8

$steamapps = Split-Path (Split-Path $GameDir -Parent) -Parent
$baseLib = Join-Path $steamapps 'workshop\content\2868840\3737335127\BaseLib\BaseLib.dll'
if (-not (Test-Path $baseLib)) {
    Write-Warning 'BaseLib is not installed. Subscribe to Workshop item 3737335127 before launching.'
}

Write-Host 'Running bridge preflight...'
& (Join-Path $InstallDir 'spire-showdown-bridge.exe') doctor --slippi $SlippiExe --iso $Iso
if ($LASTEXITCODE -ne 0) { throw "Bridge preflight failed with exit code $LASTEXITCODE" }

Write-Host ''
Write-Host 'Installation passed preflight.' -ForegroundColor Green
Write-Host 'Use the same StS2 beta branch and enabled mod list on every test computer.'
