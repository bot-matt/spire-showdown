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
}

pub struct SlippiProcess {
    child: Child,
    duel_id: String,
    session_dir: PathBuf,
}

impl SlippiProcess {
    pub fn launch(slippi: &Path, iso: &Path, duel: &DuelSpec) -> Result<Self, String> {
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

        let mut command = Command::new(slippi);
        command
            .arg("--batch")
            .arg("--exec")
            .arg(iso)
            .arg("--spire-duel")
            .arg(&duel_file)
            .stdin(Stdio::null())
            .stdout(Stdio::from(stdout))
            .stderr(Stdio::from(stderr));

        // Foreign-surface reparenting is forbidden by native Wayland. Force
        // Slippi's Qt window through XWayland on Bazzite so it can be embedded.
        #[cfg(target_os = "linux")]
        command.env("QT_QPA_PLATFORM", "xcb");

        let child = command
            .spawn()
            .map_err(|error| format!("cannot launch Slippi: {error}"))?;
        Ok(Self {
            child,
            duel_id: duel.duel_id.clone(),
            session_dir,
        })
    }

    pub fn pid(&self) -> u32 {
        self.child.id()
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
        let status: SlippiDuelStatus = serde_json::from_slice(&encoded)
            .map_err(|error| format!("cannot decode Slippi duel status: {error}"))?;
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
        if self.try_wait()?.is_none() {
            self.child
                .kill()
                .map_err(|error| format!("cannot stop Slippi: {error}"))?;
            self.child
                .wait()
                .map_err(|error| format!("cannot wait for Slippi to stop: {error}"))?;
        }
        Ok(())
    }
}

impl Drop for SlippiProcess {
    fn drop(&mut self) {
        let _ = self.stop();
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
        let status: SlippiDuelStatus =
            serde_json::from_slice(br#"{"duel_id":"abc-123","phase":"ready","winner_idx":null}"#)
                .unwrap();
        assert_eq!(status.phase, "ready");
        assert_eq!(status.winner_idx, None);
    }
}
