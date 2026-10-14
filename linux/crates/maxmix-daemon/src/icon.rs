//! The MaxMix "M" mark rendered procedurally for the tray.
//!
//! Same polygon as `packaging/icons/maxmix.svg` (traced from the original
//! MaxMix app icon): a square with a V notch cut from the top edge, in a
//! 256x256 design space. Rendering in code keeps the tray icon working even
//! when the hicolor icon is not installed.

/// Brand coral used while the device is connected.
pub const CONNECTED_RGB: (u8, u8, u8) = (0xF1, 0x61, 0x63);
/// Grey used while no device is connected (like the original tray icon).
pub const DISCONNECTED_RGB: (u8, u8, u8) = (0x77, 0x77, 0x77);

const MARGIN: f32 = 16.0;
const SPAN: f32 = 256.0 - 2.0 * MARGIN; // 224
const NOTCH_DEPTH: f32 = 80.0; // the V bottoms out at y = 96
const SUPERSAMPLE: u32 = 4;

/// Whether a point in the 256x256 design space lies inside the "M".
fn inside(u: f32, v: f32) -> bool {
    if !(MARGIN..=256.0 - MARGIN).contains(&u) || !(MARGIN..=256.0 - MARGIN).contains(&v) {
        return false;
    }
    let half = SPAN / 2.0;
    let notch_y = MARGIN + NOTCH_DEPTH * (1.0 - (u - 128.0).abs() / half);
    v >= notch_y
}

/// Render the mark as ARGB32 (big-endian byte order: A, R, G, B per pixel),
/// the format StatusNotifierItem `IconPixmap` expects.
#[must_use]
pub fn m_mark_argb(size: u32, rgb: (u8, u8, u8)) -> Vec<u8> {
    let mut out = Vec::with_capacity((size * size * 4) as usize);
    let scale = 256.0 / size as f32;
    let samples = SUPERSAMPLE * SUPERSAMPLE;
    for y in 0..size {
        for x in 0..size {
            let mut hits = 0u32;
            for sy in 0..SUPERSAMPLE {
                for sx in 0..SUPERSAMPLE {
                    let u = (x as f32 + (sx as f32 + 0.5) / SUPERSAMPLE as f32) * scale;
                    let v = (y as f32 + (sy as f32 + 0.5) / SUPERSAMPLE as f32) * scale;
                    if inside(u, v) {
                        hits += 1;
                    }
                }
            }
            let alpha = (hits * 255 / samples) as u8;
            out.extend_from_slice(&[alpha, rgb.0, rgb.1, rgb.2]);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn alpha_at(buf: &[u8], size: u32, x: u32, y: u32) -> u8 {
        buf[((y * size + x) * 4) as usize]
    }

    #[test]
    fn buffer_has_argb_size() {
        assert_eq!(m_mark_argb(32, CONNECTED_RGB).len(), 32 * 32 * 4);
    }

    #[test]
    fn shape_matches_the_m_mark() {
        let s = 64;
        let buf = m_mark_argb(s, CONNECTED_RGB);
        // Corners outside the margin are transparent.
        assert_eq!(alpha_at(&buf, s, 0, 0), 0);
        assert_eq!(alpha_at(&buf, s, s - 1, s - 1), 0);
        // The notch: top-centre is empty, bottom-centre is solid.
        assert_eq!(alpha_at(&buf, s, s / 2, 10), 0);
        assert_eq!(alpha_at(&buf, s, s / 2, s - 8), 255);
        // The two "legs" reach the top edge.
        assert_eq!(alpha_at(&buf, s, 5, 6), 255);
        assert_eq!(alpha_at(&buf, s, s - 6, 6), 255);
    }

    #[test]
    fn colour_is_applied_to_every_pixel() {
        let buf = m_mark_argb(16, DISCONNECTED_RGB);
        assert!(buf.chunks(4).all(|p| p[1..] == [0x77, 0x77, 0x77]));
    }
}
