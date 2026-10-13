//! XDG autostart management: `~/.config/autostart/maxmix.desktop`.
//! Replaces the Windows registry Run key.

use std::fs;
use std::io;
use std::path::PathBuf;

const DESKTOP_FILE: &str = "maxmix.desktop";

/// Build the `.desktop` autostart entry pointing at `exec_path`.
#[must_use]
pub fn desktop_entry(exec_path: &str) -> String {
    format!(
        "[Desktop Entry]\n\
         Type=Application\n\
         Name=MaxMix\n\
         Comment=MaxMix hardware audio mixer companion\n\
         Exec={exec_path}\n\
         Icon=audio-volume-high\n\
         Terminal=false\n\
         Categories=Utility;AudioVideo;\n\
         X-GNOME-Autostart-enabled=true\n"
    )
}

/// Path to the autostart desktop entry (honours `XDG_CONFIG_HOME`).
#[must_use]
pub fn autostart_path() -> PathBuf {
    dirs::config_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("autostart")
        .join(DESKTOP_FILE)
}

/// Enable or disable autostart by writing or removing the desktop entry.
pub fn set_autostart(enabled: bool, exec_path: &str) -> io::Result<()> {
    let path = autostart_path();
    if enabled {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::write(&path, desktop_entry(exec_path))
    } else {
        match fs::remove_file(&path) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(e),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn entry_contains_exec_and_required_keys() {
        let e = desktop_entry("/usr/bin/maxmixd");
        assert!(e.starts_with("[Desktop Entry]"));
        assert!(e.contains("Exec=/usr/bin/maxmixd"));
        assert!(e.contains("Type=Application"));
        assert!(e.contains("X-GNOME-Autostart-enabled=true"));
    }
}
