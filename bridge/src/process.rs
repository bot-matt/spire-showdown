use std::fs::{self, File};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};

use serde::Deserialize;

use crate::protocol::DuelSpec;

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
pub struct SlippiDuelStatus {
    pub duel_id: String,
    pub phase: String,
    pub winner_idx: Option<i8>,
    pub local_won: Option<bool>,
}

pub struct SlippiProcess {
    child: Child,
    duel_id: String,
    session_dir: PathBuf,
    stopped: bool,
    arena: bool,
    control: serde_json::Value,
    #[cfg(target_os = "linux")]
    wine_server: Option<PathBuf>,
    native_keyboard: bool,
}

impl SlippiProcess {
    pub fn launch(
        slippi: &Path,
        iso: &Path,
        duel: &DuelSpec,
        arena: Option<&crate::arena::ArenaConfig>,
    ) -> Result<Self, String> {
        if arena.is_some() {
            crate::arena::validate_duel(duel)?;
        }
        let safe_id = safe_duel_id(&duel.duel_id)?;
        let session_dir = std::env::temp_dir()
            .join("spire-showdown")
            .join(format!("{}-{safe_id}", std::process::id()));
        fs::create_dir_all(&session_dir)
            .map_err(|error| format!("cannot create duel session directory: {error}"))?;

        let duel_file = session_dir.join("duel.json");
        let encoded = serde_json::to_vec_pretty(duel)
            .map_err(|error| format!("cannot encode duel specification: {error}"))?;
        fs::write(&duel_file, encoded)
            .map_err(|error| format!("cannot write duel specification: {error}"))?;

        let log_path = session_dir.join("slippi.log");
        let stdout = File::create(&log_path)
            .map_err(|error| format!("cannot create Slippi log: {error}"))?;
        let stderr = stdout
            .try_clone()
            .map_err(|error| format!("cannot duplicate Slippi log handle: {error}"))?;

        #[cfg(target_os = "linux")]
        let mut wine_server = None;
        let mut command = if let Some(config) = arena {
            let root = slippi.parent().ok_or("Arena directory missing")?;
            #[cfg(target_os = "linux")]
            let mut cmd = {
                let proton = config.proton.as_ref().ok_or("Proton missing")?;
                let proton_root = proton.parent().ok_or("Proton directory missing")?;
                let steam = proton_root
                    .parent()
                    .and_then(Path::parent)
                    .and_then(Path::parent)
                    .ok_or("Steam directory missing")?;
                let compat = session_dir.join("compat");
                fs::create_dir_all(&compat).map_err(|e| e.to_string())?;
                wine_server = Some(proton_root.join("files/bin/wineserver"));
                let mut cmd = Command::new(proton);
                cmd.env("STEAM_COMPAT_DATA_PATH", &compat)
                    .env("STEAM_COMPAT_CLIENT_INSTALL_PATH", steam)
                    .env("SteamAppId", "0")
                    .env("SteamGameId", "0")
                    .env("STEAM_COMPAT_APP_ID", "0")
                    .env_remove("LD_PRELOAD")
                    .arg("run")
                    .arg(wine_path(slippi));
                cmd
            };
            #[cfg(not(target_os = "linux"))]
            let mut cmd = Command::new(slippi);
            cmd.current_dir(root)
                .arg("--spire-duel")
                .arg(wine_path(&duel_file))
                .arg("--iso")
                .arg(wine_path(iso))
                .arg("--sys-dir")
                .arg(wine_path(&root.join("Sys")))
                .arg("--lab-dir")
                .arg(wine_path(&root.join("Lab")))
                .arg("--replay-dir")
                .arg(wine_path(&session_dir.join("replays")))
                .arg("--card-dir")
                .arg(wine_path(&session_dir.join("card")))
                .arg("--settings-path")
                .arg(wine_path(&session_dir.join("settings.ini")))
                .arg("--log-file")
                .arg(wine_path(&log_path))
                .arg("--window")
                .arg("960x720")
                .arg("--fps")
                .arg("60")
                .arg("--scale")
                .arg("1")
                .arg("--backend")
                .arg("d3d11")
                .arg("--pc-settings");
            cmd.arg("--no-music").arg("--volume").arg("0");
            if duel.controller_mode == "spire" {
                cmd.env("MELEE_NO_GC_ADAPTER", "1");
            }
            if let Some(user) = &config.user_dir {
                cmd.arg("--user-dir").arg(wine_path(user));
            }
            if duel.participants.len() > 2 {
                cmd.arg("--local-peer")
                    .arg(crate::arena::local_peer_argument(duel)?);
            }
            cmd
        } else {
            let mut cmd = Command::new(slippi);
            cmd.arg("--batch")
                .arg("--exec")
                .arg(iso)
                .arg("--spire-duel")
                .arg(&duel_file);
            configure_appimage(&mut cmd, slippi);
            cmd
        };
        command
            .stdin(Stdio::null())
            .stdout(Stdio::from(stdout))
            .stderr(Stdio::from(stderr));

        // Foreign-surface reparenting is forbidden by native Wayland. Force
        // Slippi's Qt window through XWayland on Bazzite so it can be embedded.
        #[cfg(target_os = "linux")]
        {
            use std::os::unix::process::CommandExt;
            command.env("QT_QPA_PLATFORM", "xcb");
            command.env("GDK_BACKEND", "x11");
            command.process_group(0);
        }

        let child = command
            .spawn()
            .map_err(|error| format!("cannot launch Slippi: {error}"))?;
        Ok(Self {
            child,
            duel_id: duel.duel_id.clone(),
            session_dir,
            stopped: false,
            arena: arena.is_some(),
            control: serde_json::json!({"duel_id":duel.duel_id,"resume":false,"cancel":false,"lab_view":duel.lab_view}),
            #[cfg(target_os = "linux")]
            wine_server,
            native_keyboard: arena.is_none()
                || matches!(duel.controller_mode.as_str(), "keyboard" | "native"),
        })
    }

    pub fn update_options(
        &mut self,
        lab_view: bool,
        pad: Option<crate::protocol::ControllerState>,
        volume_percent: Option<u8>,
    ) -> Result<(), String> {
        if !self.arena {
            return Err(
                "Lab view / Spire input requires the Melee Unlocked integration backend".into(),
            );
        }
        self.control["lab_view"] = lab_view.into();
        if let Some(volume) = volume_percent {
            self.control["volume_percent"] = volume.min(100).into();
        }
        self.control["pad"] = serde_json::to_value(pad).map_err(|e| e.to_string())?;
        self.write_control()
    }

    pub fn resume(&mut self) -> Result<(), String> {
        if !self.arena {
            return Ok(());
        }
        self.control["resume"] = true.into();
        self.write_control()
    }

    pub fn update_viewport(&mut self, bounds: crate::protocol::Bounds) -> Result<(), String> {
        if !self.arena {
            return Ok(());
        }
        if !(320..=8192).contains(&bounds.width) || !(240..=8192).contains(&bounds.height) {
            return Err("Arena viewport must be between 320x240 and 8192x8192".into());
        }
        // X11 reparenting changes the Unix surface, but Wine's Win32 client
        // size/swap chain can remain unchanged. Resize on the engine's owning
        // window thread too, rather than displaying a clipped render target.
        self.control["viewport"] =
            serde_json::json!({"width": bounds.width, "height": bounds.height});
        self.write_control()
    }

    fn write_control(&self) -> Result<(), String> {
        // Windows readers and writers share one tiny mailbox. std::fs::rename
        // replaces an existing target on both supported platforms.
        let path = self.session_dir.join("duel.json.control.json");
        let temp = self.session_dir.join("duel.json.control.tmp");
        fs::write(
            &temp,
            serde_json::to_vec(&self.control).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
        fs::rename(temp, path).map_err(|e| format!("Cannot publish arena controls: {e}"))
    }

    pub fn pid(&self) -> u32 {
        self.child.id()
    }

    pub fn needs_native_keyboard(&self) -> bool {
        self.native_keyboard
    }

    pub fn duel_id(&self) -> &str {
        &self.duel_id
    }

    pub fn duel_status(&self) -> Result<Option<SlippiDuelStatus>, String> {
        let path = self.session_dir.join("duel.json.status.json");
        let encoded = match fs::read(&path) {
            Ok(value) => value,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(format!("cannot read Slippi duel status: {error}")),
        };
        let status: SlippiDuelStatus = match serde_json::from_slice(&encoded) {
            Ok(status) => status,
            // Slippi replaces this tiny sidecar in place. A poll can land after
            // truncation but before the complete JSON has reached the file.
            Err(error) if error.is_eof() => return Ok(None),
            Err(error) => return Err(format!("cannot decode Slippi duel status: {error}")),
        };
        if status.duel_id != self.duel_id {
            return Err("Slippi duel status ID does not match the active duel".into());
        }
        Ok(Some(status))
    }

    pub fn try_wait(&mut self) -> Result<Option<i32>, String> {
        self.child
            .try_wait()
            .map(|status| status.map(|value| value.code().unwrap_or(-1)))
            .map_err(|error| format!("cannot inspect Slippi process: {error}"))
    }

    pub fn stop(&mut self) -> Result<(), String> {
        if self.stopped {
            return Ok(());
        }
        #[cfg(target_os = "linux")]
        if let Some(server) = &self.wine_server {
            // Wine may daemonize beyond the original process group. Kill only
            // this duel's private prefix, never a user's unrelated Wine game.
            let stopper = Command::new(server)
                .arg("-k")
                .env("WINEPREFIX", self.session_dir.join("compat/pfx"))
                .env_remove("LD_PRELOAD")
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn();
            if let Ok(mut stopper) = stopper {
                let deadline = std::time::Instant::now() + std::time::Duration::from_secs(2);
                loop {
                    if !matches!(stopper.try_wait(), Ok(None)) {
                        break;
                    }
                    if std::time::Instant::now() >= deadline {
                        let _ = stopper.kill();
                        let _ = stopper.wait();
                        break;
                    }
                    std::thread::sleep(std::time::Duration::from_millis(20));
                }
            }
        }
        #[cfg(target_os = "linux")]
        unsafe {
            // AppImage extract-and-run is a wrapper; stop its entire private
            // process group so Dolphin cannot survive a finished/cancelled duel.
            libc::kill(-(self.child.id() as i32), libc::SIGKILL);
        }
        if self.try_wait()?.is_none() {
            self.child
                .kill()
                .map_err(|error| format!("cannot stop Slippi: {error}"))?;
            self.child
                .wait()
                .map_err(|error| format!("cannot wait for Slippi to stop: {error}"))?;
        }
        self.stopped = true;
        Ok(())
    }
}

fn wine_path(path: &Path) -> String {
    #[cfg(target_os = "linux")]
    {
        format!("Z:{}", path.to_string_lossy())
    }
    #[cfg(not(target_os = "linux"))]
    {
        path.to_string_lossy().into_owned()
    }
}

fn configure_appimage(command: &mut Command, executable: &Path) {
    let is_appimage = executable
        .extension()
        .and_then(|value| value.to_str())
        .is_some_and(|value| value.eq_ignore_ascii_case("appimage"));
    if is_appimage {
        // Steam containers and some Bazzite installs do not expose /dev/fuse.
        // The AppImage runtime's built-in fallback preserves the same payload.
        command.env("APPIMAGE_EXTRACT_AND_RUN", "1");

        // Native Steam launches the game in a pressure-vessel filesystem where
        // the Fedora host libraries live under /run/host. Slippi Playback does
        // not bundle librsvg, so expose only it and its missing dav1d dependency
        // through a private shim. Adding the whole host library directory would
        // mix Bazzite's PulseAudio with the older libraries bundled by Slippi.
        #[cfg(target_os = "linux")]
        {
            command.env_remove("LD_PRELOAD");
            let mut library_paths = Vec::new();
            let shim_dir = std::env::temp_dir()
                .join("spire-showdown")
                .join("appimage-host-libs");
            if fs::create_dir_all(&shim_dir).is_ok() {
                let mut shim_ready = false;
                for name in ["librsvg-2.so.2", "libdav1d.so.7"] {
                    let host_library = Path::new("/run/host/usr/lib64").join(name);
                    let Ok(host_library) = host_library.canonicalize() else {
                        continue;
                    };
                    let shim = shim_dir.join(name);
                    let _ = fs::remove_file(&shim);
                    if std::os::unix::fs::symlink(host_library, &shim).is_ok() {
                        shim_ready = true;
                    }
                }
                if shim_ready {
                    library_paths.push(shim_dir);
                }
            }
            if let Some(existing) = std::env::var_os("LD_LIBRARY_PATH") {
                library_paths.extend(std::env::split_paths(&existing));
            }
            if !library_paths.is_empty() {
                if let Ok(joined) = std::env::join_paths(library_paths) {
                    command.env("LD_LIBRARY_PATH", joined);
                }
            }
        }
    }
}

impl Drop for SlippiProcess {
    fn drop(&mut self) {
        let _ = self.stop();
        let _ = fs::copy(
            self.session_dir.join("slippi.log"),
            std::env::temp_dir()
                .join("spire-showdown")
                .join("last-live-duel.log"),
        );
        let _ = fs::remove_dir_all(&self.session_dir);
    }
}

fn safe_duel_id(value: &str) -> Result<&str, String> {
    if value.is_empty()
        || value.len() > 64
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
    {
        return Err("duel ID must be 1-64 ASCII letters, numbers, dashes, or underscores".into());
    }
    Ok(value)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mailbox_process() -> SlippiProcess {
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let session_dir =
            std::env::temp_dir().join(format!("spire-mailbox-test-{}-{nonce}", std::process::id()));
        fs::create_dir(&session_dir).unwrap();
        // No emulator, Wine prefix, or live process is needed for mailbox tests.
        let mut child = Command::new(std::env::current_exe().unwrap())
            .arg("--list")
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        child.wait().unwrap();
        SlippiProcess {
            child,
            duel_id: "mailbox-test".into(),
            session_dir,
            stopped: true,
            arena: true,
            control: serde_json::json!({"duel_id":"mailbox-test","resume":false,"cancel":false,"lab_view":true}),
            #[cfg(target_os = "linux")]
            wine_server: None,
            native_keyboard: false,
        }
    }

    #[test]
    fn controllerless_mailbox_preserves_resume_and_viewport() {
        let mut process = mailbox_process();
        process.update_options(true, None, Some(24)).unwrap();
        process
            .update_viewport(crate::protocol::Bounds {
                x: 40,
                y: 60,
                width: 800,
                height: 600,
            })
            .unwrap();
        process.resume().unwrap();
        process.update_options(false, None, None).unwrap();
        let encoded = fs::read(process.session_dir.join("duel.json.control.json")).unwrap();
        let control: serde_json::Value = serde_json::from_slice(&encoded).unwrap();
        assert!(control["pad"].is_null());
        assert_eq!(control["resume"], true);
        assert_eq!(control["lab_view"], false);
        assert_eq!(control["viewport"]["width"], 800);
        assert_eq!(control["volume_percent"], 24);
    }

    #[test]
    fn invalid_arena_viewport_does_not_publish_controls() {
        let mut process = mailbox_process();
        for (width, height) in [(0, 600), (800, 0), (8193, 600), (800, 8193)] {
            assert!(process
                .update_viewport(crate::protocol::Bounds {
                    x: 0,
                    y: 0,
                    width,
                    height
                })
                .is_err());
        }
        assert!(!process.session_dir.join("duel.json.control.json").exists());
    }

    #[test]
    fn accepts_safe_duel_ids() {
        assert_eq!(safe_duel_id("abc-123_test").unwrap(), "abc-123_test");
    }

    #[test]
    fn rejects_path_like_duel_ids() {
        assert!(safe_duel_id("../../escape").is_err());
        assert!(safe_duel_id("").is_err());
    }

    #[test]
    fn decodes_ready_status() {
        let status: SlippiDuelStatus = serde_json::from_slice(
            br#"{"duel_id":"abc-123","phase":"ready","winner_idx":null,"local_won":null}"#,
        )
        .unwrap();
        assert_eq!(status.phase, "ready");
        assert_eq!(status.winner_idx, None);
        assert_eq!(status.local_won, None);
    }
}
