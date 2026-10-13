//! Session-name sanitisation for the OLED.
//!
//! The firmware renders names with a bitmap font that only knows printable
//! ASCII (0x20..=0x7E) and treats the 30-byte field as a C string. Passing
//! UTF-8 multi-byte sequences corrupts the display (historical mojibake bug),
//! and an un-terminated 30-byte field overruns `strlen`. So the host:
//!
//! * keeps only printable ASCII, transliterating a few common accents,
//! * caps the result at [`NAME_MAX_CHARS`] so a null terminator always fits,
//! * and the frame encoder owns the trailing `0x00`.

/// Max characters emitted; the 30-byte on-wire field reserves byte 30 for null.
pub const NAME_MAX_CHARS: usize = 29;

/// The fixed on-wire name field width (null-padded).
pub const NAME_FIELD_BYTES: usize = 30;

/// Convert an arbitrary display name into at most [`NAME_MAX_CHARS`] printable
/// ASCII characters.
#[must_use]
pub fn to_ascii_name(name: &str) -> String {
    let mut out = String::with_capacity(NAME_MAX_CHARS);
    for ch in name.chars() {
        if out.len() >= NAME_MAX_CHARS {
            break;
        }
        match ch {
            c if c.is_ascii_graphic() || c == ' ' => out.push(c),
            // Common Latin-1 transliterations so accented app names stay legible.
            'á' | 'à' | 'â' | 'ä' | 'ã' | 'Á' | 'À' | 'Â' | 'Ä' | 'Ã' => out.push('a'),
            'é' | 'è' | 'ê' | 'ë' | 'É' | 'È' | 'Ê' | 'Ë' => out.push('e'),
            'í' | 'ì' | 'î' | 'ï' | 'Í' | 'Ì' | 'Î' | 'Ï' => out.push('i'),
            'ó' | 'ò' | 'ô' | 'ö' | 'õ' | 'Ó' | 'Ò' | 'Ô' | 'Ö' | 'Õ' => out.push('o'),
            'ú' | 'ù' | 'û' | 'ü' | 'Ú' | 'Ù' | 'Û' | 'Ü' => out.push('u'),
            'ñ' | 'Ñ' => out.push('n'),
            'ç' | 'Ç' => out.push('c'),
            // Anything else (emoji, CJK, control chars) is dropped.
            _ => {}
        }
    }
    out
}

/// Write `name` into a fixed [`NAME_FIELD_BYTES`]-wide, null-padded buffer,
/// guaranteeing a null terminator within the field.
#[must_use]
pub fn encode_name_field(name: &str) -> [u8; NAME_FIELD_BYTES] {
    let mut field = [0u8; NAME_FIELD_BYTES];
    let ascii = to_ascii_name(name);
    let bytes = ascii.as_bytes();
    let n = bytes.len().min(NAME_MAX_CHARS); // leaves field[29] == 0
    field[..n].copy_from_slice(&bytes[..n]);
    field
}

/// Read a null-terminated name out of a [`NAME_FIELD_BYTES`]-wide field.
#[must_use]
pub fn decode_name_field(field: &[u8]) -> String {
    let end = field.iter().position(|&b| b == 0).unwrap_or(field.len());
    String::from_utf8_lossy(&field[..end]).into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plain_ascii_passthrough() {
        assert_eq!(to_ascii_name("Spotify"), "Spotify");
    }

    #[test]
    fn accents_transliterated() {
        assert_eq!(to_ascii_name("Café Música"), "Cafe Musica");
    }

    #[test]
    fn emoji_and_cjk_dropped() {
        assert_eq!(to_ascii_name("音楽🎵Player"), "Player");
    }

    #[test]
    fn capped_at_29_chars() {
        let long = "Adobe Premiere Pro 2025 - Project1.prproj";
        let out = to_ascii_name(long);
        assert_eq!(out.len(), NAME_MAX_CHARS);
        assert_eq!(out, "Adobe Premiere Pro 2025 - Pro");
    }

    #[test]
    fn field_is_null_terminated_even_when_full() {
        let field = encode_name_field("x".repeat(40).as_str());
        assert_eq!(field.len(), NAME_FIELD_BYTES);
        assert_eq!(field[NAME_FIELD_BYTES - 1], 0, "last byte must be null");
        // 29 'x' then a null terminator.
        assert_eq!(&field[..NAME_MAX_CHARS], &[b'x'; NAME_MAX_CHARS]);
    }

    #[test]
    fn round_trip_through_field() {
        let field = encode_name_field("Discord");
        assert_eq!(decode_name_field(&field), "Discord");
    }
}
