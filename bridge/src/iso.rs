use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct IsoReport {
    pub game_id: Option<String>,
    pub revision: Option<u8>,
    pub size_bytes: Option<u64>,
    pub quick_fingerprint: Option<String>,
    pub problem: Option<String>,
}

pub fn inspect_iso(path: &Path) -> IsoReport {
    match inspect_iso_inner(path) {
        Ok(report) => report,
        Err(problem) => IsoReport {
            game_id: None,
            revision: None,
            size_bytes: None,
            quick_fingerprint: None,
            problem: Some(problem),
        },
    }
}

fn inspect_iso_inner(path: &Path) -> Result<IsoReport, String> {
    let mut file = File::open(path).map_err(|error| format!("Cannot open Melee image: {error}"))?;
    let size = file
        .metadata()
        .map_err(|error| format!("Cannot inspect Melee image: {error}"))?
        .len();
    let mut header = [0_u8; 8];
    file.read_exact(&mut header)
        .map_err(|error| format!("Cannot read Melee image header: {error}"))?;
    if &header[..6] != b"GALE01" {
        return Err("Selected image is not a supported NTSC Melee disc (expected GALE01)".into());
    }
    let revision = header[7];
    if revision != 2 {
        return Err(format!(
            "Selected Melee image is revision 1.{revision:02}; Slippi requires NTSC 1.02"
        ));
    }

    const SAMPLE_SIZE: u64 = 4 * 1024 * 1024;
    let mut hasher = Sha256::new();
    hasher.update(header);
    hasher.update(size.to_le_bytes());
    hash_sample(&mut file, 8, SAMPLE_SIZE, &mut hasher)?;
    hash_sample(
        &mut file,
        size.saturating_sub(SAMPLE_SIZE),
        SAMPLE_SIZE,
        &mut hasher,
    )?;

    Ok(IsoReport {
        game_id: Some("GALE01".into()),
        revision: Some(revision),
        size_bytes: Some(size),
        quick_fingerprint: Some(hex::encode(hasher.finalize())),
        problem: None,
    })
}

fn hash_sample(
    file: &mut File,
    offset: u64,
    length: u64,
    hasher: &mut Sha256,
) -> Result<(), String> {
    file.seek(SeekFrom::Start(offset))
        .map_err(|error| format!("Cannot seek in Melee image: {error}"))?;
    let mut sample = file.take(length);
    std::io::copy(&mut sample, hasher)
        .map_err(|error| format!("Cannot fingerprint Melee image: {error}"))?;
    Ok(())
}
