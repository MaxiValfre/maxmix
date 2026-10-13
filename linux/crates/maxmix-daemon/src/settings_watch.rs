//! Settings hot-reload and settings-window launching.
//!
//! The settings UI is a separate on-demand process that only writes the JSON
//! file. The daemon polls the file's modification time (cheap, no inotify
//! dependency) and re-applies settings when it changes.

use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::SystemTime;

/// Detects modifications of a file by comparing its mtime between polls.
#[derive(Debug)]
pub struct FileWatch {
    path: PathBuf,
    last: Option<SystemTime>,
}

impl FileWatch {
    /// Start watching `path`, taking its current mtime as the baseline.
    #[must_use]
    pub fn new(path: &Path) -> Self {
        Self { path: path.to_path_buf(), last: mtime(path) }
    }

    /// True if the file's mtime changed since the last call (or baseline).
    pub fn changed(&mut self) -> bool {
        let now = mtime(&self.path);
        if now.is_some() && now != self.last {
            self.last = now;
            true
        } else {
            false
        }
    }
}

fn mtime(path: &Path) -> Option<SystemTime> {
    std::fs::metadata(path).and_then(|m| m.modified()).ok()
}

/// Resolve the settings binary: next to the daemon executable, else on PATH.
#[must_use]
pub fn settings_binary() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|d| d.join("maxmix-settings")))
        .filter(|p| p.exists())
        .unwrap_or_else(|| PathBuf::from("maxmix-settings"))
}

/// Launch the settings window detached. Errors are logged by the caller.
pub fn launch_settings() -> std::io::Result<()> {
    Command::new(settings_binary()).spawn().map(|_| ())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[test]
    fn detects_modification_once() {
        let dir = std::env::temp_dir().join(format!("maxmix-watch-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("settings.json");
        std::fs::write(&path, "{}").unwrap();

        let mut w = FileWatch::new(&path);
        assert!(!w.changed(), "baseline is not a change");

        // Ensure a distinct mtime even on coarse-grained filesystems.
        let later = SystemTime::now() + Duration::from_secs(5);
        let f = std::fs::File::options().write(true).open(&path).unwrap();
        f.set_modified(later).unwrap();

        assert!(w.changed(), "modification detected");
        assert!(!w.changed(), "reported only once");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn missing_file_is_not_a_change() {
        let mut w = FileWatch::new(Path::new("/nonexistent/maxmix/settings.json"));
        assert!(!w.changed());
    }
}
