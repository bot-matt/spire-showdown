use std::path::PathBuf;

use serde::{Deserialize, Serialize};

pub const VERSION: u16 = 1;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Envelope<T> {
    pub protocol: u16,
    pub request_id: String,
    pub token: String,
    #[serde(flatten)]
    pub message: T,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "payload", rename_all = "snake_case")]
pub enum Request {
    Hello,
    Preflight {
        slippi: Option<PathBuf>,
        iso: Option<PathBuf>,
    },
    StartDuel {
        duel: DuelSpec,
    },
    AttachWindow {
        parent_handle: u64,
        bounds: Bounds,
    },
    Status,
    CancelDuel {
        duel_id: String,
        reason: String,
    },
    Shutdown,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DuelSpec {
    pub duel_id: String,
    pub seed: u64,
    pub opponent_connect_code: String,
    pub local_character: u8,
    pub remote_character: u8,
    pub stage: u16,
    pub stocks: u8,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
pub struct Bounds {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Response {
    Hello {
        bridge_version: String,
        platform: String,
    },
    Preflight {
        report: crate::discovery::DiscoveryReport,
    },
    Status {
        state: crate::state::DuelState,
        duel_id: Option<String>,
        slippi_pid: Option<u32>,
    },
    Started {
        duel_id: String,
        slippi_pid: u32,
    },
    Accepted,
    Error {
        code: String,
        message: String,
        recoverable: bool,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ResponseEnvelope {
    pub protocol: u16,
    pub request_id: String,
    #[serde(flatten)]
    pub response: Response,
}
