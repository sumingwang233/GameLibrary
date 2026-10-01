use std::{ffi::c_void, io, ptr};

#[link(name = "kernel32")]
extern "system" {
    fn GetModuleHandleW(name: *const u16) -> *mut c_void;
}

#[link(name = "user32")]
extern "system" {
    fn LoadImageW(
        instance: *mut c_void,
        name: *const u16,
        image_type: u32,
        width: i32,
        height: i32,
        flags: u32,
    ) -> *mut c_void;
    fn SendMessageW(hwnd: *mut c_void, message: u32, wparam: usize, lparam: isize) -> isize;
}

pub fn set_taskbar_icon(hwnd: *mut c_void) -> io::Result<()> {
    let module = unsafe { GetModuleHandleW(ptr::null()) };
    if module.is_null() {
        return Err(io::Error::last_os_error());
    }
    set_taskbar_icon_from_resource(hwnd, module)
}

fn set_taskbar_icon_from_resource(hwnd: *mut c_void, module: *mut c_void) -> io::Result<()> {
    // Tauri/Tao sets ICON_SMALL only. Load Tauri's EXE resource 32512 for ICON_BIG;
    // LR_SHARED keeps the handle alive for the process without owning/destroying it.
    unsafe {
        let icon = LoadImageW(module, 32512usize as *const u16, 1, 0, 0, 0x8040);
        if icon.is_null() {
            return Err(io::Error::last_os_error());
        }
        SendMessageW(hwnd, 0x0080, 1, icon as isize);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[link(name = "user32")]
    extern "system" {
        fn CreateWindowExW(
            extended_style: u32,
            class: *const u16,
            title: *const u16,
            style: u32,
            x: i32,
            y: i32,
            width: i32,
            height: i32,
            parent: *mut c_void,
            menu: *mut c_void,
            instance: *mut c_void,
            parameter: *mut c_void,
        ) -> *mut c_void;
        fn DestroyWindow(hwnd: *mut c_void) -> i32;
    }

    #[test]
    fn sets_big_icon_from_module_resource() {
        // The built-in STATIC class avoids a custom registration and stays hidden.
        unsafe {
            let class: Vec<u16> = "STATIC\0".encode_utf16().collect();
            let hwnd = CreateWindowExW(
                0,
                class.as_ptr(),
                ptr::null(),
                0,
                0,
                0,
                32,
                32,
                ptr::null_mut(),
                ptr::null_mut(),
                GetModuleHandleW(ptr::null()),
                ptr::null_mut(),
            );
            assert!(!hwnd.is_null(), "{}", io::Error::last_os_error());
            // Cargo's unit-test EXE has no application resources. A null module
            // selects Windows' stock 32512 icon (IDI_APPLICATION) for this check.
            let result = set_taskbar_icon_from_resource(hwnd, ptr::null_mut());
            let icon = SendMessageW(hwnd, 0x007F, 1, 0);
            DestroyWindow(hwnd);
            result.unwrap();
            assert_ne!(icon, 0, "WM_GETICON must return ICON_BIG after startup");
        }
    }
}
