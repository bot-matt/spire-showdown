[CmdletBinding()]
param([string]$GameDir,[string]$Iso,[string]$Release='latest')
$ErrorActionPreference='Stop'
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
if (-not [Environment]::Is64BitOperatingSystem) { throw 'This arena requires x64 Windows.' }
if (Get-Process -Name SlayTheSpire2,spire-showdown-bridge -ErrorAction SilentlyContinue) {
    throw 'Close Spire and its bridge before installing.'
}
$libraries=[Collections.Generic.List[string]]::new()
$steam=(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
if ($steam) { $libraries.Add($steam) }
if (${env:ProgramFiles(x86)}) { $libraries.Add((Join-Path ${env:ProgramFiles(x86)} Steam)) }
foreach($root in @($libraries.ToArray())) {
    $vdf=Join-Path $root 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach($line in Get-Content $vdf) {
            if($line -match '"path"\s+"([^"]+)"') { $libraries.Add(($Matches[1] -replace '\\\\','\')) }
        }
    }
}
if(-not $GameDir) {
    foreach($root in $libraries) {
        $candidate=Join-Path $root 'steamapps\common\Slay the Spire 2'
        if(Test-Path (Join-Path $candidate SlayTheSpire2.exe)) { $GameDir=$candidate; break }
    }
}
if(-not $GameDir) { $GameDir=Read-Host 'Slay the Spire 2 game folder' }
if(-not (Test-Path (Join-Path $GameDir SlayTheSpire2.exe))) { throw 'Spire executable not found.' }
$GameDir=(Resolve-Path $GameDir).Path
$baseFound=$false
foreach($root in $libraries) {
    if(Test-Path (Join-Path $root 'steamapps\workshop\content\2868840\3737335127\BaseLib\BaseLib.dll')) { $baseFound=$true }
}
if(-not $baseFound) { throw 'Subscribe to BaseLib Workshop item 3737335127 and let Steam download it.' }
$configDir=Join-Path $env:APPDATA SlayTheSpire2
$configPath=Join-Path $configDir spire-showdown.json
$config=@{}
if(Test-Path $configPath) {
    # Preserve every setting. Invalid existing JSON is an error, not an excuse to erase it.
    $old=Get-Content $configPath -Raw | ConvertFrom-Json
    if ($null -eq $old -or $old -isnot [PSCustomObject]) { throw 'Existing settings must be a JSON object.' }
    foreach($property in $old.PSObject.Properties) { $config[$property.Name]=$property.Value }
}
if(-not $Iso -and $config.melee_iso_path) { $Iso=$config.melee_iso_path }
if(-not $Iso) {
    $settings=Join-Path $env:APPDATA 'Slippi Launcher\Settings'
    if(Test-Path $settings) {
        try { $Iso=(Get-Content $settings -Raw | ConvertFrom-Json).settings.isoPath } catch {}
    }
}
if(-not $Iso -or -not (Test-Path $Iso)) { $Iso=Read-Host 'Path to your legally dumped Melee NTSC 1.02 ISO' }
$Iso=(Resolve-Path $Iso).Path
$stream=[IO.File]::OpenRead($Iso)
try { $header=New-Object byte[] 8; $read=$stream.Read($header,0,8) } finally { $stream.Dispose() }
if($read -ne 8 -or [Text.Encoding]::ASCII.GetString($header,0,6) -ne 'GALE01' -or $header[7] -ne 2) {
    throw 'Arena needs a Melee NTSC-U 1.02 ISO (GALE01 revision 2).'
}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('spire-showdown-arena-'+[guid]::NewGuid())
New-Item -ItemType Directory $temp | Out-Null
try {
    if($Release -eq 'latest') {
        $metadata=Invoke-RestMethod https://api.github.com/repos/bot-matt/spire-showdown/releases/latest -Headers @{'Cache-Control'='no-cache'}
        $Release=$metadata.tag_name
    }
    if($Release -notmatch '^[A-Za-z0-9._-]+$') { throw 'Invalid release tag.' }
    $url="https://github.com/bot-matt/spire-showdown/releases/download/$Release"
    $sums=Join-Path $temp SHA256SUMS
    Invoke-WebRequest "$url/SHA256SUMS" -UseBasicParsing -OutFile $sums
    $assets=@('spire-showdown-mod-Windows-x86_64.zip','spire-showdown-arena-Windows-x86_64.zip')
    foreach($asset in $assets) {
        $archive=Join-Path $temp $asset
        Write-Host "Downloading $asset ($Release)..."
        Invoke-WebRequest "$url/$asset" -UseBasicParsing -OutFile $archive
        $expected=$null
        foreach($line in Get-Content $sums) {
            if($line -match '^([a-fA-F0-9]{64})\s+\*?(.+)$' -and $Matches[2] -eq $asset) { $expected=$Matches[1] }
        }
        if(-not $expected -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) { throw "Checksum mismatch or missing: $asset" }
    }
    Expand-Archive (Join-Path $temp $assets[0]) (Join-Path $temp mod)
    Expand-Archive (Join-Path $temp $assets[1]) (Join-Path $temp engine)
    $payload=Join-Path $temp 'mod\SpireShowdown'
    $arena=Join-Path $temp 'engine\arena'
    $files=@('SpireShowdown.dll','SpireShowdown.json','spire-showdown-bridge.exe')
    foreach($file in $files) { if(-not (Test-Path (Join-Path $payload $file))) { throw "Missing mod file $file" } }
    foreach($file in @('melee_port.exe','SpireArena.json','Sys\GameFiles\GALE01','Lab')) {
        if(-not (Test-Path (Join-Path $arena $file))) { throw "Arena bundle missing $file" }
    }
    $manifest=Get-Content (Join-Path $arena SpireArena.json) -Raw | ConvertFrom-Json
    if($manifest.protocol -ne 1 -or $manifest.engine -ne 'static_recomp') { throw 'Arena integration protocol mismatch.' }
    $install=Join-Path $GameDir 'mods\SpireShowdown'
    $backup=Join-Path $install ('backups\'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[guid]::NewGuid())
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach($file in $files) {
        $target=Join-Path $install $file
        if(Test-Path $target) { Copy-Item $target $backup }
        Copy-Item (Join-Path $payload $file) "$target.new"
        Move-Item "$target.new" $target -Force
        if((Get-FileHash $target).Hash -ne (Get-FileHash (Join-Path $payload $file)).Hash) { throw "Install verification failed: $file" }
    }
    $installedArena=Join-Path $install arena
    if(Test-Path $installedArena) { Move-Item $installedArena (Join-Path $backup arena) }
    Copy-Item $arena $installedArena -Recurse
    $arenaExe=Join-Path $installedArena melee_port.exe
    & (Join-Path $install spire-showdown-bridge.exe) doctor --arena $arenaExe --iso $Iso
    if($LASTEXITCODE -ne 0) { throw 'Arena preflight failed; existing settings were not changed.' }
    New-Item -ItemType Directory -Force $configDir | Out-Null
    if(Test-Path $configPath) { Copy-Item $configPath (Join-Path $backup spire-showdown.json) }
    $config.arena_backend='unlocked'; $config.melee_unlocked_path=$arenaExe; $config.melee_iso_path=$Iso
    if(-not $config.ContainsKey('lab_view')) { $config.lab_view=$true }
    if(-not $config.ContainsKey('controller_mode')) { $config.controller_mode='auto' }
    foreach($relative in @('Slippi Launcher\netplay\Slippi','Slippi Launcher\netplay-beta\Slippi','SlippiOnline\Slippi')) {
        $userDir=Join-Path $env:APPDATA $relative
        if(Test-Path (Join-Path $userDir user.json)) {
            try {
                $auth=Get-Content (Join-Path $userDir user.json) -Raw | ConvertFrom-Json
                if(-not $config.connect_code) { $config.connect_code=$auth.connectCode }
                $config.slippi_user_dir=$userDir; break
            } catch {}
        }
    }
    [IO.File]::WriteAllText("$configPath.new",($config | ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
    Move-Item "$configPath.new" $configPath -Force
    Write-Host "Installed $Release. Recoverable backup: $backup" -ForegroundColor Green
    Write-Host 'Play with Mods, enable BaseLib + Spire Showdown; F8 tests against level-9 Fox.'
    if(-not $config.connect_code -or -not $config.slippi_user_dir) {
        Write-Host 'CPU test is available. For online play, sign into Slippi Launcher on THIS machine and rerun.' -ForegroundColor Yellow
    }
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
