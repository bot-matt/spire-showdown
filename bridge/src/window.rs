use crate::protocol::Bounds;

#[cfg(target_os = "linux")]
mod linux;
#[cfg(target_os = "windows")]
mod windows;

pub trait WindowEmbedder {
    fn attach(&mut self, parent_handle: u64, child_pid: u32, bounds: Bounds) -> Result<(), String>;
    fn detach(&mut self) -> Result<(), String>;
}

#[cfg(target_os = "linux")]
pub fn platform_embedder() -> Box<dyn WindowEmbedder + Send> {
    match linux::LinuxEmbedder::new() {
        Ok(embedder) => Box::new(embedder),
        Err(problem) => Box::new(UnavailableEmbedder(problem)),
    }
}

#[cfg(target_os = "windows")]
pub fn platform_embedder() -> Box<dyn WindowEmbedder + Send> {
    Box::new(windows::WindowsEmbedder::default())
}

#[cfg(not(any(target_os = "linux", target_os = "windows")))]
pub fn platform_embedder() -> Box<dyn WindowEmbedder + Send> {
    Box::new(UnavailableEmbedder(
        "window embedding is supported only on Bazzite/Linux and Windows".into(),
    ))
}

#[allow(dead_code)]
struct UnavailableEmbedder(String);

impl WindowEmbedder for UnavailableEmbedder {
    fn attach(
        &mut self,
        _parent_handle: u64,
        _child_pid: u32,
        _bounds: Bounds,
    ) -> Result<(), String> {
        Err(self.0.clone())
    }

    fn detach(&mut self) -> Result<(), String> {
        Ok(())
    }
}
