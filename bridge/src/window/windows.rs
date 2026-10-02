use std::ptr;
use std::thread;
use std::time::{Duration, Instant};

use windows_sys::core::BOOL;
use windows_sys::Win32::Foundation::{HWND, LPARAM};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetParent, GetWindowLongPtrW, GetWindowTextW, GetWindowThreadProcessId, IsWindow,
    IsWindowVisible, SetParent, SetWindowLongPtrW, SetWindowPos, ShowWindow, GWL_STYLE,
    SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOZORDER, SW_HIDE, SW_SHOW, WS_CAPTION, WS_CHILD,
    WS_POPUP, WS_THICKFRAME,
};

use super::WindowEmbedder;
use crate::protocol::Bounds;

#[derive(Default)]
pub struct WindowsEmbedder {
    prepared: Option<PreparedWindow>,
    attached: Option<AttachedWindow>,
}

struct PreparedWindow {
    child: usize,
    original_parent: usize,
    original_style: isize,
    was_visible: bool,
}

type AttachedWindow = PreparedWindow;

struct FindContext {
    pid: u32,
    found: HWND,
}

unsafe extern "system" fn enum_window(hwnd: HWND, parameter: LPARAM) -> BOOL {
    let context = &mut *(parameter as *mut FindContext);
    let mut window_pid = 0_u32;
    GetWindowThreadProcessId(hwnd, &mut window_pid);
    let mut title = [0_u16; 128];
    let length = GetWindowTextW(hwnd, title.as_mut_ptr(), title.len() as i32);
    if window_pid == context.pid
        && IsWindowVisible(hwnd) != 0
        && String::from_utf16_lossy(&title[..length.max(0) as usize]) == "Spire Showdown Arena"
    {
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
    fn prepare(&mut self, child_pid: u32) -> Result<(), String> {
        self.detach()?;
        let child = Self::find_window(child_pid, Duration::from_secs(10))?;
        unsafe {
            let prepared = PreparedWindow {
                child: child as usize,
                original_parent: GetParent(child) as usize,
                original_style: GetWindowLongPtrW(child, GWL_STYLE),
                was_visible: IsWindowVisible(child) != 0,
            };
            ShowWindow(child, SW_HIDE);
            self.prepared = Some(prepared);
        }
        Ok(())
    }

    fn attach(&mut self, parent_handle: u64, child_pid: u32, bounds: Bounds) -> Result<(), String> {
        if self.attached.is_some() {
            self.detach()?;
        }
        let parent = parent_handle as usize as HWND;
        if parent.is_null() {
            return Err("Godot supplied an empty Win32 parent handle".into());
        }
        let prepared = match self.prepared.take() {
            Some(value) => value,
            None => {
                let child = Self::find_window(child_pid, Duration::from_secs(10))?;
                unsafe {
                    PreparedWindow {
                        child: child as usize,
                        original_parent: GetParent(child) as usize,
                        original_style: GetWindowLongPtrW(child, GWL_STYLE),
                        was_visible: IsWindowVisible(child) != 0,
                    }
                }
            }
        };
        let child = prepared.child as HWND;
        unsafe {
            let style = (prepared.original_style as u32 & !(WS_POPUP | WS_CAPTION | WS_THICKFRAME))
                | WS_CHILD;
            SetWindowLongPtrW(child, GWL_STYLE, style as isize);
            SetParent(child, parent);
            if GetParent(child) != parent {
                SetWindowLongPtrW(child, GWL_STYLE, prepared.original_style);
                return Err("SetParent failed while embedding Slippi".into());
            }
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
                SetParent(child, prepared.original_parent as HWND);
                SetWindowLongPtrW(child, GWL_STYLE, prepared.original_style);
                if prepared.was_visible {
                    ShowWindow(child, SW_SHOW);
                }
                return Err("SetWindowPos failed while embedding Slippi".into());
            }
            ShowWindow(child, SW_SHOW);
            self.attached = Some(prepared);
        }
        Ok(())
    }

    fn detach(&mut self) -> Result<(), String> {
        // This path is for surviving surfaces; normal duel cleanup kills the process.
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
                if attached.was_visible {
                    ShowWindow(child, SW_SHOW);
                }
            }
        }
        if let Some(prepared) = self.prepared.take() {
            unsafe {
                if prepared.was_visible {
                    ShowWindow(prepared.child as HWND, SW_SHOW);
                }
            }
        }
        Ok(())
    }

    fn forget(&mut self) {
        self.prepared = None;
        self.attached = None;
    }

    fn resize(&mut self, bounds: Bounds) -> Result<(), String> {
        if let Some(attached) = self.attached.as_ref() {
            unsafe {
                if SetWindowPos(
                    attached.child as HWND,
                    ptr::null_mut(),
                    bounds.x,
                    bounds.y,
                    bounds.width as i32,
                    bounds.height as i32,
                    SWP_NOZORDER | SWP_NOACTIVATE,
                ) == 0
                {
                    return Err("cannot resize embedded Slippi arena".into());
                }
            }
        }
        Ok(())
    }

    fn is_alive(&self) -> bool {
        self.attached
            .as_ref()
            .is_none_or(|attached| unsafe { IsWindow(attached.child as HWND) != 0 })
    }
}
