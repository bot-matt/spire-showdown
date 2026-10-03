//! Validated opt-in Melee Unlocked backend. Stock upstream builds are not
//! accepted: they cannot promise one-stock boot, lab view, or winner reporting.
use crate::discovery::{DiscoveryReport, LocatedPath, Source};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ArenaConfig {
    pub executable: Option<PathBuf>,
    pub proton: Option<PathBuf>,
    pub user_dir: Option<PathBuf>,
}

impl ArenaConfig {
    pub fn resolve(mut self, report: &mut DiscoveryReport) -> Result<Self, String> {
        let executable = self
            .executable
            .take()
            .or_else(|| {
                std::env::current_exe()
                    .ok()?
                    .parent()
                    .map(|p| p.join("arena/melee_port.exe"))
            })
            .ok_or("Select the Spire-compatible Melee Unlocked executable")?;
        let executable = executable
            .canonicalize()
            .map_err(|_| "Melee Unlocked executable not found")?;
        if !executable.is_file() {
            return Err("Melee Unlocked executable is not a file".into());
        }
        let root = executable.parent().ok_or("Arena directory missing")?;
        let manifest: serde_json::Value =
            serde_json::from_slice(&std::fs::read(root.join("SpireArena.json")).map_err(|_| {
                "This is a stock Melee Unlocked build; install the Spire integration build"
            })?)
            .map_err(|_| "Invalid SpireArena.json")?;
        if manifest.get("protocol").and_then(|v| v.as_u64()) != Some(1)
            || manifest.get("engine").and_then(|v| v.as_str()) != Some("static_recomp")
        {
            return Err("Arena integration protocol or engine is incompatible".into());
        }
        if !root.join("Sys/GameFiles/GALE01").is_dir() || !root.join("Lab").is_dir() {
            return Err("Arena package is incomplete (Sys or Lab missing)".into());
        }
        #[cfg(target_os = "linux")]
        {
            self.proton = self.proton.take().or_else(locate_proton);
            if !self.proton.as_ref().is_some_and(|p| p.is_file()) {
                return Err("Proton not found; install Proton Experimental in Steam or select its proton script".into());
            }
        }
        self.user_dir = self.user_dir.take().or_else(|| {
            crate::discovery::slippi_user_paths()
                .into_iter()
                .find(|p| p.is_file())?
                .parent()
                .map(Path::to_path_buf)
        });
        report.slippi = Some(LocatedPath {
            path: executable.clone(),
            source: Source::Explicit,
        });
        report
            .problems
            .retain(|p| !p.starts_with("Slippi Dolphin was not found"));
        report.ready =
            report.melee_iso.is_some() && report.iso.as_ref().is_some_and(|i| i.problem.is_none());
        self.executable = Some(executable);
        Ok(self)
    }
}

#[cfg(target_os = "linux")]
fn locate_proton() -> Option<PathBuf> {
    let home = PathBuf::from(std::env::var_os("HOME")?);
    for root in [home.join(".local/share/Steam"), home.join(".steam/steam")] {
        let common = root.join("steamapps/common");
        for name in ["Proton - Experimental", "Proton 10.0", "Proton 9.0"] {
            let p = common.join(name).join("proton");
            if p.is_file() {
                return Some(p);
            }
        }
    }
    None
}

pub fn validate_duel(duel: &crate::protocol::DuelSpec) -> Result<(), String> {
    if duel.stocks != 1
        || ![2, 3, 8, 28, 31, 32].contains(&duel.stage)
        || duel.local_character > 25
        || duel.remote_character > 25
    {
        return Err("Arena requires one stock, retail characters, and a legal stage".into());
    }
    if duel.cpu_test {
        return Ok(());
    }
    if !(2..=4).contains(&duel.participants.len()) {
        return Err("Arena requires 2-4 contenders".into());
    }
    let mut ids = std::collections::HashSet::new();
    for p in &duel.participants {
        if p.character > 25 || !ids.insert(p.player_id) {
            return Err("Invalid or duplicate contender".into());
        }
    }
    if duel.local_player_index as usize >= duel.participants.len() {
        return Err("Invalid local contender index".into());
    }
    if duel.participants.len() > 2 {
        let mut endpoints = std::collections::HashSet::new();
        for p in &duel.participants {
            let endpoint = p
                .endpoint
                .as_deref()
                .ok_or("FFA needs each contender's reachable UDP endpoint")?;
            let addr: std::net::SocketAddr = endpoint
                .parse()
                .map_err(|_| "FFA endpoints must be numeric IPv4:port (no hostnames)")?;
            if !addr.is_ipv4()
                || addr.port() == 0
                || addr.ip().is_unspecified()
                || addr.ip().is_multicast()
                || !endpoints.insert(addr)
            {
                return Err("Invalid or duplicate FFA endpoint".into());
            }
        }
    }
    Ok(())
}

pub fn local_peer_argument(duel: &crate::protocol::DuelSpec) -> Result<String, String> {
    validate_duel(duel)?;
    let local = duel.local_player_index as usize;
    let endpoint: std::net::SocketAddr = duel.participants[local]
        .endpoint
        .as_deref()
        .ok_or("Local endpoint missing")?
        .parse()
        .map_err(|_| "Invalid local endpoint")?;
    let remotes = duel
        .participants
        .iter()
        .enumerate()
        .filter(|(i, _)| *i != local)
        .map(|(_, p)| p.endpoint.as_deref().unwrap())
        .collect::<Vec<_>>()
        .join(",");
    Ok(format!("{local}:{}:{remotes}", endpoint.port()))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stock_build_is_rejected() {
        let dir = tempfile::tempdir().unwrap();
        let exe = dir.path().join("melee_port.exe");
        std::fs::write(&exe, []).unwrap();
        let mut report = crate::discovery::discover(None, None);
        assert!(ArenaConfig {
            executable: Some(exe),
            ..Default::default()
        }
        .resolve(&mut report)
        .unwrap_err()
        .contains("stock"));
    }
}
