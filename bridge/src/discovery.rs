use std::env;
use std::ffi::OsStr;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use walkdir::WalkDir;

use crate::iso::{inspect_iso, IsoReport};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Source {
    Explicit,
    Environment,
    LauncherSettings,
    Path,
    KnownLocation,
    Missing,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LocatedPath {
    pub path: PathBuf,
    pub source: Source,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DiscoveryReport {
    pub ready: bool,
    pub platform: String,
    pub slippi: Option<LocatedPath>,
    pub melee_iso: Option<LocatedPath>,
    pub connect_code: Option<String>,
    pub iso: Option<IsoReport>,
    pub problems: Vec<String>,
}

pub fn discover(explicit_slippi: Option<&Path>, explicit_iso: Option<&Path>) -> DiscoveryReport {
    let slippi = locate_slippi(explicit_slippi);
    let melee_iso = locate_iso(explicit_iso);
    let connect_code = locate_connect_code();
    let mut problems = Vec::new();

    if slippi.is_none() {
        problems.push("Slippi Dolphin was not found; select its executable in setup".into());
    }
    if melee_iso.is_none() {
        problems.push("Melee ISO was not found; select a legally dumped image in setup".into());
    }

    let iso = melee_iso.as_ref().map(|located| inspect_iso(&located.path));
    if let Some(iso_report) = &iso {
        if let Some(problem) = &iso_report.problem {
            problems.push(problem.clone());
        }
    }

    DiscoveryReport {
        ready: slippi.is_some()
            && melee_iso.is_some()
            && iso.as_ref().is_some_and(|value| value.problem.is_none()),
        platform: env::consts::OS.into(),
        slippi,
        melee_iso,
        connect_code,
        iso,
        problems,
    }
}

fn locate_connect_code() -> Option<String> {
    for path in slippi_user_paths() {
        let Ok(contents) = std::fs::read_to_string(path) else {
            continue;
        };
        let Ok(user) = serde_json::from_str::<serde_json::Value>(&contents) else {
            continue;
        };
        let Some(code) = user.get("connectCode").and_then(serde_json::Value::as_str) else {
            continue;
        };
        let code = code.trim();
        if valid_connect_code(code) {
            return Some(code.to_owned());
        }
    }
    None
}

fn valid_connect_code(code: &str) -> bool {
    !code.is_empty() && code.len() <= 9 && code.contains('#')
}

fn locate_slippi(explicit: Option<&Path>) -> Option<LocatedPath> {
    locate_explicit(explicit).or_else(|| {
        locate_env("SPIRE_SHOWDOWN_SLIPPI")
            .or_else(locate_slippi_on_path)
            .or_else(locate_slippi_from_launcher_settings)
            .or_else(locate_slippi_known_locations)
    })
}

fn locate_iso(explicit: Option<&Path>) -> Option<LocatedPath> {
    locate_explicit(explicit)
        .or_else(|| locate_env("SPIRE_SHOWDOWN_MELEE_ISO"))
        .or_else(locate_iso_from_launcher_settings)
        .or_else(locate_iso_known_locations)
}

fn locate_explicit(path: Option<&Path>) -> Option<LocatedPath> {
    let path = path?.canonicalize().ok()?;
    path.is_file().then_some(LocatedPath {
        path,
        source: Source::Explicit,
    })
}

fn locate_env(name: &str) -> Option<LocatedPath> {
    let path = PathBuf::from(env::var_os(name)?).canonicalize().ok()?;
    path.is_file().then_some(LocatedPath {
        path,
        source: Source::Environment,
    })
}

fn locate_slippi_on_path() -> Option<LocatedPath> {
    let path = env::var_os("PATH")?;
    let names: &[&str] = if cfg!(windows) {
        &["Slippi Dolphin.exe", "Slippi_Dolphin.exe", "Dolphin.exe"]
    } else {
        &[
            "Slippi_Dolphin",
            "slippi-dolphin",
            "Slippi_Online-x86_64.AppImage",
        ]
    };

    for directory in env::split_paths(&path) {
        for name in names {
            let candidate = directory.join(name);
            if candidate.is_file() {
                return Some(LocatedPath {
                    path: candidate.canonicalize().ok()?,
                    source: Source::Path,
                });
            }
        }
    }
    None
}

fn locate_slippi_known_locations() -> Option<LocatedPath> {
    for root in slippi_roots() {
        if let Some(path) = find_best_slippi_file(&root, 6) {
            return Some(LocatedPath {
                path,
                source: Source::KnownLocation,
            });
        }
    }
    None
}

fn locate_slippi_from_launcher_settings() -> Option<LocatedPath> {
    let (root, settings) = read_launcher_settings()?;
    let beta = settings
        .get("settings")
        .and_then(|value| value.get("useNetplayBeta"))
        .and_then(serde_json::Value::as_bool)
        .unwrap_or(false);
    let directory = if beta { "netplay-beta" } else { "netplay" };
    let path = find_best_slippi_file(&root.join(directory), 2)?;
    Some(LocatedPath {
        path,
        source: Source::LauncherSettings,
    })
}

fn locate_iso_from_launcher_settings() -> Option<LocatedPath> {
    let (_, settings) = read_launcher_settings()?;
    let configured = settings
        .get("settings")?
        .get("isoPath")?
        .as_str()
        .map(PathBuf::from)?;
    let path = configured.canonicalize().ok()?;
    (path.is_file() && is_melee_image(&path)).then_some(LocatedPath {
        path,
        source: Source::LauncherSettings,
    })
}

fn read_launcher_settings() -> Option<(PathBuf, serde_json::Value)> {
    for root in slippi_roots() {
        let settings_path = root.join("Settings");
        let Ok(contents) = std::fs::read_to_string(settings_path) else {
            continue;
        };
        if let Ok(settings) = serde_json::from_str(&contents) {
            return Some((root, settings));
        }
    }
    None
}

fn locate_iso_known_locations() -> Option<LocatedPath> {
    for root in iso_roots() {
        if let Some(path) = find_file(&root, 4, is_melee_image) {
            return Some(LocatedPath {
                path,
                source: Source::KnownLocation,
            });
        }
    }
    None
}

fn find_file(root: &Path, max_depth: usize, predicate: fn(&Path) -> bool) -> Option<PathBuf> {
    if !root.is_dir() {
        return None;
    }
    WalkDir::new(root)
        .max_depth(max_depth)
        .follow_links(false)
        .into_iter()
        .filter_map(Result::ok)
        .map(|entry| entry.into_path())
        .find(|path| path.is_file() && predicate(path))
        .and_then(|path| path.canonicalize().ok())
}

fn find_best_slippi_file(root: &Path, max_depth: usize) -> Option<PathBuf> {
    if !root.is_dir() {
        return None;
    }
    let mut candidates: Vec<PathBuf> = WalkDir::new(root)
        .max_depth(max_depth)
        .follow_links(false)
        .into_iter()
        .filter_map(Result::ok)
        .map(|entry| entry.into_path())
        .filter(|path| path.is_file() && is_slippi_binary(path))
        .collect();
    candidates.sort_by_key(|path| std::cmp::Reverse(slippi_score(path)));
    candidates.first().and_then(|path| path.canonicalize().ok())
}

fn slippi_score(path: &Path) -> u8 {
    let name = path
        .file_name()
        .and_then(OsStr::to_str)
        .unwrap_or_default()
        .to_ascii_lowercase();
    if name.contains("playback") {
        0
    } else if name.contains("online") || name.contains("netplay") {
        3
    } else if name.contains("slippi") && name.contains("dolphin") {
        2
    } else {
        1
    }
}

fn is_slippi_binary(path: &Path) -> bool {
    let name = path
        .file_name()
        .and_then(OsStr::to_str)
        .unwrap_or_default()
        .to_ascii_lowercase();
    let looks_like_slippi = name.contains("slippi")
        && !name.contains("playback")
        && (name.contains("dolphin") || name.contains("online") || name.ends_with(".appimage"));
    let windows_dolphin = cfg!(windows) && name == "dolphin.exe";
    looks_like_slippi || windows_dolphin
}

fn is_melee_image(path: &Path) -> bool {
    let extension = path
        .extension()
        .and_then(OsStr::to_str)
        .unwrap_or_default()
        .to_ascii_lowercase();
    if extension != "iso" && extension != "gcm" {
        return false;
    }
    let mut header = [0_u8; 6];
    std::fs::File::open(path)
        .and_then(|mut file| std::io::Read::read_exact(&mut file, &mut header))
        .is_ok()
        && &header == b"GALE01"
}

fn home_dir() -> Option<PathBuf> {
    env::var_os(if cfg!(windows) { "USERPROFILE" } else { "HOME" }).map(PathBuf::from)
}

fn slippi_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if cfg!(windows) {
        if let Some(value) = env::var_os("APPDATA") {
            roots.push(PathBuf::from(value).join("Slippi Launcher"));
        }
        if let Some(value) = env::var_os("LOCALAPPDATA") {
            roots.push(PathBuf::from(&value).join("Programs"));
            roots.push(PathBuf::from(value).join("slippi-launcher"));
        }
    } else if let Some(home) = home_dir() {
        roots.push(home.join(".config/Slippi Launcher"));
        roots.push(home.join(".config/slippi-launcher"));
        roots.push(home.join(".local/share/slippi-launcher"));
        roots.push(home.join("Applications"));
    }
    roots
}

fn slippi_user_paths() -> Vec<PathBuf> {
    let mut paths = Vec::new();
    if cfg!(windows) {
        if let Some(value) = env::var_os("APPDATA") {
            let root = PathBuf::from(value);
            paths.push(root.join("Slippi Launcher/netplay/Slippi/user.json"));
            paths.push(root.join("Slippi Launcher/netplay-beta/Slippi/user.json"));
            paths.push(root.join("SlippiOnline/Slippi/user.json"));
            paths.push(root.join("slippi-dolphin/netplay/Slippi/user.json"));
            paths.push(root.join("slippi-dolphin/netplay-beta/Slippi/user.json"));
        }
    } else if let Some(home) = home_dir() {
        paths.push(home.join(".config/SlippiOnline/Slippi/user.json"));
        paths.push(home.join(".config/slippi-dolphin/netplay/Slippi/user.json"));
        paths.push(home.join(".config/slippi-dolphin/netplay-beta/Slippi/user.json"));
    }
    paths
}

fn iso_roots() -> Vec<PathBuf> {
    let Some(home) = home_dir() else {
        return Vec::new();
    };
    ["Games", "ROMs", "roms", "ISOs", "isos"]
        .into_iter()
        .map(|name| home.join(name))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn recognizes_melee_disc_header() {
        let directory = tempfile::tempdir().unwrap();
        let iso = directory.path().join("melee.iso");
        let mut file = std::fs::File::create(&iso).unwrap();
        file.write_all(b"GALE01\0\x02test").unwrap();
        assert!(is_melee_image(&iso));
    }

    #[test]
    fn rejects_other_game_images() {
        let directory = tempfile::tempdir().unwrap();
        let iso = directory.path().join("other.iso");
        let mut file = std::fs::File::create(&iso).unwrap();
        file.write_all(b"OTHER1test").unwrap();
        assert!(!is_melee_image(&iso));
    }

    #[test]
    fn validates_connect_code_shape() {
        assert!(valid_connect_code("ABCD#123"));
        assert!(!valid_connect_code("missing"));
        assert!(!valid_connect_code("TOOLONG#123"));
    }
}
