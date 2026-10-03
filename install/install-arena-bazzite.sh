#!/usr/bin/env bash
# Melee Unlocked integration installer. No system-wide Wine or compiler install.
set -euo pipefail
die() { printf 'Spire Showdown: %s\n' "$*" >&2; exit 1; }
game_dir=${SPIRE_SHOWDOWN_GAME_DIR:-}
iso_path=${SPIRE_SHOWDOWN_MELEE_ISO:-}
proton_path=${SPIRE_SHOWDOWN_PROTON:-}
release=${SPIRE_SHOWDOWN_RELEASE:-latest}
while (($#)); do
  case "$1" in
    --game-dir) game_dir=${2:?}; shift 2;;
    --iso) iso_path=${2:?}; shift 2;;
    --proton) proton_path=${2:?}; shift 2;;
    --release) release=${2:?}; shift 2;;
    --help|-h) printf 'Usage: %s [--game-dir PATH] [--iso PATH] [--proton PATH] [--release TAG]\n' "$0"; exit 0;;
    *) die "Unknown option: $1";;
  esac
done
[[ $(uname -m) == x86_64 ]] || die 'This release requires x86_64 Linux.'
for tool in curl unzip python3 sha256sum; do command -v "$tool" >/dev/null || die "$tool is required"; done
if pgrep -x SlayTheSpire2 >/dev/null || pgrep -f '/spire-showdown-bridge( |$)' >/dev/null; then
  die 'Close Spire and its bridge before installing.'
fi
work_dir=$(mktemp -d /tmp/spire-showdown-arena-install.XXXXXX)
trap 'rm -rf -- "$work_dir"' EXIT
config_path=${SPIRE_SHOWDOWN_CONFIG:-"$HOME/.local/share/SlayTheSpire2/spire-showdown.json"}
# Refuse malformed settings before replacing any installed files.
if [[ -f "$config_path" ]]; then
  python3 - "$config_path" <<'PY'
import json,sys
if not isinstance(json.load(open(sys.argv[1])),dict): sys.exit('Existing settings must be a JSON object.')
PY
fi
# Discover Steam libraryfolders.vdf, including external drives and Flatpak Steam.
mapfile -t libraries < <(python3 - <<'PY'
import pathlib,re
h=pathlib.Path.home()
roots=[h/'.local/share/Steam',h/'.steam/steam',h/'.var/app/com.valvesoftware.Steam/.local/share/Steam']
found=[]
for root in roots:
    if root.is_dir(): found.append(root)
    try:
        for path in re.findall(r'"path"\s+"([^"]+)"',(root/'steamapps/libraryfolders.vdf').read_text()):
            found.append(pathlib.Path(path.replace('\\\\','\\')))
    except OSError: pass
for root in dict.fromkeys(found): print(root)
PY
)
for library in "${libraries[@]}"; do
  candidate="$library/steamapps/common/Slay the Spire 2"
  if [[ -z "$game_dir" && -f "$candidate/SlayTheSpire2" ]]; then game_dir=$candidate; fi
  for name in 'Proton - Experimental' 'Proton 10.0' 'Proton 9.0'; do
    candidate="$library/steamapps/common/$name/proton"
    if [[ -z "$proton_path" && -f "$candidate" ]]; then proton_path=$candidate; fi
  done
done
if [[ -z "$game_dir" ]]; then read -r -p 'Slay the Spire 2 game folder: ' game_dir; fi
[[ -f "$game_dir/SlayTheSpire2" ]] || die 'Native Linux Spire executable not found.'
game_dir=$(realpath "$game_dir")
if [[ -z "$proton_path" ]]; then
  die 'Install Proton Experimental from Steam > Library > Tools, then rerun. Or pass --proton PATH.'
fi
[[ -f "$proton_path" ]] || die 'Proton script not found.'
base_found=false
for library in "${libraries[@]}" "${game_dir%/steamapps/common/Slay the Spire 2}"; do
  [[ ! -f "$library/steamapps/workshop/content/2868840/3737335127/BaseLib/BaseLib.dll" ]] || base_found=true
done
$base_found || die 'Subscribe to BaseLib Workshop item 3737335127 and let Steam finish downloading it.'
if [[ -z "$iso_path" ]]; then
  iso_path=$(python3 - "$config_path" <<'PY'
import pathlib,json,sys
h=pathlib.Path.home()
for file,key in [(pathlib.Path(sys.argv[1]),'melee_iso_path'),(h/'.config/Slippi Launcher/Settings','settings')]:
    try:
        data=json.loads(file.read_text()); value=data.get(key)
        if isinstance(value,dict): value=value.get('isoPath')
        if value and pathlib.Path(value).is_file(): print(value); break
    except (OSError,ValueError): pass
else:
    for p in [h/'Downloads/Melee.iso',h/'Games/Melee.iso',h/'ROMs/Melee.iso']:
        if p.is_file(): print(p); break
PY
)
fi
if [[ -z "$iso_path" || ! -f "$iso_path" ]]; then read -r -p 'Path to your legally dumped Melee NTSC 1.02 ISO: ' iso_path; fi
[[ -f "$iso_path" ]] || die 'ISO not found.'
iso_path=$(realpath "$iso_path")
python3 - "$iso_path" <<'PY'
import sys
with open(sys.argv[1],'rb') as f: header=f.read(8)
if header[:6]!=b'GALE01' or header[7]!=2: sys.exit('Arena needs a Melee NTSC-U 1.02 ISO (GALE01 revision 2).')
PY
if [[ "$release" == latest ]]; then
  curl -fLsS -H 'Cache-Control: no-cache' https://api.github.com/repos/bot-matt/spire-showdown/releases/latest -o "$work_dir/release.json"
  release=$(python3 - "$work_dir/release.json" <<'PY'
import json,sys
print(json.load(open(sys.argv[1]))['tag_name'])
PY
)
fi
[[ "$release" =~ ^[A-Za-z0-9._-]+$ ]] || die 'Invalid release tag.'
url="https://github.com/bot-matt/spire-showdown/releases/download/$release"
assets=(spire-showdown-mod-Linux-x86_64.zip spire-showdown-arena-Windows-x86_64.zip)
curl -fLsS "$url/SHA256SUMS" -o "$work_dir/SHA256SUMS" || die 'Release has no verified arena payload; installation was not changed.'
for asset in "${assets[@]}"; do
  printf 'Downloading %s (%s)...\n' "$asset" "$release"
  curl -fL --progress-bar "$url/$asset" -o "$work_dir/$asset"
  expected=$(awk -v name="$asset" '$2==name || $2=="*"name {print $1}' "$work_dir/SHA256SUMS")
  [[ "$expected" =~ ^[a-fA-F0-9]{64}$ ]] || die "Missing checksum for $asset"
  actual=$(sha256sum "$work_dir/$asset"); [[ ${actual%% *} == "$expected" ]] || die "Checksum mismatch: $asset"
done
unzip -q "$work_dir/${assets[0]}" -d "$work_dir/mod"
unzip -q "$work_dir/${assets[1]}" -d "$work_dir/engine"
payload="$work_dir/mod/SpireShowdown"
arena="$work_dir/engine/arena"
for file in SpireShowdown.dll SpireShowdown.json spire-showdown-bridge; do
  [[ -f "$payload/$file" ]] || die "Mod bundle missing $file"
done
[[ -f "$arena/melee_port.exe" && -f "$arena/SpireArena.json" && -d "$arena/Lab" && -d "$arena/Sys/GameFiles/GALE01" ]] || die 'Arena bundle incomplete.'
python3 - "$arena/SpireArena.json" <<'PY'
import json,sys
m=json.load(open(sys.argv[1]))
if m.get('protocol')!=1 or m.get('engine')!='static_recomp': sys.exit('Arena integration protocol mismatch.')
PY
chmod +x "$payload/spire-showdown-bridge"
"$payload/spire-showdown-bridge" doctor --arena "$arena/melee_port.exe" --proton "$proton_path" --iso "$iso_path"
install_dir="$game_dir/mods/SpireShowdown"
mkdir -p "$install_dir"
# Keep recoverable backups; never remove unrelated mods or global Slippi files.
backup="$install_dir/backups/$(date +%Y%m%d-%H%M%S)-$$"
mkdir -p "$backup"
for file in SpireShowdown.dll SpireShowdown.json spire-showdown-bridge; do
  [[ ! -e "$install_dir/$file" ]] || cp -a "$install_dir/$file" "$backup/"
done
if [[ -e "$install_dir/arena" ]]; then mv "$install_dir/arena" "$backup/arena-replaced"; fi
cp -a "$arena" "$install_dir/arena"
for file in SpireShowdown.dll SpireShowdown.json spire-showdown-bridge; do
  cp "$payload/$file" "$install_dir/.$file.new"
  mv -f "$install_dir/.$file.new" "$install_dir/$file"
  cmp -s "$payload/$file" "$install_dir/$file" || die "Install verification failed: $file"
done
chmod +x "$install_dir/spire-showdown-bridge"
"$install_dir/spire-showdown-bridge" doctor --arena "$install_dir/arena/melee_port.exe" --proton "$proton_path" --iso "$iso_path"
mkdir -p "$(dirname "$config_path")"
[[ ! -f "$config_path" ]] || cp -a "$config_path" "$backup/spire-showdown.json"
python3 - "$config_path" "$install_dir/arena/melee_port.exe" "$proton_path" "$iso_path" <<'PY'
import json,pathlib,sys
p=pathlib.Path(sys.argv[1]); data={}
if p.exists(): data=json.loads(p.read_text()) # Never silently erase malformed existing settings.
data.update(arena_backend='unlocked',melee_unlocked_path=sys.argv[2],proton_path=sys.argv[3],melee_iso_path=sys.argv[4])
data.setdefault('lab_view',True); data.setdefault('controller_mode','auto')
h=pathlib.Path.home()
for root in [h/'.config/Slippi Launcher/netplay/Slippi',h/'.config/SlippiOnline/Slippi']:
    try:
        auth=json.loads((root/'user.json').read_text())
        if not data.get('connect_code'): data['connect_code']=auth['connectCode']
        data['slippi_user_dir']=str(root); break
    except (OSError,ValueError,KeyError): pass
t=p.with_suffix('.json.new'); t.write_text(json.dumps(data,indent=2)+'\n'); t.replace(p)
if not data.get('connect_code') or not data.get('slippi_user_dir'): print('CPU test is available. For online play, sign into Slippi Launcher on THIS machine, then rerun.')
PY
printf '\nInstalled %s. Backup: %s\n' "$release" "$backup"
printf 'Steam > Spire 2 > Properties > Launch Options: --display-driver x11\nPlay with Mods, enable BaseLib and Spire Showdown, then F8 tests against level-9 Fox.\n'
