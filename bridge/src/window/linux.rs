use std::thread;
use std::time::{Duration, Instant};

use x11rb::connection::Connection;
use x11rb::protocol::xproto::{
    Atom, AtomEnum, ConfigureWindowAux, ConnectionExt, MapState, PropMode, Window,
};
use x11rb::rust_connection::RustConnection;
use x11rb::wrapper::ConnectionExt as _;

use super::WindowEmbedder;
use crate::protocol::Bounds;

pub struct LinuxEmbedder {
    connection: RustConnection,
    screen_num: usize,
    prepared: Option<PreparedWindow>,
    attached: Option<AttachedWindow>,
}

struct PreparedWindow {
    child: Window,
    original_parent: Window,
    was_viewable: bool,
}

type AttachedWindow = PreparedWindow;

impl LinuxEmbedder {
    pub fn new() -> Result<Self, String> {
        let (connection, screen_num) = x11rb::connect(None)
            .map_err(|error| format!("cannot connect to XWayland/X11: {error}"))?;
        Ok(Self {
            connection,
            screen_num,
            prepared: None,
            attached: None,
        })
    }

    fn root(&self) -> Window {
        self.connection.setup().roots[self.screen_num].root
    }

    fn find_window_for_pid(&self, pid: u32, timeout: Duration) -> Result<Window, String> {
        let pid_atom = self
            .connection
            .intern_atom(false, b"_NET_WM_PID")
            .map_err(display_error)?
            .reply()
            .map_err(display_error)?
            .atom;
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(window) = self.find_window_recursive(self.root(), pid_atom, pid, 0)? {
                return Ok(window);
            }
            if Instant::now() >= deadline {
                return Err(format!(
                    "timed out waiting for X11 window owned by PID {pid}"
                ));
            }
            thread::sleep(Duration::from_millis(100));
        }
    }

    fn find_window_recursive(
        &self,
        parent: Window,
        pid_atom: Atom,
        pid: u32,
        depth: u8,
    ) -> Result<Option<Window>, String> {
        if depth > 12 {
            return Ok(None);
        }
        let tree = self
            .connection
            .query_tree(parent)
            .map_err(display_error)?
            .reply()
            .map_err(display_error)?;
        for child in tree.children {
            let property = self
                .connection
                .get_property(false, child, pid_atom, AtomEnum::CARDINAL, 0, 1)
                .map_err(display_error)?
                .reply()
                .map_err(display_error)?;
            let matches_pid = property.value32().and_then(|mut values| values.next()) == Some(pid);
            if matches_pid {
                let attributes = self
                    .connection
                    .get_window_attributes(child)
                    .map_err(display_error)?
                    .reply()
                    .map_err(display_error)?;
                if attributes.map_state == MapState::VIEWABLE {
                    return Ok(Some(child));
                }
            }
            if let Some(found) = self.find_window_recursive(child, pid_atom, pid, depth + 1)? {
                return Ok(Some(found));
            }
        }
        Ok(None)
    }

    fn remove_decorations(&self, window: Window) -> Result<(), String> {
        let motif_atom = self
            .connection
            .intern_atom(false, b"_MOTIF_WM_HINTS")
            .map_err(display_error)?
            .reply()
            .map_err(display_error)?
            .atom;
        // flags=MWM_HINTS_DECORATIONS, decorations=0
        let hints = [2_u32, 0, 0, 0, 0];
        self.connection
            .change_property32(PropMode::REPLACE, window, motif_atom, motif_atom, &hints)
            .map_err(display_error)?;
        Ok(())
    }
}

impl WindowEmbedder for LinuxEmbedder {
    fn prepare(&mut self, child_pid: u32) -> Result<(), String> {
        self.detach()?;
        let child = self.find_window_for_pid(child_pid, Duration::from_secs(10))?;
        let original_parent = self
            .connection
            .query_tree(child)
            .map_err(display_error)?
            .reply()
            .map_err(display_error)?
            .parent;
        let was_viewable = self
            .connection
            .get_window_attributes(child)
            .map_err(display_error)?
            .reply()
            .map_err(display_error)?
            .map_state
            == MapState::VIEWABLE;
        self.connection.unmap_window(child).map_err(display_error)?;
        self.connection.flush().map_err(display_error)?;
        self.prepared = Some(PreparedWindow {
            child,
            original_parent,
            was_viewable,
        });
        Ok(())
    }

    fn attach(&mut self, parent_handle: u64, child_pid: u32, bounds: Bounds) -> Result<(), String> {
        if self.attached.is_some() {
            self.detach()?;
        }
        let parent = u32::try_from(parent_handle)
            .map_err(|_| "Godot supplied an invalid X11 parent handle".to_string())?;
        let prepared = match self.prepared.take() {
            Some(value) => value,
            None => {
                let child = self.find_window_for_pid(child_pid, Duration::from_secs(10))?;
                let original_parent = self
                    .connection
                    .query_tree(child)
                    .map_err(display_error)?
                    .reply()
                    .map_err(display_error)?
                    .parent;
                PreparedWindow {
                    child,
                    original_parent,
                    was_viewable: true,
                }
            }
        };
        let child = prepared.child;
        self.remove_decorations(child)?;
        self.connection
            .reparent_window(child, parent, bounds.x as i16, bounds.y as i16)
            .map_err(display_error)?;
        self.connection
            .configure_window(
                child,
                &ConfigureWindowAux::new()
                    .x(bounds.x)
                    .y(bounds.y)
                    .width(bounds.width)
                    .height(bounds.height)
                    .border_width(0),
            )
            .map_err(display_error)?;
        self.connection.map_window(child).map_err(display_error)?;
        self.connection.flush().map_err(display_error)?;
        self.attached = Some(prepared);
        Ok(())
    }

    fn detach(&mut self) -> Result<(), String> {
        if let Some(attached) = self.attached.take() {
            self.connection
                .reparent_window(attached.child, attached.original_parent, 0, 0)
                .map_err(display_error)?;
            if attached.was_viewable {
                self.connection
                    .map_window(attached.child)
                    .map_err(display_error)?;
            }
            self.connection.flush().map_err(display_error)?;
        }
        if let Some(prepared) = self.prepared.take() {
            if prepared.was_viewable {
                self.connection
                    .map_window(prepared.child)
                    .map_err(display_error)?;
            }
            self.connection.flush().map_err(display_error)?;
        }
        Ok(())
    }

    fn forget(&mut self) {
        self.prepared = None;
        self.attached = None;
    }
}

fn display_error(error: impl std::fmt::Display) -> String {
    error.to_string()
}
