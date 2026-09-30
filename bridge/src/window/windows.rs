use std::ptr;
use std::thread;
use std::time::{Duration, Instant};

use windows_sys::core::BOOL;
use windows_sys::Win32::Foundation::{HWND, LPARAM};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetParent, GetWindowLongPtrW, GetWindowThreadProcessId, IsWindowVisible,
    SetParent, SetWindowLongPtrW, SetWindowPos, GWL_STYLE, SWP_FRAMECHANGED, SWP_NOACTIVATE,
    SWP_NOZORDER, WS_CAPTION, WS_CHILD, WS_POPUP, WS_THICKFRAME,
};

use super::WindowEmbedder;
use crate::protocol::Bounds;

#[derive(Default)]
pub struct WindowsEmbedder {
    attached: Option<AttachedWindow>,
}

struct AttachedWindow {
    child: usize,
    original_parent: usize,
    original_style: isize,
}

struct FindContext {
    pid: u32,
    found: HWND,
}

unsafe extern "system" fn enum_window(hwnd: HWND, parameter: LPARAM) -> BOOL {
    let context = &mut *(parameter as *mut FindContext);
    let mut window_pid = 0_u32;
    GetWindowThreadProcessId(hwnd, &mut window_pid);
    if window_pid == context.pid && IsWindowVisible(hwnd) != 0 {
        context.found = hwnd;
        return 0;
    }
    1
}

impl WindowsEmbedder {
    fn find_window(pid: u32, timeout: Duration) -> Result<HWND, String> {
        let deadline = Instant::now() + timeout;
        loop {
            let mut context = FindContext {
                pid,
                found: ptr::null_mut(),
            };
            unsafe {
                EnumWindows(
                    Some(enum_window),
                    &mut context as *mut FindContext as LPARAM,
                );
            }
            if !context.found.is_null() {
                return Ok(context.found);
            }
            if Instant::now() >= deadline {
                return Err(format!(
                    "timed out waiting for Win32 window owned by PID {pid}"
                ));
            }
            thread::sleep(Duration::from_millis(100));
        }
    }
}

impl WindowEmbedder for WindowsEmbedder {
    fn attach(&mut self, parent_handle: u64, child_pid: u32, bounds: Bounds) -> Result<(), String> {
        self.detach()?;
        let parent = parent_handle as usize as HWND;
        if parent.is_null() {
            return Err("Godot supplied an empty Win32 parent handle".into());
        }
        let child = Self::find_window(child_pid, Duration::from_secs(10))?;
        unsafe {
            let original_parent = GetParent(child);
            let original_style = GetWindowLongPtrW(child, GWL_STYLE);
            let style =
                (original_style as u32 & !(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD;
            SetWindowLongPtrW(child, GWL_STYLE, style as isize);
            SetParent(child, parent);
            if SetWindowPos(
                child,
                ptr::null_mut(),
                bounds.x,
                bounds.y,
                bounds.width as i32,
                bounds.height as i32,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED,
            ) == 0
            {
                SetParent(child, original_parent);
                SetWindowLongPtrW(child, GWL_STYLE, original_style);
                return Err("SetWindowPos failed while embedding Slippi".into());
            }
            self.attached = Some(AttachedWindow {
                child: child as usize,
                original_parent: original_parent as usize,
                original_style,
            });
        }
        Ok(())
    }

    fn detach(&mut self) -> Result<(), String> {
        if let Some(attached) = self.attached.take() {
            unsafe {
                let child = attached.child as HWND;
                SetParent(child, attached.original_parent as HWND);
                SetWindowLongPtrW(child, GWL_STYLE, attached.original_style);
                SetWindowPos(
                    child,
                    ptr::null_mut(),
                    0,
                    0,
                    0,
                    0,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED,
                );
            }
        }
        Ok(())
    }
}
