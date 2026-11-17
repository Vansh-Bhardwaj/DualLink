//! Where the service keeps its configuration and its log.

use duallink_core::Config;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

const LOG_LIMIT: u64 = 1024 * 1024;

pub fn folder() -> PathBuf {
    let base = std::env::var_os("ProgramData").map_or_else(|| PathBuf::from(r"C:\ProgramData"), PathBuf::from);
    base.join("DualLink")
}

fn config_path() -> PathBuf {
    folder().join("config.json")
}

/// The saved configuration, or the defaults when there is none or it cannot be used.
pub fn load() -> Config {
    let Ok(text) = fs::read_to_string(config_path()) else { return Config::default() };
    match serde_json::from_str::<Config>(&text) {
        Ok(config) if config.validate().is_ok() => config,
        _ => {
            log("The saved configuration could not be used; starting with combining off.");
            Config::default()
        }
    }
}

/// Writes to a temporary file first, so a crash mid-write never leaves half a file.
pub fn save(config: &Config) -> std::io::Result<()> {
    fs::create_dir_all(folder())?;
    let path = config_path();
    let temporary = path.with_extension("json.tmp");
    fs::write(&temporary, serde_json::to_vec_pretty(config).expect("config always serialises"))?;
    fs::rename(&temporary, &path)
}

static LOG: Mutex<()> = Mutex::new(());

/// Appends one line to the service log, starting a fresh file when it grows past a megabyte.
pub fn log(message: &str) {
    let _guard = LOG.lock().unwrap();
    let seconds = SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs());
    let (h, m, s) = ((seconds / 3600) % 24, (seconds / 60) % 60, seconds % 60);
    let days = seconds / 86_400;
    let line = format!("{} {h:02}:{m:02}:{s:02}Z {message}\n", date(days));
    if std::env::args().any(|a| a == "--console") {
        print!("{line}");
    }
    let _ = fs::create_dir_all(folder());
    let path = folder().join("service.log");
    if fs::metadata(&path).is_ok_and(|m| m.len() > LOG_LIMIT) {
        let _ = fs::rename(&path, folder().join("service.old.log"));
    }
    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(&path) {
        let _ = file.write_all(line.as_bytes());
    }
}

/// Days since 1970 as a calendar date (Howard Hinnant's civil-from-days).
fn date(days: u64) -> String {
    let z = days as i64 + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!("{year:04}-{month:02}-{day:02}")
}

#[cfg(test)]
mod tests {
    #[test]
    fn dates_are_right() {
        assert_eq!(super::date(0), "1970-01-01");
        assert_eq!(super::date(20_774), "2026-11-17");
        assert_eq!(super::date(19_782), "2024-02-29");
    }
}
