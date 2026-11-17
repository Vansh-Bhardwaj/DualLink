//! Programs as people know them: the name and icon Windows shows for an .exe, and the
//! programs open on screen right now.

use rune::Pixels;
use std::collections::HashSet;
use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM};
use windows::Win32::Graphics::Gdi::{
    BI_RGB, BITMAP, BITMAPINFO, BITMAPINFOHEADER, DIB_RGB_COLORS, DeleteObject, GetDC, GetDIBits, GetObjectW, HGDIOBJ,
    ReleaseDC,
};
use windows::Win32::Storage::FileSystem::{GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW};
use windows::Win32::System::Threading::{
    OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
};
use windows::Win32::UI::Shell::SHDefExtractIconW;
use windows::Win32::UI::WindowsAndMessaging::{
    DestroyIcon, EnumWindows, GW_OWNER, GWL_EXSTYLE, GetIconInfo, GetWindow, GetWindowLongW, GetWindowTextLengthW,
    GetWindowThreadProcessId, HICON, ICONINFO, IsWindowVisible, WS_EX_TOOLWINDOW,
};
use windows::core::{BOOL, PCWSTR, PWSTR};

/// Icons are read at this many pixels, enough for a 32 pt tile on a 1.5× screen.
pub const ICON_PIXELS: u32 = 48;

/// What the window knows about one program.
#[derive(Clone)]
pub struct AppInfo {
    /// The full path, lower-cased: the key everything is looked up by.
    pub path: String,
    pub name: String,
    pub icon: Option<Pixels>,
}

impl AppInfo {
    /// Reads a program's name and icon. Slow (it opens the file), so call it off the UI thread.
    pub fn read(path: &str) -> AppInfo {
        let icon = icon(path).map(|(width, height, rgba)| {
            let pixels = Pixels::new();
            pixels.set(width, height, rgba);
            pixels
        });
        AppInfo { path: path.to_lowercase(), name: display_name(path), icon }
    }
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

/// The program's own description of itself ("Steam", "Microsoft Edge"), else its file name
/// without ".exe".
pub fn display_name(path: &str) -> String {
    let stem = || {
        let file = path.rsplit(['\\', '/']).next().unwrap_or(path);
        let stem = file.strip_suffix(".exe").or_else(|| file.strip_suffix(".EXE")).unwrap_or(file);
        let mut letters = stem.chars();
        letters.next().map_or_else(String::new, |first| first.to_uppercase().chain(letters).collect())
    };
    description(path).filter(|d| !d.is_empty() && d.len() <= 40).map(|d| spaced(&d)).unwrap_or_else(stem)
}

/// "EpicGamesLauncher" reads as "Epic Games Launcher"; names that already have spaces stay as they are.
fn spaced(name: &str) -> String {
    if name.contains(' ') {
        return name.to_string();
    }
    let mut out = String::with_capacity(name.len() + 4);
    let mut previous: Option<char> = None;
    for c in name.chars() {
        if c.is_uppercase() && previous.is_some_and(char::is_lowercase) {
            out.push(' ');
        }
        out.push(c);
        previous = Some(c);
    }
    out
}

fn description(path: &str) -> Option<String> {
    let file = wide(path);
    unsafe {
        let size = GetFileVersionInfoSizeW(PCWSTR(file.as_ptr()), None);
        if size == 0 {
            return None;
        }
        let mut block = vec![0u8; size as usize];
        GetFileVersionInfoW(PCWSTR(file.as_ptr()), None, size, block.as_mut_ptr().cast()).ok()?;

        // The first language the file lists, as "040904b0".
        let mut pointer = std::ptr::null_mut();
        let mut length = 0u32;
        let translation = wide(r"\VarFileInfo\Translation");
        let language = if VerQueryValueW(block.as_ptr().cast(), PCWSTR(translation.as_ptr()), &mut pointer, &mut length)
            .as_bool()
            && length >= 4
        {
            let pair = std::slice::from_raw_parts(pointer.cast::<u16>(), 2);
            format!("{:04x}{:04x}", pair[0], pair[1])
        } else {
            "040904b0".into()
        };
        let query = wide(&format!(r"\StringFileInfo\{language}\FileDescription"));
        if !VerQueryValueW(block.as_ptr().cast(), PCWSTR(query.as_ptr()), &mut pointer, &mut length).as_bool()
            || length == 0
        {
            return None;
        }
        let text = std::slice::from_raw_parts(pointer.cast::<u16>(), length as usize);
        let end = text.iter().position(|&c| c == 0).unwrap_or(text.len());
        Some(String::from_utf16_lossy(&text[..end]).trim().to_string())
    }
}

/// The program's icon as straight-alpha RGBA rows, top row first.
pub fn icon(path: &str) -> Option<(u32, u32, Vec<u8>)> {
    let file = wide(path);
    unsafe {
        let mut handle = HICON::default();
        SHDefExtractIconW(PCWSTR(file.as_ptr()), 0, 0, Some(&mut handle), None, ICON_PIXELS).ok().ok()?;
        if handle.is_invalid() {
            return None;
        }
        let pixels = icon_pixels(handle);
        let _ = DestroyIcon(handle);
        pixels
    }
}

unsafe fn icon_pixels(handle: HICON) -> Option<(u32, u32, Vec<u8>)> {
    unsafe {
        let mut info = ICONINFO::default();
        GetIconInfo(handle, &mut info).ok()?;
        let colour = HGDIOBJ(info.hbmColor.0);
        let mask = HGDIOBJ(info.hbmMask.0);
        let result = (|| {
            if info.hbmColor.is_invalid() {
                return None; // a monochrome icon; the tile's initial will do
            }
            let mut bitmap = BITMAP::default();
            if GetObjectW(colour, size_of::<BITMAP>() as i32, Some((&raw mut bitmap).cast())) == 0 {
                return None;
            }
            let (width, height) = (bitmap.bmWidth, bitmap.bmHeight);
            if width <= 0 || height <= 0 || width > 256 || height > 256 {
                return None;
            }
            let read = |source: windows::Win32::Graphics::Gdi::HBITMAP| {
                let mut header = BITMAPINFO {
                    bmiHeader: BITMAPINFOHEADER {
                        biSize: size_of::<BITMAPINFOHEADER>() as u32,
                        biWidth: width,
                        biHeight: -height, // top row first
                        biPlanes: 1,
                        biBitCount: 32,
                        biCompression: BI_RGB.0,
                        ..Default::default()
                    },
                    ..Default::default()
                };
                let mut bgra = vec![0u8; (width * height * 4) as usize];
                let dc = GetDC(None);
                let lines = GetDIBits(dc, source, 0, height as u32, Some(bgra.as_mut_ptr().cast()), &mut header, DIB_RGB_COLORS);
                ReleaseDC(None, dc);
                (lines == height).then_some(bgra)
            };
            let mut rgba = read(info.hbmColor)?;
            let has_alpha = rgba.as_chunks::<4>().0.iter().any(|p| p[3] != 0);
            // Older icons carry no alpha; their mask says which pixels are see-through.
            let see_through = if has_alpha { None } else { read(info.hbmMask) };
            for (index, pixel) in rgba.as_chunks_mut::<4>().0.iter_mut().enumerate() {
                pixel.swap(0, 2);
                if !has_alpha {
                    let hidden = see_through.as_ref().is_some_and(|m| m[index * 4] != 0);
                    pixel[3] = if hidden { 0 } else { 255 };
                }
            }
            Some((width as u32, height as u32, rgba))
        })();
        let _ = DeleteObject(colour);
        let _ = DeleteObject(mask);
        result
    }
}

/// The programs with a window on screen, as full paths, leaving out Windows' own parts.
pub fn open_now() -> Vec<String> {
    unsafe extern "system" fn each(window: HWND, found: LPARAM) -> BOOL {
        unsafe {
            let found = &mut *(found.0 as *mut Vec<u32>);
            let tool = GetWindowLongW(window, GWL_EXSTYLE) as u32 & WS_EX_TOOLWINDOW.0 != 0;
            let owned = GetWindow(window, GW_OWNER).is_ok_and(|owner| !owner.is_invalid());
            if IsWindowVisible(window).as_bool() && !tool && !owned && GetWindowTextLengthW(window) > 0 {
                let mut pid = 0;
                GetWindowThreadProcessId(window, Some(&mut pid));
                if pid != 0 && !found.contains(&pid) {
                    found.push(pid);
                }
            }
            true.into()
        }
    }
    let mut pids: Vec<u32> = Vec::new();
    unsafe {
        let _ = EnumWindows(Some(each), LPARAM((&raw mut pids) as isize));
    }
    let system = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".into()).to_lowercase();
    let me = std::env::current_exe().map(|p| p.to_string_lossy().to_lowercase()).unwrap_or_default();
    let mut seen = HashSet::new();
    pids.into_iter()
        .filter_map(process_path)
        .filter(|path| {
            let lower = path.to_lowercase();
            !lower.starts_with(&system) && lower != me && seen.insert(lower)
        })
        .collect()
}

fn process_path(pid: u32) -> Option<String> {
    unsafe {
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buffer = [0u16; 1024];
        let mut length = buffer.len() as u32;
        let result = QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, PWSTR(buffer.as_mut_ptr()), &mut length);
        let _ = CloseHandle(process);
        result.ok()?;
        Some(String::from_utf16_lossy(&buffer[..length as usize]))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_program_reads_its_own_name_and_icon() {
        let explorer = format!(r"{}\explorer.exe", std::env::var("SystemRoot").unwrap());
        assert_eq!(display_name(&explorer), "Windows Explorer");
        let (width, height, rgba) = icon(&explorer).expect("Explorer has an icon");
        assert_eq!((width, height), (ICON_PIXELS, ICON_PIXELS));
        assert!(rgba.as_chunks::<4>().0.iter().any(|p| p[3] == 0), "the corners are see-through");
        assert!(rgba.as_chunks::<4>().0.iter().any(|p| p[3] == 255), "the middle is solid");
    }

    #[test]
    fn joined_words_are_spaced() {
        assert_eq!(spaced("EpicGamesLauncher"), "Epic Games Launcher");
        assert_eq!(spaced("Microsoft Edge"), "Microsoft Edge");
        assert_eq!(spaced("JDownloader"), "JDownloader");
    }

    #[test]
    fn a_missing_program_falls_back_to_its_file_name() {
        assert_eq!(display_name(r"C:\nowhere\fetch-tool.exe"), "Fetch-tool");
        assert!(icon(r"C:\nowhere\fetch-tool.exe").is_none());
    }
}
