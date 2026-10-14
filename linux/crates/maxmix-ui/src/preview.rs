//! Ring preview for the settings window.
//!
//! A faithful Rust port of the firmware renderer (`firmware/MaxMix/mm_light.c`)
//! using the same integer maths, so the on-screen ring animates exactly like
//! the device. Keep the two in sync when the firmware lighting changes.

use maxmix_core::{Color, Lighting};

/// Pixels on the MaxMix ring (firmware `PIXELS_COUNT`).
pub const PIXELS: u8 = 8;

/// Everything the renderer needs, taken from the form.
#[derive(Debug, Clone)]
pub struct PreviewConfig {
    pub mode: Lighting,
    pub speed: u8,
    /// Palette, 1..=8 colours.
    pub colors: Vec<Color>,
    pub volume_min: Color,
    pub volume_max: Color,
}

fn lerp8(a: u8, b: u8, t: u8) -> u8 {
    (i32::from(a) + (i32::from(b) - i32::from(a)) * i32::from(t) / 255) as u8
}

#[must_use]
pub fn lerp(a: Color, b: Color, t: u8) -> Color {
    Color::new(lerp8(a.r, b.r, t), lerp8(a.g, b.g, t), lerp8(a.b, b.b, t))
}

#[must_use]
pub fn scale(c: Color, k: u8) -> Color {
    let s = |v: u8| (u16::from(v) * u16::from(k) / 255) as u8;
    Color::new(s(c.r), s(c.g), s(c.b))
}

fn period(speed: u8) -> u32 {
    4400 - 400 * u32::from(speed.clamp(1, 10))
}

/// Triangle wave 0..255..0 (firmware `mm_wave`).
#[must_use]
pub fn wave(now_ms: u32, speed: u8) -> u8 {
    let period = period(speed);
    let phase = now_ms % period;
    let half = period / 2;
    let v = if phase < half { phase * 255 / half } else { (period - phase) * 255 / half };
    v.min(255) as u8
}

/// Fully saturated hue wheel (firmware `mm_hue`).
#[must_use]
pub fn hue(h: u8) -> Color {
    let region = h / 43;
    let rem = (h - region * 43) * 6;
    let q = 255 - rem;
    match region {
        0 => Color::new(255, rem, 0),
        1 => Color::new(q, 255, 0),
        2 => Color::new(0, 255, rem),
        3 => Color::new(0, q, 255),
        4 => Color::new(rem, 0, 255),
        _ => Color::new(255, 0, q),
    }
}

fn palette_n(cfg: &PreviewConfig) -> usize {
    cfg.colors.len().clamp(1, 8)
}

fn color_at(cfg: &PreviewConfig, i: usize) -> Color {
    cfg.colors.get(i).copied().unwrap_or(Color::new(0, 0, 0))
}

/// Palette as a gradient (firmware `mm_palette_linear`).
#[must_use]
pub fn palette_linear(cfg: &PreviewConfig, t: u8) -> Color {
    let n = palette_n(cfg);
    if n == 1 {
        return color_at(cfg, 0);
    }
    let pos = u16::from(t) * (n as u16 - 1);
    let i = usize::from(pos / 255);
    if i >= n - 1 {
        return color_at(cfg, n - 1);
    }
    lerp(color_at(cfg, i), color_at(cfg, i + 1), (pos % 255) as u8)
}

/// Palette as a loop (firmware `mm_palette_cyclic`).
#[must_use]
pub fn palette_cyclic(cfg: &PreviewConfig, p: u8) -> Color {
    let n = palette_n(cfg);
    if n == 1 {
        return color_at(cfg, 0);
    }
    let pos = u16::from(p) * n as u16;
    let i = usize::from(pos >> 8);
    lerp(color_at(cfg, i), color_at(cfg, (i + 1) % n), (pos & 0xFF) as u8)
}

fn rotation(now_ms: u32, speed: u8) -> u8 {
    let period = period(speed);
    ((now_ms % period) * 256 / period) as u8
}

/// Colour of pixel `i` for the lighting mode at `volume` (0..=100).
#[must_use]
pub fn render_pixel(cfg: &PreviewConfig, volume: u8, i: u8, now_ms: u32) -> Color {
    let off = Color::new(0, 0, 0);
    match cfg.mode {
        Lighting::Rainbow | Lighting::Carousel => {
            let p = ((u16::from(i) * 256 / u16::from(PIXELS)) as u8)
                .wrapping_add(rotation(now_ms, cfg.speed));
            if cfg.mode == Lighting::Rainbow {
                return hue(p);
            }
            palette_cyclic(cfg, p)
        }
        Lighting::Static | Lighting::Pulse | Lighting::Fade => {
            let lit = ((u16::from(volume.min(100)) * u16::from(PIXELS) + 50) / 100) as u8;
            if i >= lit {
                return off;
            }
            let t = (u16::from(i) * 255 / u16::from(PIXELS - 1)) as u8;
            let base = palette_linear(cfg, t);
            match cfg.mode {
                Lighting::Pulse => {
                    scale(base, (51 + u16::from(wave(now_ms, cfg.speed)) * 204 / 255) as u8)
                }
                Lighting::Fade => palette_cyclic(cfg, rotation(now_ms, cfg.speed)),
                _ => base,
            }
        }
    }
}

/// Gauge length in q8 pixel units (firmware `mm_level_q8`).
#[must_use]
pub fn level_q8(volume: u8) -> u16 {
    (u32::from(volume.min(100)) * u32::from(PIXELS) * 256 / 100) as u16
}

/// Volume gauge fill (firmware `mm_render_gauge`): Low below, High above.
#[must_use]
pub fn render_gauge(cfg: &PreviewConfig, level_q8: u16, i: u8) -> Color {
    let start = u16::from(i) * 256;
    let frac = level_q8.saturating_sub(start);
    let t = frac.min(255) as u8;
    lerp(cfg.volume_min, cfg.volume_max, t)
}

/// One easing step (firmware `mm_ease_q8`).
#[must_use]
pub fn ease_q8(cur: u16, target: u16) -> u16 {
    let diff = i32::from(target) - i32::from(cur);
    let mut step = diff / 4;
    if step.abs() < 16 {
        step = diff.clamp(-16, 16);
    }
    (i32::from(cur) + step) as u16
}

#[cfg(test)]
mod tests {
    use super::*;

    fn cfg(mode: Lighting) -> PreviewConfig {
        PreviewConfig {
            mode,
            speed: 5,
            colors: vec![Color::new(255, 0, 0), Color::new(0, 0, 255)],
            volume_min: Color::new(0, 255, 64),
            volume_max: Color::new(255, 0, 0),
        }
    }

    #[test]
    fn matches_firmware_primitives() {
        // Same checks as firmware/test/test_core.c.
        assert_eq!(wave(0, 5), 0);
        assert_eq!(wave(1200, 5), 255);
        assert_eq!(hue(0), Color::new(255, 0, 0));
        let g = hue(85);
        assert!(g.g == 255 && g.r < 10 && g.b < 10);
        assert_eq!(lerp(Color::new(255, 0, 160), Color::new(0, 0, 255), 255), Color::new(0, 0, 255));
    }

    #[test]
    fn carousel_starts_at_min_and_peaks_at_max() {
        let c = cfg(Lighting::Carousel);
        assert_eq!(render_pixel(&c, 0, 0, 0), Color::new(255, 0, 0));
        let half = render_pixel(&c, 0, 4, 0);
        assert!(half.r < 10 && half.b > 245);
    }

    #[test]
    fn three_colour_palette_matches_firmware() {
        // Same expectations as test_palette in firmware/test/test_core.c.
        let mut c = cfg(Lighting::Static);
        c.colors = vec![Color::new(255, 0, 0), Color::new(0, 255, 0), Color::new(0, 0, 255)];
        assert_eq!(palette_linear(&c, 0), Color::new(255, 0, 0));
        assert_eq!(palette_linear(&c, 255), Color::new(0, 0, 255));
        let mid = palette_linear(&c, 128);
        assert!(mid.g > 250 && mid.r < 5 && mid.b < 5);
        let g = palette_cyclic(&c, 86);
        assert!(g.g > 245 && g.r < 10);
        let back = palette_cyclic(&c, 250);
        assert!(back.r > 200 && back.b < 60);
        assert_eq!(render_pixel(&c, 100, 0, 0), Color::new(255, 0, 0));
        assert_eq!(render_pixel(&c, 100, 7, 0), Color::new(0, 0, 255));
        c.colors.truncate(1);
        assert_eq!(palette_cyclic(&c, 200), Color::new(255, 0, 0));
    }

    #[test]
    fn static_mode_is_a_volume_gauge() {
        let c = cfg(Lighting::Static);
        assert_ne!(render_pixel(&c, 50, 3, 0), Color::new(0, 0, 0));
        assert_eq!(render_pixel(&c, 50, 4, 0), Color::new(0, 0, 0));
    }

    #[test]
    fn gauge_fills_from_low_to_high() {
        let c = cfg(Lighting::Static);
        assert_eq!(render_gauge(&c, level_q8(0), 7), c.volume_min);
        assert_eq!(render_gauge(&c, level_q8(100), 0), c.volume_max);
        assert_eq!(render_gauge(&c, level_q8(50), 3), c.volume_max);
        assert_eq!(render_gauge(&c, level_q8(50), 4), c.volume_min);
    }

    #[test]
    fn easing_lands_on_target() {
        let mut v = 0;
        for _ in 0..40 {
            v = ease_q8(v, 2048);
        }
        assert_eq!(v, 2048);
    }
}
