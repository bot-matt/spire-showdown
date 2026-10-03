use crate::protocol::Bounds;

#[cfg(target_os = "linux")]
mod linux;
#[cfg(target_os = "windows")]
mod windows;

pub trait WindowEmbedder {
    fn prepare(&mut self, child_pid: u32) -> Result<(), String>;
    fn attach(&mut self, parent_handle: u64, child_pid: u32, bounds: Bounds) -> Result<(), String>;
    fn detach(&mut self) -> Result<(), String>;
    fn resize(&mut self, bounds: Bounds) -> Result<(), String>;
    fn reveal(&mut self, native_keyboard: bool) -> Result<(), String>;
    fn is_alive(&self) -> bool {
        true
    }
    fn forget(&mut self);
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
    fn prepare(&mut self, _child_pid: u32) -> Result<(), String> {
        Err(self.0.clone())
    }

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

    fn forget(&mut self) {}
    fn reveal(&mut self, _native_keyboard: bool) -> Result<(), String> {
        Err(self.0.clone())
    }

    fn resize(&mut self, _bounds: Bounds) -> Result<(), String> {
        Err(self.0.clone())
    }
}
