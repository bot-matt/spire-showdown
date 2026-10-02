#!/usr/bin/env bash
set -euo pipefail

die() {
  printf 'Spire Showdown installer: %s\n' "$*" >&2
  exit 1
}

note() {
  printf '\n==> %s\n' "$*"
}

downloads_dir=${XDG_DOWNLOAD_DIR:-"$HOME/Downloads"}
game_dir=${SPIRE_SHOWDOWN_GAME_DIR:-}
iso_path=${SPIRE_SHOWDOWN_MELEE_ISO:-}
slippi_bundle=${SPIRE_SHOWDOWN_SLIPPI_BUNDLE:-}

while (($#)); do
  case "$1" in
    --game-dir) game_dir=${2:?missing path after --game-dir}; shift 2 ;;
    --iso) iso_path=${2:?missing path after --iso}; shift 2 ;;
    --slippi-bundle) slippi_bundle=${2:?missing path after --slippi-bundle}; shift 2 ;;
    -h|--help)
      printf 'Usage: %s [--game-dir PATH] [--iso PATH] [--slippi-bundle ZIP]\n' "$0"
      exit 0
      ;;
    *) die "unknown option: $1" ;;
  esac
done

find_game() {
  local candidate
  for candidate in \
    "$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2" \
    "$HOME/.steam/steam/steamapps/common/Slay the Spire 2" \
    /run/media/*/*/Games/Steam/steamapps/common/'Slay the Spire 2' \
    /run/media/*/*/SteamLibrary/steamapps/common/'Slay the Spire 2'; do
    if [[ -x "$candidate/SlayTheSpire2" ]]; then
      printf '%s\n' "$candidate"
      return
    fi
  done
}

if [[ -z "$game_dir" ]]; then
  game_dir=$(find_game || true)
fi
if [[ -z "$game_dir" || ! -x "$game_dir/SlayTheSpire2" ]]; then
  read -r -p 'Paste the Slay the Spire 2 game folder: ' game_dir
fi
[[ -x "$game_dir/SlayTheSpire2" ]] || die "game executable not found in $game_dir"
if pgrep -f 'SlayTheSpire2|spire-showdown-bridge' >/dev/null 2>&1; then
  die 'close Slay the Spire 2 and its bridge before running the installer'
fi
command -v curl >/dev/null || die 'curl is required'
command -v unzip >/dev/null || die 'unzip is required'
command -v python3 >/dev/null || die 'python3 is required'

steamapps=${game_dir%/common/Slay the Spire 2}
baselib="$steamapps/workshop/content/2868840/3737335127/BaseLib/BaseLib.dll"
if [[ ! -f "$baselib" ]]; then
  die 'BaseLib is missing. Subscribe to Workshop item 3737335127, let Steam finish downloading it, then rerun this installer: https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127'
fi

work_dir=$(mktemp -d /tmp/spire-showdown-install.XXXXXX)
trap 'rm -rf -- "$work_dir"' EXIT
note 'Downloading the latest Spire Showdown mod payload'
curl --fail --location --progress-bar \
  'https://github.com/bot-matt/spire-showdown/releases/latest/download/spire-showdown-mod-Linux-x86_64.zip' \
  --output "$work_dir/mod.zip"
mkdir -p "$work_dir/mod"
unzip -q -o "$work_dir/mod.zip" -d "$work_dir/mod"
payload_dir="$work_dir/mod/SpireShowdown"
[[ -f "$payload_dir/SpireShowdown.dll" && -f "$payload_dir/spire-showdown-bridge" ]] \
  || die 'latest Linux mod bundle is incomplete'

if [[ -z "$slippi_bundle" ]]; then
  slippi_bundle="$downloads_dir/spire-showdown-slippi-bazzite-x86_64.zip"
  mkdir -p "$downloads_dir"
  note 'Downloading the latest public patched Slippi release (replacing any cached copy)'
  curl --fail --location --progress-bar \
    'https://github.com/bot-matt/spire-showdown/releases/latest/download/spire-showdown-slippi-bazzite-x86_64.zip' \
    --output "$slippi_bundle" || slippi_bundle=
fi
if [[ -z "$slippi_bundle" || ! -f "$slippi_bundle" ]]; then
  read -r -p 'Paste the patched Bazzite Slippi artifact ZIP: ' slippi_bundle
fi
[[ -f "$slippi_bundle" ]] || die "Slippi artifact not found: $slippi_bundle"

if [[ -z "$iso_path" ]]; then
  launcher_settings="$HOME/.config/Slippi Launcher/Settings"
  if [[ -f "$launcher_settings" ]] && command -v python3 >/dev/null; then
    iso_path=$(python3 - "$launcher_settings" <<'PY' || true
import json, pathlib, sys
try:
    data = json.loads(pathlib.Path(sys.argv[1]).read_text())
    path = data.get("settings", {}).get("isoPath", "")
    if pathlib.Path(path).is_file():
        print(path)
except Exception:
    pass
PY
)
  fi
fi
if [[ -z "$iso_path" ]]; then
  for candidate in "$downloads_dir/Melee.iso" "$HOME/Games/Melee.iso" "$HOME/ROMs/Melee.iso"; do
    if [[ -f "$candidate" ]]; then
      iso_path=$candidate
      break
    fi
  done
fi
if [[ -z "$iso_path" || ! -f "$iso_path" ]]; then
  read -r -p 'Paste the path to your legally dumped Melee ISO: ' iso_path
fi
[[ -f "$iso_path" ]] || die "Melee ISO not found: $iso_path"

install_dir="$game_dir/mods/SpireShowdown"
note "Installing mod to $install_dir"
mkdir -p "$install_dir"
install -m 0644 "$payload_dir/SpireShowdown.dll" "$install_dir/.SpireShowdown.dll.new"
install -m 0644 "$payload_dir/SpireShowdown.json" "$install_dir/.SpireShowdown.json.new"
install -m 0755 "$payload_dir/spire-showdown-bridge" "$install_dir/.spire-showdown-bridge.new"
mv -f "$install_dir/.SpireShowdown.dll.new" "$install_dir/SpireShowdown.dll"
mv -f "$install_dir/.SpireShowdown.json.new" "$install_dir/SpireShowdown.json"
mv -f "$install_dir/.spire-showdown-bridge.new" "$install_dir/spire-showdown-bridge"
cmp -s "$payload_dir/SpireShowdown.dll" "$install_dir/SpireShowdown.dll" \
  || die 'installed mod DLL does not match the release payload'
cmp -s "$payload_dir/SpireShowdown.json" "$install_dir/SpireShowdown.json" \
  || die 'installed mod manifest does not match the release payload'
cmp -s "$payload_dir/spire-showdown-bridge" "$install_dir/spire-showdown-bridge" \
  || die 'installed bridge does not match the release payload'

mkdir -p "$work_dir/slippi"
unzip -q -o "$slippi_bundle" -d "$work_dir/slippi"
appimage=$(find "$work_dir/slippi" -type f -iname '*.AppImage' -print -quit)
[[ -n "$appimage" ]] || die 'patched Slippi artifact did not contain an AppImage'
installed_slippi="$install_dir/Spire-Showdown-Slippi-Bazzite-x86_64.AppImage"
install -m 0755 "$appimage" "$install_dir/.Spire-Showdown-Slippi.new"
mv -f "$install_dir/.Spire-Showdown-Slippi.new" "$installed_slippi"
cmp -s "$appimage" "$installed_slippi" \
  || die 'installed Slippi AppImage does not match the release payload'

note 'Configuring the active Steam gamepad for Player 1'
slippi_config_dir="$HOME/.config/SlippiOnline/Config"
mkdir -p "$slippi_config_dir"
controller_name=$(python3 - <<'PY'
import pathlib

# Steam exposes the controller used by the game as an evdev joystick to child
# processes too. Prefer that virtual pad, then the first joystick-capable pad.
candidates = []
for joystick in pathlib.Path("/sys/class/input").glob("js*"):
    device = joystick / "device"
    try:
        name = (device / "name").read_text().strip()
    except OSError:
        continue
    lowered = name.lower()
    if any(word in lowered for word in ("extest", "fake device", "mouse", "tablet", "touch", "keyboard", "pen")):
        continue
    candidates.append(name)
for name in candidates:
    if "steam" in name.lower() or "gamepad" in name.lower():
        print(name)
        break
else:
    if candidates:
        print(candidates[0])
PY
)
if [[ -n "$controller_name" ]]; then
  python3 - "$slippi_config_dir/GCPadNew.ini" "$controller_name" <<'PY'
import pathlib, sys
path, name = pathlib.Path(sys.argv[1]), sys.argv[2]
path.write_text(f'''[GCPad1]
Device = evdev/0/{name}
Buttons/A = `Button 0`
Buttons/B = `Button 1`
Buttons/X = `Button 2`
Buttons/Y = `Button 3`
Buttons/Z = `Button 5`
Buttons/Start = `Button 7`
Main Stick/Up = `Axis 1-`
Main Stick/Down = `Axis 1+`
Main Stick/Left = `Axis 0-`
Main Stick/Right = `Axis 0+`
C-Stick/Up = `Axis 4-`
C-Stick/Down = `Axis 4+`
C-Stick/Left = `Axis 3-`
C-Stick/Right = `Axis 3+`
Triggers/L = `Axis 2+`
Triggers/R = `Axis 5+`
D-Pad/Up = `Axis 7-`
D-Pad/Down = `Axis 7+`
D-Pad/Left = `Axis 6-`
D-Pad/Right = `Axis 6+`
Rumble/Motor = Motor
[GCPad2]
[GCPad3]
[GCPad4]
''')
PY
  python3 - "$slippi_config_dir/Dolphin.ini" <<'PY'
import configparser, pathlib, sys
path = pathlib.Path(sys.argv[1])
config = configparser.ConfigParser(strict=False)
config.optionxform = str
if path.exists():
    config.read(path)
if not config.has_section("Core"):
    config.add_section("Core")
config.set("Core", "SIDevice0", "6")
config.set("Core", "EnableCheats", "True")
for port in range(1, 4):
    config.set("Core", f"SIDevice{port}", "0")
if not config.has_section("Input"):
    config.add_section("Input")
config.set("Input", "BackgroundInput", "True")
with path.open("w") as output:
    config.write(output, space_around_delimiters=True)
PY
  note "Mapped Player 1 to $controller_name"
else
  note 'No gamepad was visible; connect it and rerun this installer'
fi

config_dir="$HOME/.local/share/SlayTheSpire2"
config_path="$config_dir/spire-showdown.json"
mkdir -p "$config_dir"
connect_code=${SPIRE_SHOWDOWN_CONNECT_CODE:-}
if [[ -z "$connect_code" ]]; then
  connect_code=$(python3 - "$config_path" <<'PY' || true
import json, os, pathlib, sys
paths = [
    pathlib.Path(sys.argv[1]),
    pathlib.Path.home() / ".config/Slippi Launcher/netplay/Slippi/user.json",
    pathlib.Path.home() / ".config/Slippi Launcher/netplay-beta/Slippi/user.json",
    pathlib.Path.home() / ".config/SlippiOnline/Slippi/user.json",
    pathlib.Path.home() / ".config/slippi-dolphin/netplay/Slippi/user.json",
    pathlib.Path.home() / ".config/slippi-dolphin/netplay-beta/Slippi/user.json",
]
for path in paths:
    try:
        data = json.loads(path.read_text())
        code = data.get("connect_code") or data.get("connectCode")
        if isinstance(code, str) and "#" in code and len(code.strip()) <= 9:
            print(code.strip())
            break
    except Exception:
        pass
PY
)
fi
if [[ "$connect_code" != *'#'* || ${#connect_code} -gt 9 ]]; then
  read -r -p 'Enter your Slippi connect code (for example NAME#123): ' connect_code
fi
[[ "$connect_code" == *'#'* && ${#connect_code} -le 9 ]] \
  || die 'a valid Slippi connect code is required; sign into Slippi Launcher and rerun the installer'
python3 - "$config_path" \
  "$installed_slippi" "$iso_path" "$connect_code" <<'PY'
import json, pathlib, sys
config_path, slippi_path, iso_path = map(pathlib.Path, sys.argv[1:4])
connect_code = sys.argv[4]
existing = {}
try:
    existing = json.loads(config_path.read_text())
except Exception:
    pass
existing.update({
    "connect_code": connect_code,
    "slippi_path": str(slippi_path.resolve()),
    "melee_iso_path": str(iso_path.resolve()),
})
config_path.write_text(json.dumps(existing, indent=2) + "\n")
PY

note 'Running bridge preflight'
"$install_dir/spire-showdown-bridge" doctor \
  --slippi "$installed_slippi" \
  --iso "$iso_path"

installed_version=$(python3 - "$install_dir/SpireShowdown.json" <<'PY'
import json, pathlib, sys
print(json.loads(pathlib.Path(sys.argv[1]).read_text())["version"])
PY
)

cat <<EOF

SPIRE SHOWDOWN $installed_version INSTALLATION PASSED.
Installed files match the release payload byte-for-byte.

In Steam, set Slay the Spire 2 Launch Options to:
  --display-driver x11

Start Slay the Spire 2 with PLAY WITH MODS and verify BaseLib and Spire Showdown are enabled.
Use the same StS2 beta branch and enabled mod list on every test computer.
EOF
