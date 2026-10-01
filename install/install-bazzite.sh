#!/usr/bin/env bash
set -euo pipefail

die() {
  printf 'Spire Showdown installer: %s\n' "$*" >&2
  exit 1
}

note() {
  printf '\n==> %s\n' "$*"
}

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
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

payload_dir=
for candidate in "$script_dir/SpireShowdown" "$script_dir/../SpireShowdown"; do
  if [[ -f "$candidate/SpireShowdown.dll" && -f "$candidate/spire-showdown-bridge" ]]; then
    payload_dir=$candidate
    break
  fi
done
[[ -n "$payload_dir" ]] || die 'run this script from the extracted Linux mod artifact'

if [[ -z "$slippi_bundle" ]]; then
  shopt -s nullglob
  matches=("$downloads_dir"/spire-showdown-slippi-bazzite-x86_64*.zip)
  shopt -u nullglob
  if ((${#matches[@]})); then
    slippi_bundle=${matches[0]}
  fi
fi
if [[ -z "$slippi_bundle" ]]; then
  command -v curl >/dev/null || die 'curl is required to download patched Slippi'
  slippi_bundle="$downloads_dir/spire-showdown-slippi-bazzite-x86_64.zip"
  note 'Downloading the public patched Slippi release'
  curl --fail --location --progress-bar \
    'https://github.com/bot-matt/spire-showdown/releases/latest/download/spire-showdown-slippi-bazzite-x86_64.zip' \
    --output "$slippi_bundle" || slippi_bundle=
fi
if [[ -z "$slippi_bundle" || ! -f "$slippi_bundle" ]]; then
  read -r -p 'Paste the patched Bazzite Slippi artifact ZIP: ' slippi_bundle
fi
[[ -f "$slippi_bundle" ]] || die "Slippi artifact not found: $slippi_bundle"
command -v unzip >/dev/null || die 'unzip is required'

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
cp -f "$payload_dir/SpireShowdown.dll" "$payload_dir/SpireShowdown.json" \
  "$payload_dir/spire-showdown-bridge" "$install_dir/"

tmp_dir=$(mktemp -d /tmp/spire-showdown-install.XXXXXX)
trap 'rm -rf -- "$tmp_dir"' EXIT
unzip -q -o "$slippi_bundle" -d "$tmp_dir"
appimage=$(find "$tmp_dir" -type f -iname '*.AppImage' -print -quit)
[[ -n "$appimage" ]] || die 'patched Slippi artifact did not contain an AppImage'
cp -f "$appimage" "$install_dir/Spire-Showdown-Slippi-Bazzite-x86_64.AppImage"
chmod 0755 "$install_dir/spire-showdown-bridge" \
  "$install_dir/Spire-Showdown-Slippi-Bazzite-x86_64.AppImage"

config_dir="$HOME/.local/share/SlayTheSpire2"
config_path="$config_dir/spire-showdown.json"
mkdir -p "$config_dir"
python3 - "$config_path" \
  "$install_dir/Spire-Showdown-Slippi-Bazzite-x86_64.AppImage" "$iso_path" <<'PY'
import json, pathlib, sys
config_path, slippi_path, iso_path = map(pathlib.Path, sys.argv[1:])
existing = {}
try:
    existing = json.loads(config_path.read_text())
except Exception:
    pass
existing.update({
    "connect_code": existing.get("connect_code"),
    "slippi_path": str(slippi_path.resolve()),
    "melee_iso_path": str(iso_path.resolve()),
})
config_path.write_text(json.dumps(existing, indent=2) + "\n")
PY

steamapps=${game_dir%/common/Slay the Spire 2}
baselib="$steamapps/workshop/content/2868840/3737335127/BaseLib/BaseLib.dll"
if [[ ! -f "$baselib" ]]; then
  printf '\nWARNING: BaseLib is not installed. Subscribe to Workshop item 3737335127 before launching.\n'
fi

note 'Running bridge preflight'
"$install_dir/spire-showdown-bridge" doctor \
  --slippi "$install_dir/Spire-Showdown-Slippi-Bazzite-x86_64.AppImage" \
  --iso "$iso_path"

cat <<EOF

Installation passed preflight.

In Steam, set Slay the Spire 2 Launch Options to:
  --display-driver x11

Use the same StS2 beta branch and enabled mod list on every test computer.
EOF
