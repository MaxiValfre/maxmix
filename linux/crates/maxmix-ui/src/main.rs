//! `maxmix-settings`: on-demand settings window.
//!
//! It only edits `~/.config/maxmix/settings.json`; the running daemon watches
//! that file and pushes changes to the device. Launched from the tray, it exits
//! when closed, so it costs nothing while not in use.

use maxmix_core::{default_settings_path, load_or_init, save, AppSettings, Color, Lighting};

slint::slint! {
    import { Button, CheckBox, ComboBox, Slider, SpinBox, GroupBox, VerticalBox, HorizontalBox } from "std-widgets.slint";

    // Colour picker: live swatch + R/G/B sliders + quick palette.
    component ColorPicker inherits VerticalBox {
        in property <string> label;
        in-out property <float> r;
        in-out property <float> g;
        in-out property <float> b;
        property <[{r: int, g: int, b: int}]> palette: [
            {r: 255, g: 0, b: 0}, {r: 255, g: 96, b: 0}, {r: 255, g: 200, b: 0},
            {r: 0, g: 255, b: 0}, {r: 0, g: 255, b: 200}, {r: 0, g: 128, b: 255},
            {r: 0, g: 0, b: 255}, {r: 160, g: 0, b: 255}, {r: 255, g: 0, b: 160},
            {r: 255, g: 255, b: 255}, {r: 0, g: 0, b: 32}, {r: 0, g: 0, b: 0},
        ];
        padding: 0px;
        HorizontalBox {
            padding: 0px;
            Text { text: root.label; vertical-alignment: center; width: 90px; }
            Rectangle {
                width: 48px; height: 24px; border-radius: 4px;
                border-width: 1px; border-color: #888;
                background: rgb(root.r, root.g, root.b);
            }
            Text {
                vertical-alignment: center;
                text: "R" + Math.round(root.r) + "  G" + Math.round(root.g) + "  B" + Math.round(root.b);
            }
        }
        HorizontalBox { padding: 0px; Text { text: "R"; width: 12px; vertical-alignment: center; }
            Slider { minimum: 0; maximum: 255; value <=> root.r; } }
        HorizontalBox { padding: 0px; Text { text: "G"; width: 12px; vertical-alignment: center; }
            Slider { minimum: 0; maximum: 255; value <=> root.g; } }
        HorizontalBox { padding: 0px; Text { text: "B"; width: 12px; vertical-alignment: center; }
            Slider { minimum: 0; maximum: 255; value <=> root.b; } }
        HorizontalBox {
            padding: 0px; spacing: 4px;
            for c in root.palette: Rectangle {
                width: 22px; height: 22px; border-radius: 11px;
                border-width: 1px; border-color: #888;
                background: rgb(c.r, c.g, c.b);
                TouchArea { clicked => { root.r = c.r; root.g = c.g; root.b = c.b; } }
            }
        }
    }

    export component SettingsWindow inherits Window {
        title: "MaxMix Settings";
        preferred-width: 440px;

        in-out property <float> min-r;
        in-out property <float> min-g;
        in-out property <float> min-b;
        in-out property <float> max-r;
        in-out property <float> max-g;
        in-out property <float> max-b;
        in-out property <int> lighting-mode;
        in-out property <int> lighting-speed;
        in-out property <int> idle-mode;
        in-out property <int> idle-timeout;
        in-out property <int> acceleration;
        in-out property <int> sleep-after;
        in-out property <bool> continuous-scroll;
        in-out property <bool> ring-reversed;
        in-out property <bool> autostart;
        in-out property <string> status;

        callback save();
        callback reset-defaults();

        VerticalBox {
            GroupBox {
                title: "Lighting (runs on the device)";
                VerticalBox {
                    HorizontalBox { Text { text: "Mode"; vertical-alignment: center; width: 140px; }
                        ComboBox { model: ["Static", "Pulse", "Fade"]; current-index <=> root.lighting-mode; } }
                    HorizontalBox { Text { text: "Speed (1-10)"; vertical-alignment: center; width: 140px; }
                        SpinBox { minimum: 1; maximum: 10; value <=> root.lighting-speed; } }
                    ColorPicker { label: "Min color"; r <=> root.min-r; g <=> root.min-g; b <=> root.min-b; }
                    ColorPicker { label: "Max color"; r <=> root.max-r; g <=> root.max-g; b <=> root.max-b; }
                    HorizontalBox { Text { text: "Idle mode"; vertical-alignment: center; width: 140px; }
                        ComboBox { model: ["Static", "Pulse", "Fade"]; current-index <=> root.idle-mode; } }
                    HorizontalBox { Text { text: "Idle after (s, 0=off)"; vertical-alignment: center; width: 140px; }
                        SpinBox { minimum: 0; maximum: 255; value <=> root.idle-timeout; } }
                }
            }
            GroupBox {
                title: "Device";
                VerticalBox {
                    HorizontalBox { Text { text: "Acceleration (0-100)"; vertical-alignment: center; width: 140px; }
                        SpinBox { minimum: 0; maximum: 100; value <=> root.acceleration; } }
                    HorizontalBox { Text { text: "Sleep after (s, 0=never)"; vertical-alignment: center; width: 140px; }
                        SpinBox { minimum: 0; maximum: 255; value <=> root.sleep-after; } }
                    CheckBox { text: "Continuous scroll"; checked <=> root.continuous-scroll; }
                    CheckBox { text: "Reverse LED ring"; checked <=> root.ring-reversed; }
                }
            }
            GroupBox {
                title: "General";
                CheckBox { text: "Start MaxMix at login"; checked <=> root.autostart; }
            }
            Text { text: root.status; wrap: word-wrap; }
            HorizontalBox {
                Button { text: "Defaults"; clicked => { root.reset-defaults(); } }
                Button { text: "Save"; primary: true; clicked => { root.save(); } }
            }
        }
    }
}

/// Plain snapshot of the form fields, decoupled from Slint so it is testable.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Form {
    color_min: String,
    color_max: String,
    lighting_mode: i32,
    lighting_speed: i32,
    idle_mode: i32,
    idle_timeout: i32,
    acceleration: i32,
    sleep_after: i32,
    continuous_scroll: bool,
    ring_reversed: bool,
    autostart: bool,
}

fn clamp_u8(v: i32, lo: u8, hi: u8) -> u8 {
    v.clamp(i32::from(lo), i32::from(hi)) as u8
}

impl Form {
    fn from_settings(s: &AppSettings) -> Self {
        Self {
            color_min: s.color_min.to_hex(),
            color_max: s.color_max.to_hex(),
            lighting_mode: s.lighting_mode.index() as i32,
            lighting_speed: i32::from(s.lighting_speed),
            idle_mode: s.idle_lighting_mode.index() as i32,
            idle_timeout: i32::from(s.idle_timeout_s),
            acceleration: i32::from(s.acceleration),
            sleep_after: i32::from(s.sleep_after_s),
            continuous_scroll: s.continuous_scroll,
            ring_reversed: s.ring_reversed,
            autostart: s.autostart,
        }
    }

    /// Validate and merge onto `base`. Errors name the offending field.
    fn apply_to(&self, base: &AppSettings) -> Result<AppSettings, String> {
        let color_min = Color::from_hex(&self.color_min)
            .ok_or_else(|| format!("Invalid min color '{}': use #RRGGBB", self.color_min))?;
        let color_max = Color::from_hex(&self.color_max)
            .ok_or_else(|| format!("Invalid max color '{}': use #RRGGBB", self.color_max))?;
        let mut s = base.clone();
        s.color_min = color_min;
        s.color_max = color_max;
        s.lighting_mode = Lighting::from_index(self.lighting_mode.max(0) as usize);
        s.lighting_speed = clamp_u8(self.lighting_speed, 1, 10);
        s.idle_lighting_mode = Lighting::from_index(self.idle_mode.max(0) as usize);
        s.idle_timeout_s = clamp_u8(self.idle_timeout, 0, 255);
        s.acceleration = clamp_u8(self.acceleration, 0, 100);
        s.sleep_after_s = clamp_u8(self.sleep_after, 0, 255);
        s.continuous_scroll = self.continuous_scroll;
        s.ring_reversed = self.ring_reversed;
        s.autostart = self.autostart;
        Ok(s)
    }
}

fn hex_to_rgb(hex: &str) -> (f32, f32, f32) {
    let c = Color::from_hex(hex).unwrap_or(Color::new(0, 0, 0));
    (f32::from(c.r), f32::from(c.g), f32::from(c.b))
}

fn rgb_to_hex(r: f32, g: f32, b: f32) -> String {
    let ch = |v: f32| v.round().clamp(0.0, 255.0) as u8;
    Color::new(ch(r), ch(g), ch(b)).to_hex()
}

fn write_form(ui: &SettingsWindow, f: &Form) {
    let (r, g, b) = hex_to_rgb(&f.color_min);
    ui.set_min_r(r);
    ui.set_min_g(g);
    ui.set_min_b(b);
    let (r, g, b) = hex_to_rgb(&f.color_max);
    ui.set_max_r(r);
    ui.set_max_g(g);
    ui.set_max_b(b);
    ui.set_lighting_mode(f.lighting_mode);
    ui.set_lighting_speed(f.lighting_speed);
    ui.set_idle_mode(f.idle_mode);
    ui.set_idle_timeout(f.idle_timeout);
    ui.set_acceleration(f.acceleration);
    ui.set_sleep_after(f.sleep_after);
    ui.set_continuous_scroll(f.continuous_scroll);
    ui.set_ring_reversed(f.ring_reversed);
    ui.set_autostart(f.autostart);
}

fn read_form(ui: &SettingsWindow) -> Form {
    Form {
        color_min: rgb_to_hex(ui.get_min_r(), ui.get_min_g(), ui.get_min_b()),
        color_max: rgb_to_hex(ui.get_max_r(), ui.get_max_g(), ui.get_max_b()),
        lighting_mode: ui.get_lighting_mode(),
        lighting_speed: ui.get_lighting_speed(),
        idle_mode: ui.get_idle_mode(),
        idle_timeout: ui.get_idle_timeout(),
        acceleration: ui.get_acceleration(),
        sleep_after: ui.get_sleep_after(),
        continuous_scroll: ui.get_continuous_scroll(),
        ring_reversed: ui.get_ring_reversed(),
        autostart: ui.get_autostart(),
    }
}

fn main() -> Result<(), slint::PlatformError> {
    let path = default_settings_path();
    let settings = load_or_init(&path);

    let ui = SettingsWindow::new()?;
    write_form(&ui, &Form::from_settings(&settings));
    ui.set_status(format!("Editing {}", path.display()).into());

    let weak = ui.as_weak();
    let save_path = path.clone();
    ui.on_save(move || {
        let Some(ui) = weak.upgrade() else { return };
        let base = load_or_init(&save_path);
        match read_form(&ui).apply_to(&base) {
            Ok(new) => match save(&save_path, &new) {
                Ok(()) => ui.set_status("Saved. The daemon applies it to the device.".into()),
                Err(e) => ui.set_status(format!("Could not save: {e}").into()),
            },
            Err(msg) => ui.set_status(msg.into()),
        }
    });

    let weak = ui.as_weak();
    ui.on_reset_defaults(move || {
        if let Some(ui) = weak.upgrade() {
            write_form(&ui, &Form::from_settings(&AppSettings::default()));
            ui.set_status("Defaults loaded — press Save to apply.".into());
        }
    });

    ui.run()
}

#[cfg(test)]
#[allow(clippy::field_reassign_with_default)]
mod tests {
    use super::*;

    #[test]
    fn form_round_trips_settings() {
        let s = AppSettings::default();
        assert_eq!(Form::from_settings(&s).apply_to(&s).unwrap(), s);
    }

    #[test]
    fn invalid_color_is_rejected_with_field_name() {
        let s = AppSettings::default();
        let mut f = Form::from_settings(&s);
        f.color_max = "#ZZZZZZ".into();
        let err = f.apply_to(&s).unwrap_err();
        assert!(err.contains("max color"), "{err}");
    }

    #[test]
    fn out_of_range_values_are_clamped() {
        let s = AppSettings::default();
        let mut f = Form::from_settings(&s);
        f.lighting_speed = 50;
        f.acceleration = -5;
        let out = f.apply_to(&s).unwrap();
        assert_eq!(out.lighting_speed, 10);
        assert_eq!(out.acceleration, 0);
    }

    #[test]
    fn slider_rgb_round_trips_hex() {
        let (r, g, b) = hex_to_rgb("#0080FF");
        assert_eq!(rgb_to_hex(r, g, b).to_uppercase(), "#0080FF");
        // Fractional slider values round and clamp to a valid byte.
        assert_eq!(rgb_to_hex(254.6, -3.0, 300.0).to_uppercase(), "#FF00FF");
    }

    #[test]
    fn untouched_fields_are_preserved() {
        let mut s = AppSettings::default();
        s.last_port = Some("/dev/ttyUSB0".into());
        let out = Form::from_settings(&s).apply_to(&s).unwrap();
        assert_eq!(out.last_port.as_deref(), Some("/dev/ttyUSB0"));
    }
}
