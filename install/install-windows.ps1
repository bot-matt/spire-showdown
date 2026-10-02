[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Iso,
    [string]$SlippiBundle,
    [string]$ConnectCode
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

function Test-ConnectCode([string]$Code) {
    return -not [string]::IsNullOrWhiteSpace($Code) -and
        $Code.Length -le 9 -and $Code.Contains('#')
}

function Find-SlippiConnectCode {
    $paths = @(
        (Join-Path $env:APPDATA 'Slippi Launcher\netplay\Slippi\user.json'),
        (Join-Path $env:APPDATA 'Slippi Launcher\netplay-beta\Slippi\user.json'),
        (Join-Path $env:APPDATA 'SlippiOnline\Slippi\user.json'),
        (Join-Path $env:APPDATA 'slippi-dolphin\netplay\Slippi\user.json'),
        (Join-Path $env:APPDATA 'slippi-dolphin\netplay-beta\Slippi\user.json')
    )
    foreach ($path in $paths) {
        if (-not (Test-Path $path)) { continue }
        try {
            $code = (Get-Content $path -Raw | ConvertFrom-Json).connectCode
            if (Test-ConnectCode $code) { return $code.Trim() }
        } catch {}
    }
    return $null
}

if (-not $GameDir) { $GameDir = Find-Sts2Game }
if (-not $GameDir) { $GameDir = Read-Host 'Paste the Slay the Spire 2 game folder' }
if (-not (Test-Path (Join-Path $GameDir 'SlayTheSpire2.exe'))) {
    throw "Game executable not found in $GameDir"
}
if (Get-Process -Name 'SlayTheSpire2','spire-showdown-bridge' -ErrorAction SilentlyContinue) {
    throw 'Close Slay the Spire 2 and any Spire Showdown bridge process before running the installer.'
}

$steamapps = Split-Path (Split-Path $GameDir -Parent) -Parent
$baseLib = Join-Path $steamapps 'workshop\content\2868840\3737335127\BaseLib\BaseLib.dll'
if (-not (Test-Path $baseLib)) {
    throw @"
BaseLib is not installed in this Steam library.
Subscribe to Workshop item 3737335127, let Steam finish downloading it, then run this installer again:
https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127
"@
}

$payloadTempDir = Join-Path ([IO.Path]::GetTempPath()) ("spire-showdown-payload-" + [guid]::NewGuid())
New-Item -ItemType Directory $payloadTempDir | Out-Null
$payloadBundle = Join-Path $payloadTempDir 'mod.zip'
$payloadRoot = Join-Path $payloadTempDir 'mod'
Write-Host 'Downloading the latest Spire Showdown Windows mod payload...'
Invoke-WebRequest `
    -Uri 'https://github.com/bot-matt/spire-showdown/releases/latest/download/spire-showdown-mod-Windows-x86_64.zip' `
    -Headers @{ 'Cache-Control' = 'no-cache' } `
    -OutFile $payloadBundle
Expand-Archive -Path $payloadBundle -DestinationPath $payloadRoot -Force
$PayloadDir = Join-Path $payloadRoot 'SpireShowdown'
if (-not (Test-Path (Join-Path $PayloadDir 'SpireShowdown.dll')) -or
    -not (Test-Path (Join-Path $PayloadDir 'spire-showdown-bridge.exe'))) {
    throw 'Latest Windows mod bundle is incomplete.'
}

if (-not $SlippiBundle) {
    $SlippiBundle = Join-Path $HOME 'Downloads\spire-showdown-slippi-windows-x86_64.zip'
    New-Item -ItemType Directory -Force (Split-Path -Parent $SlippiBundle) | Out-Null
    Write-Host 'Downloading the latest public patched Slippi release (replacing any cached copy)...'
    try {
        Invoke-WebRequest `
            -Uri 'https://github.com/bot-matt/spire-showdown/releases/latest/download/spire-showdown-slippi-windows-x86_64.zip' `
            -Headers @{ 'Cache-Control' = 'no-cache' } `
            -OutFile $SlippiBundle
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
foreach ($name in @('SpireShowdown.dll', 'SpireShowdown.json', 'spire-showdown-bridge.exe')) {
    $sourceHash = (Get-FileHash (Join-Path $PayloadDir $name) -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash (Join-Path $InstallDir $name) -Algorithm SHA256).Hash
    if ($sourceHash -ne $installedHash) { throw "Installed $name does not match the release payload." }
}

$tempDir = Join-Path ([IO.Path]::GetTempPath()) ("spire-showdown-" + [guid]::NewGuid())
New-Item -ItemType Directory $tempDir | Out-Null
try {
    Expand-Archive -Path $SlippiBundle -DestinationPath $tempDir -Force
    $innerZip = Get-ChildItem $tempDir -Recurse -Filter '*.zip' -File | Select-Object -First 1
    $slippiDir = Join-Path $InstallDir 'Slippi'
    if (Test-Path $slippiDir) { Remove-Item $slippiDir -Recurse -Force }
    New-Item -ItemType Directory -Force $slippiDir | Out-Null
    if ($innerZip) {
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

Write-Host 'Configuring the active XInput/Steam gamepad for Player 1...'
$slippiUserConfig = Join-Path (Split-Path $SlippiExe -Parent) 'User\Config'
New-Item -ItemType Directory -Force $slippiUserConfig | Out-Null
$gcPadConfig = @'
[GCPad1]
Device = XInput/0/Gamepad
Buttons/A = `Button A`
Buttons/B = `Button B`
Buttons/X = `Button X`
Buttons/Y = `Button Y`
Buttons/Z = `Shoulder R`
Buttons/Start = `Start`
Main Stick/Up = `Left Y+`
Main Stick/Down = `Left Y-`
Main Stick/Left = `Left X-`
Main Stick/Right = `Left X+`
C-Stick/Up = `Right Y+`
C-Stick/Down = `Right Y-`
C-Stick/Left = `Right X-`
C-Stick/Right = `Right X+`
Triggers/L = `Trigger L`
Triggers/R = `Trigger R`
D-Pad/Up = `Pad N`
D-Pad/Down = `Pad S`
D-Pad/Left = `Pad W`
D-Pad/Right = `Pad E`
Rumble/Motor = `Motor L` | `Motor R`
[GCPad2]
[GCPad3]
[GCPad4]
'@
[IO.File]::WriteAllText((Join-Path $slippiUserConfig 'GCPadNew.ini'), $gcPadConfig, [Text.UTF8Encoding]::new($false))
$dolphinIni = Join-Path $slippiUserConfig 'Dolphin.ini'
$dolphinText = if (Test-Path $dolphinIni) { Get-Content $dolphinIni -Raw } else { '' }
if ($dolphinText -notmatch '(?m)^\[Core\]\s*$') { $dolphinText += "`r`n[Core]`r`n" }
foreach ($setting in @{'SIDevice0'='6'; 'SIDevice1'='0'; 'SIDevice2'='0'; 'SIDevice3'='0'}.GetEnumerator()) {
    if ($dolphinText -match "(?m)^$($setting.Key)\s*=") {
        $dolphinText = [regex]::Replace($dolphinText, "(?m)^$($setting.Key)\s*=.*$", "$($setting.Key) = $($setting.Value)")
    } else {
        $dolphinText = $dolphinText -replace '(?m)^\[Core\]\s*$', "[Core]`r`n$($setting.Key) = $($setting.Value)"
    }
}
if ($dolphinText -notmatch '(?m)^\[Input\]\s*$') { $dolphinText += "`r`n[Input]`r`n" }
if ($dolphinText -match '(?m)^BackgroundInput\s*=') {
    $dolphinText = [regex]::Replace($dolphinText, '(?m)^BackgroundInput\s*=.*$', 'BackgroundInput = True')
} else {
    $dolphinText = $dolphinText -replace '(?m)^\[Input\]\s*$', "[Input]`r`nBackgroundInput = True"
}
[IO.File]::WriteAllText($dolphinIni, $dolphinText, [Text.UTF8Encoding]::new($false))

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
if (-not (Test-ConnectCode $ConnectCode) -and $config.ContainsKey('connect_code')) {
    $ConnectCode = $config.connect_code
}
if (-not (Test-ConnectCode $ConnectCode)) { $ConnectCode = Find-SlippiConnectCode }
if (-not (Test-ConnectCode $ConnectCode)) {
    $ConnectCode = Read-Host 'Enter your Slippi connect code (for example NAME#123)'
}
if (-not (Test-ConnectCode $ConnectCode)) {
    throw 'A valid Slippi connect code is required. Sign into Slippi Launcher, then rerun this installer.'
}
$config.connect_code = $ConnectCode.Trim()
$config.slippi_path = $SlippiExe
$config.melee_iso_path = (Resolve-Path $Iso).Path
$encodedConfig = $config | ConvertTo-Json
[IO.File]::WriteAllText($configPath, $encodedConfig, [Text.UTF8Encoding]::new($false))
$verifiedConfig = Get-Content $configPath -Raw | ConvertFrom-Json
if (-not (Test-ConnectCode $verifiedConfig.connect_code)) {
    throw "Generated config did not retain a valid connect code: $configPath"
}
foreach ($required in @(
    (Join-Path $InstallDir 'SpireShowdown.dll'),
    (Join-Path $InstallDir 'SpireShowdown.json'),
    (Join-Path $InstallDir 'spire-showdown-bridge.exe'),
    $verifiedConfig.slippi_path,
    $verifiedConfig.melee_iso_path
)) {
    if (-not (Test-Path $required)) { throw "Post-install validation failed; missing $required" }
}

Write-Host 'Running bridge preflight...'
$doctorOutput = & (Join-Path $InstallDir 'spire-showdown-bridge.exe') doctor --slippi $SlippiExe --iso $Iso
$doctorExit = $LASTEXITCODE
$doctorJson = $doctorOutput -join [Environment]::NewLine
$doctorJson | Write-Host
if ($doctorExit -ne 0) { throw "Bridge preflight failed with exit code $doctorExit" }
try { $doctor = $doctorJson | ConvertFrom-Json } catch { throw 'Bridge returned an invalid preflight report.' }
if (-not $doctor.ready) { throw 'Bridge preflight did not report ready.' }

Write-Host ''
$installedManifest = Get-Content (Join-Path $InstallDir 'SpireShowdown.json') -Raw | ConvertFrom-Json
Write-Host "SPIRE SHOWDOWN $($installedManifest.version) INSTALLATION PASSED." -ForegroundColor Green
Write-Host 'Installed mod files match the release payload byte-for-byte.' -ForegroundColor Green
Write-Host "Connect code: $($verifiedConfig.connect_code)" -ForegroundColor Green
Write-Host 'In Steam, start Slay the Spire 2 and choose PLAY WITH MODS.' -ForegroundColor Yellow
Write-Host 'On the mod screen, verify BaseLib and Spire Showdown are both enabled.' -ForegroundColor Yellow
Write-Host 'Use the same StS2 beta branch and enabled mod list on every test computer.'
Remove-Item $payloadTempDir -Recurse -Force -ErrorAction SilentlyContinue
