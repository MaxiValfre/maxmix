//! Stable session identity.
//!
//! The firmware addresses sessions by a 7-bit id (0..=127). It must stay stable
//! across restarts of both the app and MaxMix, so it is derived deterministically
//! from a session's identity key (its binary path / application id) rather than
//! from any volatile runtime handle. We fold a FNV-1a hash into 7 bits — the same
//! "hash the exe path, truncate to the firmware id width" strategy the .NET app used.

const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

fn fnv1a(bytes: &[u8]) -> u64 {
    let mut hash = FNV_OFFSET;
    for &b in bytes {
        hash ^= u64::from(b);
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    hash
}

/// Derive a stable 7-bit id (0..=127) from a session identity key.
#[must_use]
pub fn stable_id(key: &str) -> u8 {
    (fnv1a(key.as_bytes()) & 0x7F) as u8
}

/// Human-facing name for a stream.
///
/// Electron/Chromium apps (Discord, Slack...) open their voice streams through
/// WebRTC, which reports `application.name = "WEBRTC VoiceEngine"`; ALSA
/// clients may report "ALSA plug-in [...]". For such generic names prefer the
/// process binary — unless the binary is itself a generic runner (Wine, Python,
/// Java...), where `application.name` is the meaningful one (e.g. a game).
#[must_use]
pub fn display_name(app_name: Option<&str>, binary: Option<&str>, fallback: &str) -> String {
    let app = app_name.map(str::trim).filter(|s| !s.is_empty());
    let bin = binary.map(str::trim).filter(|s| !s.is_empty() && !is_generic_binary(s));
    match (app, bin) {
        (Some(a), Some(b)) if is_generic_app_name(a) => pretty_binary(b),
        (Some(a), _) => a.to_string(),
        (None, Some(b)) => pretty_binary(b),
        (None, None) => fallback.to_string(),
    }
}

fn is_generic_app_name(name: &str) -> bool {
    let n = name.to_ascii_lowercase();
    n.contains("webrtc") || n.starts_with("alsa plug-in") || n == "playstream"
}

fn is_generic_binary(bin: &str) -> bool {
    let b = bin.rsplit('/').next().unwrap_or(bin).to_ascii_lowercase();
    ["wine", "python", "java", "node", "electron", "bwrap", "mono", "sh", "bash"].iter().any(|g| {
        b == *g
            || b.starts_with(&format!("{g}64"))
            || b.starts_with(&format!("{g}-"))
            || b.starts_with(&format!("{g}3"))
    })
}

/// "discord" -> "Discord"; keeps already-capitalised names ("Discord").
fn pretty_binary(bin: &str) -> String {
    let base = bin.rsplit('/').next().unwrap_or(bin);
    let mut chars = base.chars();
    match chars.next() {
        Some(c) => c.to_uppercase().collect::<String>() + chars.as_str(),
        None => base.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn webrtc_voice_engine_uses_the_binary() {
        assert_eq!(display_name(Some("WEBRTC VoiceEngine"), Some("Discord"), "?"), "Discord");
        assert_eq!(display_name(Some("WEBRTC VoiceEngine"), Some("slack"), "?"), "Slack");
        assert_eq!(display_name(Some("ALSA plug-in [mpv]"), Some("mpv"), "?"), "Mpv");
    }

    #[test]
    fn meaningful_app_names_are_kept() {
        assert_eq!(display_name(Some("Google Chrome"), Some("chrome"), "?"), "Google Chrome");
        // Wine game: generic runner binary, real name in application.name.
        assert_eq!(
            display_name(Some("Strikers Club"), Some("wine64-preloader"), "?"),
            "Strikers Club"
        );
    }

    #[test]
    fn generic_name_with_generic_binary_keeps_the_name() {
        assert_eq!(
            display_name(Some("WEBRTC VoiceEngine"), Some("python3"), "?"),
            "WEBRTC VoiceEngine"
        );
    }

    #[test]
    fn missing_fields_fall_back() {
        assert_eq!(display_name(None, Some("spotify"), "?"), "Spotify");
        assert_eq!(display_name(None, None, "sink-input-7"), "sink-input-7");
        assert_eq!(display_name(Some("  "), None, "x"), "x");
    }

    #[test]
    fn is_deterministic() {
        assert_eq!(stable_id("/usr/bin/spotify"), stable_id("/usr/bin/spotify"));
    }

    #[test]
    fn stays_in_7_bit_range() {
        for key in ["firefox", "/usr/bin/discord", "", "a very long identity key here"] {
            assert!(stable_id(key) <= 127);
        }
    }

    #[test]
    fn different_keys_usually_differ() {
        // Not a hard guarantee (7 bits collide), but these three must not all collapse.
        let a = stable_id("/usr/bin/firefox");
        let b = stable_id("/usr/bin/spotify");
        let c = stable_id("/usr/bin/discord");
        assert!(a != b || b != c, "hash folded everything to one id");
    }
}
