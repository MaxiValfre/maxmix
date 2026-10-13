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

#[cfg(test)]
mod tests {
    use super::*;

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
