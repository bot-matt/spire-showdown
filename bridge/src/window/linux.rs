use std::thread;
use std::time::{Duration, Instant};

use x11rb::connection::Connection;
use x11rb::errors::ReplyError;
use x11rb::protocol::xproto::{
    Atom, AtomEnum, ChangeWindowAttributesAux, ConfigureWindowAux, ConnectionExt, InputFocus,
    MapState, PropMode, Window,
};
use x11rb::protocol::ErrorKind;
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
    host_parent: Option<Window>,
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
            .reply();
        let Some(tree) = live_window_reply(tree)? else {
            return Ok(None);
        };
        for child in tree.children {
            let property = self
                .connection
                .get_property(false, child, pid_atom, AtomEnum::CARDINAL, 0, 1)
                .map_err(display_error)?
                .reply();
            let Some(property) = live_window_reply(property)? else {
                continue;
            };
            let window_pid = property.value32().and_then(|mut values| values.next());
            // AppImage's extract-and-run runtime remains as the process that
            // Command spawned and starts AppRun/Dolphin as a child. Match that
            // process tree, not only the wrapper PID returned by Command.
            let matches_pid = window_pid.is_some_and(|value| pid_belongs_to(value, pid));
            if matches_pid {
                let attributes = self
                    .connection
                    .get_window_attributes(child)
                    .map_err(display_error)?
                    .reply();
                let Some(_attributes) = live_window_reply(attributes)? else {
                    continue;
                };
                let name_atom = self
                    .connection
                    .intern_atom(false, b"_NET_WM_NAME")
                    .map_err(display_error)?
                    .reply()
                    .map_err(display_error)?
                    .atom;
                let name = self
                    .connection
                    .get_property(false, child, name_atom, AtomEnum::ANY, 0, 128)
                    .map_err(display_error)?
                    .reply();
                let Some(name) = live_window_reply(name)? else {
                    continue;
                };
                // The arena deliberately starts hidden/offscreen. Wine may
                // expose its rendering surface as UNMAPPED before reparenting.
                // Exact title + private ownership is the readiness criterion;
                // requiring VIEWABLE deadlocks the launch/reveal handshake.
                if name.value == b"Spire Showdown Arena" {
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

// Desktop windows may vanish between QueryTree and GetProperty. That is an
// ordinary discovery race, not failure of the arena we are waiting for.
fn live_window_reply<T>(reply: Result<T, ReplyError>) -> Result<Option<T>, String> {
    match reply {
        Ok(value) => Ok(Some(value)),
        Err(ReplyError::X11Error(error)) if error.error_kind == ErrorKind::Window => Ok(None),
        Err(error) => Err(display_error(error)),
    }
}

fn pid_belongs_to(candidate: u32, root: u32) -> bool {
    // Proton's Wine processes can be reparented to the user service manager.
    // A private per-duel prefix remains a stable ownership boundary even
    // when normal process ancestry no longer leads back to the launcher.
    if same_private_wine_prefix(candidate, root) {
        return true;
    }
    let mut current = candidate;
    for _ in 0..32 {
        if current == root {
            return true;
        }
        let Ok(status) = std::fs::read_to_string(format!("/proc/{current}/status")) else {
            return false;
        };
        let Some(parent) = status.lines().find_map(|line| {
            line.strip_prefix("PPid:")
                .and_then(|value| value.trim().parse::<u32>().ok())
        }) else {
            return false;
        };
        if parent == 0 || parent == current {
            return false;
        }
        current = parent;
    }
    false
}

fn process_env(pid: u32, key: &[u8]) -> Option<std::ffi::OsString> {
    use std::os::unix::ffi::OsStringExt;
    let bytes = std::fs::read(format!("/proc/{pid}/environ")).ok()?;
    bytes.split(|b| *b == 0).find_map(|entry| {
        let value = entry.strip_prefix(key)?.strip_prefix(b"=")?;
        Some(std::ffi::OsString::from_vec(value.to_vec()))
    })
}

fn same_private_wine_prefix(candidate: u32, root: u32) -> bool {
    let Some(compat) = process_env(root, b"STEAM_COMPAT_DATA_PATH") else {
        return false;
    };
    let compat = std::path::PathBuf::from(compat);
    // Never expand this rule to a shared Steam prefix or arbitrary Wine app.
    if !compat.starts_with(std::env::temp_dir().join("spire-showdown")) {
        return false;
    }
    let Some(prefix) = process_env(candidate, b"WINEPREFIX") else {
        return false;
    };
    let expected = compat.join("pfx").canonicalize();
    let actual = std::path::PathBuf::from(prefix).canonicalize();
    matches!((expected,actual),(Ok(a),Ok(b)) if a==b)
}

impl WindowEmbedder for LinuxEmbedder {
    fn prepare(&mut self, child_pid: u32) -> Result<(), String> {
        self.detach()?;
        let child = self.find_window_for_pid(child_pid, Duration::from_secs(45))?;
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
        // A Proton arena is already offscreen. An early XUnmap can make Wine
        // treat its swap chain as minimized throughout boot. Keep it rendering
        // until attachment; the normal Dolphin path still needs early hiding.
        let private_arena = process_env(child_pid, b"STEAM_COMPAT_DATA_PATH").is_some_and(|p| {
            std::path::PathBuf::from(p).starts_with(std::env::temp_dir().join("spire-showdown"))
        });
        if !private_arena {
            self.connection.unmap_window(child).map_err(display_error)?;
        }
        self.connection.flush().map_err(display_error)?;
        self.prepared = Some(PreparedWindow {
            child,
            original_parent,
            was_viewable,
            host_parent: None,
        });
        Ok(())
    }

    fn attach(&mut self, parent_handle: u64, child_pid: u32, bounds: Bounds) -> Result<(), String> {
        if self.attached.is_some() {
            self.detach()?;
        }
        let parent = u32::try_from(parent_handle)
            .map_err(|_| "Godot supplied an invalid X11 parent handle".to_string())?;
        let mut prepared = match self.prepared.take() {
            Some(value) => value,
            None => {
                let child = self.find_window_for_pid(child_pid, Duration::from_secs(45))?;
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
                    host_parent: None,
                }
            }
        };
        let child = prepared.child;
        self.remove_decorations(child)?;
        // Stop the desktop window manager from reclaiming or fullscreening
        // this surface after it becomes a Godot child window.
        self.connection
            .change_window_attributes(
                child,
                &ChangeWindowAttributesAux::new().override_redirect(1),
            )
            .map_err(display_error)?
            .check()
            .map_err(display_error)?;
        self.connection
            .reparent_window(child, parent, bounds.x as i16, bounds.y as i16)
            .map_err(display_error)?
            .check()
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
            .map_err(display_error)?
            .check()
            .map_err(display_error)?;
        if self
            .connection
            .query_tree(child)
            .map_err(display_error)?
            .reply()
            .map_err(display_error)?
            .parent
            != parent
        {
            return Err("Slippi's arena did not attach to the Spire window".into());
        }
        self.connection.flush().map_err(display_error)?;
        prepared.host_parent = Some(parent);
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

    fn resize(&mut self, bounds: Bounds) -> Result<(), String> {
        let Some(attached) = self.attached.as_ref() else {
            return Ok(());
        };
        let geometry = self
            .connection
            .get_geometry(attached.child)
            .map_err(display_error)?
            .reply()
            .map_err(display_error)?;
        if i32::from(geometry.x) != bounds.x
            || i32::from(geometry.y) != bounds.y
            || u32::from(geometry.width) != bounds.width
            || u32::from(geometry.height) != bounds.height
        {
            self.connection
                .configure_window(
                    attached.child,
                    &ConfigureWindowAux::new()
                        .x(bounds.x)
                        .y(bounds.y)
                        .width(bounds.width)
                        .height(bounds.height)
                        .border_width(0),
                )
                .map_err(display_error)?
                .check()
                .map_err(display_error)?;
            self.connection.flush().map_err(display_error)?;
        }
        Ok(())
    }

    fn is_alive(&self) -> bool {
        self.attached.as_ref().is_none_or(|attached| {
            self.connection
                .get_window_attributes(attached.child)
                .ok()
                .and_then(|cookie| cookie.reply().ok())
                .is_some()
        })
    }

    fn reveal(&mut self, native_keyboard: bool) -> Result<(), String> {
        let attached = self.attached.as_ref().ok_or("Slippi is not embedded")?;
        self.connection
            .map_window(attached.child)
            .map_err(display_error)?
            .check()
            .map_err(display_error)?;
        // Godot must keep receiving input when it forwards the Spire gamepad.
        // Only native keyboard bindings need focus inside Wine/Dolphin.
        let target = if native_keyboard {
            attached.child
        } else {
            attached.host_parent.ok_or("Arena has no Spire parent")?
        };
        let focus = self
            .connection
            .set_input_focus(InputFocus::PARENT, target, x11rb::CURRENT_TIME)
            .map_err(display_error)?
            .check();
        // Mapping is asynchronous on XWayland. Controllers forwarded from
        // Spire do not need X keyboard focus; a transient BadMatch must not
        // cancel an otherwise successfully attached/revealed match.
        match focus {
            Ok(()) => {}
            Err(ReplyError::X11Error(error)) if error.error_kind == ErrorKind::Match => {}
            Err(error) => return Err(display_error(error)),
        }
        self.connection.flush().map_err(display_error)?;
        Ok(())
    }
}

fn display_error(error: impl std::fmt::Display) -> String {
    error.to_string()
}
