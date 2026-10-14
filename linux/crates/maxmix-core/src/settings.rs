//! User settings: JSON at `~/.config/maxmix/settings.json` (XDG), schema-versioned
//! with migration and corrupt-file recovery.

use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use maxmix_protocol::{LightingConfig, LightingMode, Rgb};

/// Current settings schema version.
pub const SCHEMA_VERSION: u32 = 1;

/// A serialisable RGB colour.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Color {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Color {
    #[must_use]
    pub const fn new(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }
}

impl Color {
    /// Format as `#RRGGBB`.
    #[must_use]
    pub fn to_hex(self) -> String {
        format!("#{:02X}{:02X}{:02X}", self.r, self.g, self.b)
    }

    /// Parse `#RRGGBB` or `RRGGBB` (case-insensitive). Returns `None` if invalid.
    #[must_use]
    pub fn from_hex(text: &str) -> Option<Self> {
        let t = text.trim().trim_start_matches('#');
        if t.len() != 6 || !t.chars().all(|c| c.is_ascii_hexdigit()) {
            return None;
        }
        let byte = |i: usize| u8::from_str_radix(&t[i..i + 2], 16).ok();
        Some(Self::new(byte(0)?, byte(2)?, byte(4)?))
    }
}

impl Lighting {
    /// All modes in UI order.
    pub const ALL: [Lighting; 5] =
        [Lighting::Static, Lighting::Pulse, Lighting::Fade, Lighting::Rainbow, Lighting::Carousel];

    /// Index into [`Lighting::ALL`].
    #[must_use]
    pub fn index(self) -> usize {
        match self {
            Lighting::Static => 0,
            Lighting::Pulse => 1,
            Lighting::Fade => 2,
            Lighting::Rainbow => 3,
            Lighting::Carousel => 4,
        }
    }

    /// Mode for a UI index; out-of-range falls back to `Static`.
    #[must_use]
    pub fn from_index(i: usize) -> Self {
        Self::ALL.get(i).copied().unwrap_or(Lighting::Static)
    }
}

impl From<Color> for Rgb {
    fn from(c: Color) -> Self {
        Rgb::new(c.r, c.g, c.b)
    }
}

/// Firmware lighting animation mode, mirrored for settings persistence.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Lighting {
    Static,
    Pulse,
    Fade,
    Rainbow,
    Carousel,
}

impl From<Lighting> for LightingMode {
    fn from(l: Lighting) -> Self {
        match l {
            Lighting::Static => LightingMode::Static,
            Lighting::Pulse => LightingMode::Pulse,
            Lighting::Fade => LightingMode::Fade,
            Lighting::Rainbow => LightingMode::Rainbow,
            Lighting::Carousel => LightingMode::Carousel,
        }
    }
}

/// All persisted user preferences.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AppSettings {
    pub schema_version: u32,
    pub color_min: Color,
    pub color_max: Color,
    /// Volume gauge gradient (shown while the volume changes). Older settings
    /// files lack these fields and get the defaults.
    #[serde(default = "default_volume_min")]
    pub volume_color_min: Color,
    #[serde(default = "default_volume_max")]
    pub volume_color_max: Color,
    pub lighting_mode: Lighting,
    /// Animation speed 1..=10.
    pub lighting_speed: u8,
    pub idle_lighting_mode: Lighting,
    /// Seconds of inactivity before idle lighting (0 = disabled).
    pub idle_timeout_s: u8,
    /// Encoder acceleration 0..=100.
    pub acceleration: u8,
    /// Seconds before the device sleeps (0 = never).
    pub sleep_after_s: u8,
    /// Seconds without input before the OLED dims to protect it from burn-in
    /// (0 = never). Older settings files get the default.
    #[serde(default = "default_dim_after")]
    pub display_dim_after_s: u8,
    pub continuous_scroll: bool,
    pub ring_reversed: bool,
    pub autostart: bool,
    pub start_minimized: bool,
    /// Last serial port path used (manual override hint).
    pub last_port: Option<String>,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            schema_version: SCHEMA_VERSION,
            color_min: Color::new(0, 0, 32),
            color_max: Color::new(0, 128, 255),
            volume_color_min: default_volume_min(),
            volume_color_max: default_volume_max(),
            lighting_mode: Lighting::Static,
            lighting_speed: 5,
            idle_lighting_mode: Lighting::Pulse,
            idle_timeout_s: 30,
            acceleration: 50,
            sleep_after_s: 0,
            display_dim_after_s: default_dim_after(),
            continuous_scroll: false,
            ring_reversed: false,
            autostart: false,
            start_minimized: false,
            last_port: None,
        }
    }
}

fn default_dim_after() -> u8 {
    30
}

/// Volume gauge defaults: a classic green -> red level meter.
fn default_volume_min() -> Color {
    Color::new(0, 255, 64)
}

fn default_volume_max() -> Color {
    Color::new(255, 0, 0)
}

impl AppSettings {
    /// Build the firmware lighting config this settings snapshot implies.
    #[must_use]
    pub fn lighting_config(&self) -> LightingConfig {
        LightingConfig {
            mode: self.lighting_mode.into(),
            speed: self.lighting_speed.clamp(1, 10),
            color_min: self.color_min.into(),
            color_max: self.color_max.into(),
            idle_mode: self.idle_lighting_mode.into(),
            idle_timeout_s: self.idle_timeout_s,
            volume_min: self.volume_color_min.into(),
            volume_max: self.volume_color_max.into(),
        }
    }
}

/// The default settings file path, honouring `XDG_CONFIG_HOME`.
#[must_use]
pub fn default_settings_path() -> PathBuf {
    dirs::config_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("maxmix")
        .join("settings.json")
}

/// Load settings from `path`, applying migration. On a missing file, write and
/// return defaults. On a corrupt file, back it up as `*.bak` and return defaults.
pub fn load_or_init(path: &Path) -> AppSettings {
    match fs::read_to_string(path) {
        Ok(text) => match serde_json::from_str::<AppSettings>(&text) {
            Ok(mut s) => {
                if migrate(&mut s) {
                    let _ = save(path, &s);
                }
                s
            }
            Err(_) => {
                // Corrupt: preserve the bad file, then fall back to defaults.
                let _ = fs::rename(path, path.with_extension("json.bak"));
                let s = AppSettings::default();
                let _ = save(path, &s);
                s
            }
        },
        Err(_) => {
            let s = AppSettings::default();
            let _ = save(path, &s);
            s
        }
    }
}

/// Persist settings to `path`, creating parent directories.
pub fn save(path: &Path, settings: &AppSettings) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let text = serde_json::to_string_pretty(settings)
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
    fs::write(path, text)
}

/// Apply sequential migrations in place. Returns true if anything changed.
fn migrate(settings: &mut AppSettings) -> bool {
    let mut changed = false;
    // Future: match on settings.schema_version and upgrade step by step.
    if settings.schema_version < SCHEMA_VERSION {
        settings.schema_version = SCHEMA_VERSION;
        changed = true;
    }
    changed
}

#[cfg(test)]
#[allow(clippy::field_reassign_with_default)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    #[test]
    fn missing_file_creates_defaults() {
        let dir = tempdir().unwrap();
        let path = dir.path().join("settings.json");
        let s = load_or_init(&path);
        assert_eq!(s, AppSettings::default());
        assert!(path.exists(), "defaults are written on first load");
    }

    #[test]
    fn round_trips_through_disk() {
        let dir = tempdir().unwrap();
        let path = dir.path().join("settings.json");
        let mut s = AppSettings::default();
        s.acceleration = 73;
        s.color_max = Color::new(10, 20, 30);
        save(&path, &s).unwrap();
        let loaded = load_or_init(&path);
        assert_eq!(loaded.acceleration, 73);
        assert_eq!(loaded.color_max, Color::new(10, 20, 30));
    }

    #[test]
    fn corrupt_file_is_backed_up_and_reset() {
        let dir = tempdir().unwrap();
        let path = dir.path().join("settings.json");
        fs::write(&path, "}{ not json").unwrap();
        let s = load_or_init(&path);
        assert_eq!(s, AppSettings::default());
        assert!(path.with_extension("json.bak").exists(), "corrupt file preserved");
    }

    #[test]
    fn old_schema_is_migrated() {
        let dir = tempdir().unwrap();
        let path = dir.path().join("settings.json");
        let mut old = AppSettings::default();
        old.schema_version = 0;
        save(&path, &old).unwrap();
        let loaded = load_or_init(&path);
        assert_eq!(loaded.schema_version, SCHEMA_VERSION);
    }

    #[test]
    fn settings_without_dim_timeout_load_with_default() {
        let mut v = serde_json::to_value(AppSettings::default()).unwrap();
        v.as_object_mut().unwrap().remove("display_dim_after_s");
        let s: AppSettings = serde_json::from_value(v).unwrap();
        assert_eq!(s.display_dim_after_s, 30);
    }

    #[test]
    fn settings_without_volume_colours_load_with_defaults() {
        let mut v = serde_json::to_value(AppSettings::default()).unwrap();
        let obj = v.as_object_mut().unwrap();
        obj.remove("volume_color_min");
        obj.remove("volume_color_max");
        let s: AppSettings = serde_json::from_value(v).unwrap();
        assert_eq!(s.volume_color_min, Color::new(0, 255, 64));
        assert_eq!(s.volume_color_max, Color::new(255, 0, 0));
    }

    #[test]
    fn lighting_config_carries_volume_colours() {
        let mut s = AppSettings::default();
        s.volume_color_min = Color::new(1, 2, 3);
        s.volume_color_max = Color::new(4, 5, 6);
        let cfg = s.lighting_config();
        assert_eq!(cfg.volume_min, Rgb::new(1, 2, 3));
        assert_eq!(cfg.volume_max, Rgb::new(4, 5, 6));
    }

    #[test]
    fn every_ui_lighting_mode_maps_to_the_wire() {
        for (i, l) in Lighting::ALL.iter().enumerate() {
            assert_eq!(l.index(), i);
            assert_eq!(Lighting::from_index(i), *l);
            assert_eq!(LightingMode::from(*l) as usize, i, "UI order == wire value");
        }
    }

    #[test]
    fn lighting_config_clamps_speed() {
        let mut s = AppSettings::default();
        s.lighting_speed = 99;
        assert_eq!(s.lighting_config().speed, 10);
    }

    #[test]
    fn hex_round_trip() {
        let c = Color::new(255, 128, 0);
        assert_eq!(c.to_hex(), "#FF8000");
        assert_eq!(Color::from_hex("#FF8000"), Some(c));
        assert_eq!(Color::from_hex("ff8000"), Some(c), "no hash, lowercase");
    }

    #[test]
    fn hex_rejects_invalid() {
        for bad in ["", "#FFF", "#GG0000", "#FF80001", "red"] {
            assert_eq!(Color::from_hex(bad), None, "{bad} must be rejected");
        }
    }

    #[test]
    fn lighting_index_round_trip_and_fallback() {
        for m in Lighting::ALL {
            assert_eq!(Lighting::from_index(m.index()), m);
        }
        assert_eq!(Lighting::from_index(99), Lighting::Static);
    }
}
