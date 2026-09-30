use crate::protocol::Bounds;

#[allow(dead_code)]
pub trait WindowEmbedder {
    fn attach(&mut self, parent_handle: u64, child_pid: u32, bounds: Bounds) -> Result<(), String>;
    fn detach(&mut self) -> Result<(), String>;
}

/// The release implementation is selected at compile time:
/// Win32 uses SetParent/style restoration, while Bazzite uses XWayland/X11
/// re-parenting. The trait is present now so bridge and protocol work can
/// proceed without leaking platform handles into duel logic.
#[allow(dead_code)]
pub struct PendingPlatformEmbedder;

impl WindowEmbedder for PendingPlatformEmbedder {
    fn attach(
        &mut self,
        _parent_handle: u64,
        _child_pid: u32,
        _bounds: Bounds,
    ) -> Result<(), String> {
        Err("platform embedder is not implemented yet".into())
    }

    fn detach(&mut self) -> Result<(), String> {
        Ok(())
    }
}
