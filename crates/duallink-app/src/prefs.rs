//! The window's own preferences, kept per Windows user, and starting with Windows.
//! What DualLink combines is the service's business and is saved by the service.

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Appearance {
    #[default]
    Auto,
    Light,
    Dark,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct Prefs {
    pub appearance: Appearance,
    /// Closing the window keeps DualLink in the notification area.
    pub keep_in_tray: bool,
}

impl Default for Prefs {
    fn default() -> Self {
        Prefs { appearance: Appearance::Auto, keep_in_tray: true }
    }
}

fn path() -> Option<PathBuf> {
    Some(PathBuf::from(std::env::var_os("LOCALAPPDATA")?).join("DualLink").join("app.json"))
}

impl Prefs {
    pub fn load() -> Prefs {
        path().and_then(|p| std::fs::read(p).ok()).and_then(|bytes| serde_json::from_slice(&bytes).ok()).unwrap_or_default()
    }

    pub fn save(&self) {
        let Some(path) = path() else { return };
        if let Some(folder) = path.parent() {
            let _ = std::fs::create_dir_all(folder);
        }
        let temporary = path.with_extension("json.tmp");
        if std::fs::write(&temporary, serde_json::to_vec_pretty(self).expect("prefs always serialise")).is_ok() {
            let _ = std::fs::rename(&temporary, &path);
        }
    }
}

/// Starting DualLink's window, quietly in the notification area, when the user signs in.
/// The service starts with Windows by itself; this is only the window.
pub mod autostart {
    use windows::Win32::Foundation::ERROR_SUCCESS;
    use windows::Win32::System::Registry::{
        HKEY_CURRENT_USER, REG_SZ, RRF_RT_REG_SZ, RegDeleteKeyValueW, RegGetValueW, RegSetKeyValueW,
    };
    use windows::core::{PCWSTR, w};

    const KEY: PCWSTR = w!(r"Software\Microsoft\Windows\CurrentVersion\Run");
    const NAME: PCWSTR = w!("DualLink");

    fn command() -> Option<String> {
        let exe = std::env::current_exe().ok()?;
        Some(format!("\"{}\" --background", exe.display()))
    }

    /// Whether Windows starts this copy of DualLink at sign-in.
    pub fn enabled() -> bool {
        let mut buffer = [0u16; 1024];
        let mut size = (buffer.len() * 2) as u32;
        let found = unsafe {
            RegGetValueW(HKEY_CURRENT_USER, KEY, NAME, RRF_RT_REG_SZ, None, Some(buffer.as_mut_ptr().cast()), Some(&mut size))
        };
        if found != ERROR_SUCCESS {
            return false;
        }
        let text = String::from_utf16_lossy(&buffer[..(size as usize / 2).saturating_sub(1)]);
        command().is_some_and(|expected| text.eq_ignore_ascii_case(&expected))
    }

    pub fn set(on: bool) -> bool {
        unsafe {
            if !on {
                let result = RegDeleteKeyValueW(HKEY_CURRENT_USER, KEY, NAME);
                return result == ERROR_SUCCESS || !enabled();
            }
            let Some(command) = command() else { return false };
            let wide: Vec<u16> = command.encode_utf16().chain(std::iter::once(0)).collect();
            RegSetKeyValueW(HKEY_CURRENT_USER, KEY, NAME, REG_SZ.0, Some(wide.as_ptr().cast()), (wide.len() * 2) as u32)
                == ERROR_SUCCESS
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preferences_from_an_older_build_fill_in_new_fields() {
        let prefs: Prefs = serde_json::from_str(r#"{"appearance":"dark"}"#).unwrap();
        assert_eq!(prefs, Prefs { appearance: Appearance::Dark, keep_in_tray: true });
    }
}
