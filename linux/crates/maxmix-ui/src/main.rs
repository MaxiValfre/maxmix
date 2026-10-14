//! `maxmix-settings`: on-demand settings window.
//!
//! It only edits `~/.config/maxmix/settings.json`; the running daemon watches
//! that file and pushes changes to the device. Launched from the tray, it exits
//! when closed, so it costs nothing while not in use.
//!
//! The header shows a live preview of the LED ring (see [`preview`]), rendered
//! with the same maths as the firmware.

mod preview;

use std::cell::Cell;
use std::rc::Rc;
use std::time::{Duration, Instant};

use maxmix_core::{default_settings_path, load_or_init, save, AppSettings, Color, Lighting};
use slint::winit_030::WinitWindowAccessor;
use slint::{Model, VecModel};

use preview::{PreviewConfig, PIXELS};

slint::slint! {
    import { Button, CheckBox, ComboBox, Slider, SpinBox, TabWidget, Palette } from "std-widgets.slint";

    export struct Rgbf { r: float, g: float, b: float }

    export global Fmt {
        pure callback hex(float, float, float) -> string;
    }

    // A labelled form row: fixed label column so every control lines up.
    component Row inherits HorizontalLayout {
        in property <string> label;
        spacing: 12px;
        Text { text: root.label; width: 170px; vertical-alignment: center; color: Palette.foreground; }
        @children
    }

    // Compact colour row: swatch + hex; click opens a popup with sliders and a palette.
    component ColorRow inherits HorizontalLayout {
        in property <string> label;
        in-out property <float> r;
        in-out property <float> g;
        in-out property <float> b;
        property <[{r: int, g: int, b: int}]> palette-a: [
            {r: 255, g: 0, b: 0}, {r: 255, g: 96, b: 0}, {r: 255, g: 200, b: 0},
            {r: 0, g: 255, b: 0}, {r: 0, g: 255, b: 200}, {r: 0, g: 128, b: 255},
        ];
        property <[{r: int, g: int, b: int}]> palette-b: [
            {r: 0, g: 0, b: 255}, {r: 160, g: 0, b: 255}, {r: 255, g: 0, b: 160},
            {r: 255, g: 255, b: 255}, {r: 0, g: 0, b: 32}, {r: 0, g: 0, b: 0},
        ];
        spacing: 12px;
        Text { text: root.label; width: 170px; vertical-alignment: center; color: Palette.foreground; }
        swatch := Rectangle {
            width: 64px; height: 28px; border-radius: 6px;
            border-width: 1px; border-color: Palette.border;
            background: rgb(root.r, root.g, root.b);
            TouchArea { mouse-cursor: pointer; clicked => { popup.show(); } }
        }
        Text {
            text: Fmt.hex(root.r, root.g, root.b);
            vertical-alignment: center; color: Palette.foreground; opacity: 0.75;
        }
        Rectangle { horizontal-stretch: 1; }

        popup := PopupWindow {
            x: swatch.x;
            y: swatch.y + swatch.height + 6px;
            width: 290px;
            close-policy: close-on-click-outside;
            Rectangle {
                background: Palette.alternate-background;
                border-radius: 10px; border-width: 1px; border-color: Palette.border;
                drop-shadow-blur: 12px; drop-shadow-color: #0008;
                VerticalLayout {
                    padding: 12px; spacing: 8px;
                    HorizontalLayout { spacing: 8px;
                        Rectangle { width: 40px; height: 22px; border-radius: 4px; background: rgb(root.r, root.g, root.b); }
                        Text { text: Fmt.hex(root.r, root.g, root.b); vertical-alignment: center; color: Palette.foreground; }
                    }
                    HorizontalLayout { spacing: 8px;
                        Text { text: "R"; width: 14px; vertical-alignment: center; color: #f55; }
                        Slider { minimum: 0; maximum: 255; value <=> root.r; }
                    }
                    HorizontalLayout { spacing: 8px;
                        Text { text: "G"; width: 14px; vertical-alignment: center; color: #5d5; }
                        Slider { minimum: 0; maximum: 255; value <=> root.g; }
                    }
                    HorizontalLayout { spacing: 8px;
                        Text { text: "B"; width: 14px; vertical-alignment: center; color: #59f; }
                        Slider { minimum: 0; maximum: 255; value <=> root.b; }
                    }
                    for row in [root.palette-a, root.palette-b]: HorizontalLayout {
                        spacing: 8px; alignment: center;
                        for c in row: Rectangle {
                            width: 30px; height: 30px; border-radius: 15px;
                            border-width: 1px; border-color: Palette.border;
                            background: rgb(c.r, c.g, c.b);
                            TouchArea { mouse-cursor: pointer; clicked => { root.r = c.r; root.g = c.g; root.b = c.b; } }
                        }
                    }
                }
            }
        }
    }

    // The 8-LED ring, animated from Rust with the firmware's renderer.
    component RingPreview inherits Rectangle {
        in property <[color]> leds;
        width: 120px; height: 120px;
        Image {
            source: @image-url("../../../packaging/icons/maxmix.png");
            width: 34px; height: 34px;
            x: (parent.width - self.width) / 2; y: (parent.height - self.height) / 2;
            opacity: 0.85;
        }
        for c[i] in root.leds: Rectangle {
            width: 20px; height: 20px; border-radius: 10px;
            x: root.width / 2 + 44px * Math.cos(i * 45deg - 90deg) - self.width / 2;
            y: root.height / 2 + 44px * Math.sin(i * 45deg - 90deg) - self.height / 2;
            background: c;
            border-width: 1px; border-color: #0006;
            drop-shadow-color: c; drop-shadow-blur: 10px;
        }
    }

    export component SettingsWindow inherits Window {
        title: "MaxMix";
        icon: @image-url("../../../packaging/icons/maxmix.png");
        preferred-width: 500px;
        min-width: 460px;

        in-out property <[Rgbf]> colors;
        in-out property <float> vmin-r;
        in-out property <float> vmin-g;
        in-out property <float> vmin-b;
        in-out property <float> vmax-r;
        in-out property <float> vmax-g;
        in-out property <float> vmax-b;
        in-out property <int> lighting-mode;
        in-out property <float> lighting-speed;
        in-out property <int> idle-mode;
        in-out property <int> idle-timeout;
        in-out property <float> acceleration;
        in-out property <int> sleep-after;
        in-out property <int> dim-after;
        in-out property <bool> continuous-scroll;
        in-out property <bool> ring-reversed;
        in-out property <bool> autostart;
        in-out property <string> status;
        in-out property <int> tab;
        in-out property <float> preview-volume: 60;
        in property <[color]> leds;

        callback save();
        callback reset-defaults();
        callback preview-volume-moved();
        callback add-color();
        callback remove-color(int);

        property <[string]> modes: ["Fijo", "Pulso", "Fundido", "Arcoíris", "Carrusel"];

        VerticalLayout {
            padding: 16px; spacing: 12px;

            // Header: live ring preview + title + volume tester.
            HorizontalLayout {
                spacing: 18px;
                RingPreview { leds: root.leds; }
                VerticalLayout {
                    alignment: center; spacing: 6px;
                    Text { text: "MaxMix"; font-size: 22px; font-weight: 700; color: Palette.foreground; }
                    Text {
                        text: root.tab == 1 ? "Vista previa del indicador de volumen" : "Vista previa del anillo";
                        color: Palette.foreground; opacity: 0.65;
                    }
                    HorizontalLayout { spacing: 8px;
                        Text { text: "Probar volumen"; vertical-alignment: center; color: Palette.foreground; opacity: 0.8; }
                        Slider {
                            minimum: 0; maximum: 100; value <=> root.preview-volume;
                            changed => { root.preview-volume-moved(); }
                        }
                        Text { text: Math.round(root.preview-volume) + "%"; width: 36px; vertical-alignment: center; color: Palette.foreground; }
                    }
                }
            }

            TabWidget {
                current-index <=> root.tab;
                Tab {
                    title: "Luces";
                    VerticalLayout { padding: 14px; spacing: 10px; alignment: start;
                        Row { label: "Modo";
                            ComboBox { model: root.modes; current-index <=> root.lighting-mode; } }
                        Row { label: "Velocidad";
                            Slider { minimum: 1; maximum: 10; step: 1; value <=> root.lighting-speed; }
                            Text { text: Math.round(root.lighting-speed); width: 24px; vertical-alignment: center; color: Palette.foreground; } }
                        for c[i] in root.colors: HorizontalLayout {
                            spacing: 8px;
                            ColorRow { label: "Color " + (i + 1); r <=> c.r; g <=> c.g; b <=> c.b; }
                            Button {
                                text: "✕"; width: 36px;
                                enabled: root.colors.length > 1;
                                clicked => { root.remove-color(i); }
                            }
                        }
                        Row { label: "";
                            Button {
                                text: root.colors.length < 8 ? "＋ Agregar color" : "Máximo 8 colores";
                                enabled: root.colors.length < 8;
                                clicked => { root.add-color(); }
                            }
                            Rectangle { horizontal-stretch: 1; }
                        }
                        Row { label: "Modo en reposo";
                            ComboBox { model: root.modes; current-index <=> root.idle-mode; } }
                        Row { label: "Reposo tras (s, 0 = nunca)";
                            SpinBox { minimum: 0; maximum: 255; value <=> root.idle-timeout; } }
                    }
                }
                Tab {
                    title: "Volumen";
                    VerticalLayout { padding: 14px; spacing: 10px; alignment: start;
                        Text {
                            text: "El anillo muestra el volumen al cambiarlo, mientras editás y (tenue, latiendo) cuando la app está muteada.";
                            wrap: word-wrap; color: Palette.foreground; opacity: 0.7;
                        }
                        ColorRow { label: "Volumen bajo"; r <=> root.vmin-r; g <=> root.vmin-g; b <=> root.vmin-b; }
                        ColorRow { label: "Volumen alto"; r <=> root.vmax-r; g <=> root.vmax-g; b <=> root.vmax-b; }
                    }
                }
                Tab {
                    title: "Dispositivo";
                    VerticalLayout { padding: 14px; spacing: 10px; alignment: start;
                        Row { label: "Aceleración del dial";
                            Slider { minimum: 0; maximum: 100; step: 1; value <=> root.acceleration; }
                            Text { text: Math.round(root.acceleration) + "%"; width: 40px; vertical-alignment: center; color: Palette.foreground; } }
                        Row { label: "Atenuar pantalla tras (s)";
                            SpinBox { minimum: 0; maximum: 255; value <=> root.dim-after; } }
                        Row { label: "Apagar pantalla tras (s)";
                            SpinBox { minimum: 0; maximum: 255; value <=> root.sleep-after; } }
                        Text { text: "0 = nunca. Atenuar protege la pantalla OLED del desgaste."; color: Palette.foreground; opacity: 0.6; }
                        CheckBox { text: "Navegación continua entre apps"; checked <=> root.continuous-scroll; }
                        CheckBox { text: "Invertir el anillo de LEDs"; checked <=> root.ring-reversed; }
                        CheckBox { text: "Iniciar MaxMix al iniciar sesión"; checked <=> root.autostart; }
                    }
                }
            }

            HorizontalLayout {
                spacing: 10px;
                Text { text: root.status; vertical-alignment: center; wrap: word-wrap; color: Palette.foreground; opacity: 0.75; horizontal-stretch: 1; }
                Button { text: "Restaurar"; clicked => { root.reset-defaults(); } }
                Button { text: "Guardar"; primary: true; clicked => { root.save(); } }
            }
        }
    }
}

/// Plain snapshot of the form fields, decoupled from Slint so it is testable.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Form {
    /// Lighting palette as #RRGGBB, 1..=8 entries.
    colors: Vec<String>,
    volume_min: String,
    volume_max: String,
    lighting_mode: i32,
    lighting_speed: i32,
    idle_mode: i32,
    idle_timeout: i32,
    acceleration: i32,
    sleep_after: i32,
    dim_after: i32,
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
            colors: s.palette().iter().map(|c| c.to_hex()).collect(),
            volume_min: s.volume_color_min.to_hex(),
            volume_max: s.volume_color_max.to_hex(),
            lighting_mode: s.lighting_mode.index() as i32,
            lighting_speed: i32::from(s.lighting_speed),
            idle_mode: s.idle_lighting_mode.index() as i32,
            idle_timeout: i32::from(s.idle_timeout_s),
            acceleration: i32::from(s.acceleration),
            sleep_after: i32::from(s.sleep_after_s),
            dim_after: i32::from(s.display_dim_after_s),
            continuous_scroll: s.continuous_scroll,
            ring_reversed: s.ring_reversed,
            autostart: s.autostart,
        }
    }

    /// Validate and merge onto `base`. Errors name the offending field.
    fn apply_to(&self, base: &AppSettings) -> Result<AppSettings, String> {
        let parse = |hex: &str, field: &str| {
            Color::from_hex(hex).ok_or_else(|| format!("{field} inválido '{hex}': usá #RRGGBB"))
        };
        let mut s = base.clone();
        let palette = self
            .colors
            .iter()
            .enumerate()
            .map(|(i, hex)| parse(hex, &format!("Color {}", i + 1)))
            .collect::<Result<Vec<_>, _>>()?;
        s.set_palette(&palette);
        s.volume_color_min = parse(&self.volume_min, "Color de volumen bajo")?;
        s.volume_color_max = parse(&self.volume_max, "Color de volumen alto")?;
        s.lighting_mode = Lighting::from_index(self.lighting_mode.max(0) as usize);
        s.lighting_speed = clamp_u8(self.lighting_speed, 1, 10);
        s.idle_lighting_mode = Lighting::from_index(self.idle_mode.max(0) as usize);
        s.idle_timeout_s = clamp_u8(self.idle_timeout, 0, 255);
        s.acceleration = clamp_u8(self.acceleration, 0, 100);
        s.sleep_after_s = clamp_u8(self.sleep_after, 0, 255);
        s.display_dim_after_s = clamp_u8(self.dim_after, 0, 255);
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

fn rgb_to_color(r: f32, g: f32, b: f32) -> Color {
    let ch = |v: f32| v.round().clamp(0.0, 255.0) as u8;
    Color::new(ch(r), ch(g), ch(b))
}

fn rgb_to_hex(r: f32, g: f32, b: f32) -> String {
    rgb_to_color(r, g, b).to_hex()
}

fn hex_to_rgbf(hex: &str) -> Rgbf {
    let (r, g, b) = hex_to_rgb(hex);
    Rgbf { r, g, b }
}

fn set_colors(ui: &SettingsWindow, colors: Vec<Rgbf>) {
    ui.set_colors(Rc::new(VecModel::from(colors)).into());
}

fn get_colors(ui: &SettingsWindow) -> Vec<Rgbf> {
    ui.get_colors().iter().collect()
}

fn write_form(ui: &SettingsWindow, f: &Form) {
    set_colors(ui, f.colors.iter().map(|h| hex_to_rgbf(h)).collect());
    let (r, g, b) = hex_to_rgb(&f.volume_min);
    ui.set_vmin_r(r);
    ui.set_vmin_g(g);
    ui.set_vmin_b(b);
    let (r, g, b) = hex_to_rgb(&f.volume_max);
    ui.set_vmax_r(r);
    ui.set_vmax_g(g);
    ui.set_vmax_b(b);
    ui.set_lighting_mode(f.lighting_mode);
    ui.set_lighting_speed(f.lighting_speed as f32);
    ui.set_idle_mode(f.idle_mode);
    ui.set_idle_timeout(f.idle_timeout);
    ui.set_acceleration(f.acceleration as f32);
    ui.set_sleep_after(f.sleep_after);
    ui.set_dim_after(f.dim_after);
    ui.set_continuous_scroll(f.continuous_scroll);
    ui.set_ring_reversed(f.ring_reversed);
    ui.set_autostart(f.autostart);
}

fn read_form(ui: &SettingsWindow) -> Form {
    Form {
        colors: get_colors(ui).iter().map(|c| rgb_to_hex(c.r, c.g, c.b)).collect(),
        volume_min: rgb_to_hex(ui.get_vmin_r(), ui.get_vmin_g(), ui.get_vmin_b()),
        volume_max: rgb_to_hex(ui.get_vmax_r(), ui.get_vmax_g(), ui.get_vmax_b()),
        lighting_mode: ui.get_lighting_mode(),
        lighting_speed: ui.get_lighting_speed().round() as i32,
        idle_mode: ui.get_idle_mode(),
        idle_timeout: ui.get_idle_timeout(),
        acceleration: ui.get_acceleration().round() as i32,
        sleep_after: ui.get_sleep_after(),
        dim_after: ui.get_dim_after(),
        continuous_scroll: ui.get_continuous_scroll(),
        ring_reversed: ui.get_ring_reversed(),
        autostart: ui.get_autostart(),
    }
}

/// Snapshot the preview inputs straight from the (possibly unsaved) form.
fn preview_config(ui: &SettingsWindow) -> PreviewConfig {
    PreviewConfig {
        mode: Lighting::from_index(ui.get_lighting_mode().max(0) as usize),
        speed: ui.get_lighting_speed().round().clamp(1.0, 10.0) as u8,
        colors: get_colors(ui).iter().map(|c| rgb_to_color(c.r, c.g, c.b)).collect(),
        volume_min: rgb_to_color(ui.get_vmin_r(), ui.get_vmin_g(), ui.get_vmin_b()),
        volume_max: rgb_to_color(ui.get_vmax_r(), ui.get_vmax_g(), ui.get_vmax_b()),
    }
}

/// LED colour for the preview; "off" LEDs stay faintly visible on screen.
fn led_color(c: Color) -> slint::Color {
    if c == Color::new(0, 0, 0) {
        slint::Color::from_rgb_u8(0x2a, 0x2a, 0x2a)
    } else {
        slint::Color::from_rgb_u8(c.r, c.g, c.b)
    }
}

/// Preview frame time while the window is in the foreground (~30 fps).
const PREVIEW_FRAME: Duration = Duration::from_millis(33);
/// Focus polling interval while the preview is paused in the background.
const BACKGROUND_POLL: Duration = Duration::from_millis(250);

/// Animate only when the window is visible, not minimised and focused. An
/// unknown focus state (non-winit backend) keeps animating.
fn preview_should_animate(focused: Option<bool>, minimized: bool, visible: bool) -> bool {
    visible && !minimized && focused.unwrap_or(true)
}

/// How long the gauge stays up after moving the test slider (device: 1.5s).
const GAUGE_HOLD: Duration = Duration::from_millis(1500);
const VOLUME_TAB: i32 = 1;
/// Palette size limit (firmware `MM_MAX_PALETTE`).
const MAX_COLORS: usize = 8;

fn main() -> Result<(), slint::PlatformError> {
    // Wayland compositors pick the window icon from the desktop entry whose
    // name matches the app id (maxmix-settings.desktop).
    let _ = slint::set_xdg_app_id("maxmix-settings");

    let path = default_settings_path();
    let settings = load_or_init(&path);

    let ui = SettingsWindow::new()?;
    ui.global::<Fmt>().on_hex(|r, g, b| rgb_to_hex(r, g, b).into());
    write_form(&ui, &Form::from_settings(&settings));

    // ---- live ring preview --------------------------------------------
    let leds = Rc::new(VecModel::from(vec![led_color(Color::new(0, 0, 0)); PIXELS as usize]));
    ui.set_leds(leds.clone().into());
    let gauge_until = Rc::new(Cell::new(Instant::now()));
    let level = Rc::new(Cell::new(0u16));
    let started = Instant::now();

    let g = gauge_until.clone();
    ui.on_preview_volume_moved(move || g.set(Instant::now() + GAUGE_HOLD));

    // The preview only animates while the window is focused and on screen; in
    // the background the timer just polls for focus a few times per second.
    let timer = Rc::new(slint::Timer::default());
    let weak = ui.as_weak();
    let weak_timer = Rc::downgrade(&timer);
    timer.start(slint::TimerMode::Repeated, PREVIEW_FRAME, move || {
        let Some(ui) = weak.upgrade() else { return };
        let window = ui.window();
        let focused = window.with_winit_window(|w| w.has_focus());
        let animate = preview_should_animate(focused, window.is_minimized(), window.is_visible());
        if let Some(t) = weak_timer.upgrade() {
            let wanted = if animate { PREVIEW_FRAME } else { BACKGROUND_POLL };
            if t.interval() != wanted {
                t.set_interval(wanted);
            }
        }
        if !animate {
            return;
        }
        let cfg = preview_config(&ui);
        let volume = ui.get_preview_volume().round().clamp(0.0, 100.0) as u8;
        let now_ms = started.elapsed().as_millis() as u32;
        level.set(preview::ease_q8(level.get(), preview::level_q8(volume)));
        let gauge = ui.get_tab() == VOLUME_TAB || Instant::now() < gauge_until.get();
        for i in 0..PIXELS {
            let c = if gauge {
                preview::render_gauge(&cfg, level.get(), i)
            } else {
                preview::render_pixel(&cfg, volume, i, now_ms)
            };
            if leds.row_data(i as usize) != Some(led_color(c)) {
                leds.set_row_data(i as usize, led_color(c));
            }
        }
    });

    // ---- palette editing ------------------------------------------------
    let weak = ui.as_weak();
    ui.on_add_color(move || {
        let Some(ui) = weak.upgrade() else { return };
        let mut colors = get_colors(&ui);
        if colors.len() < MAX_COLORS {
            // Start from the last colour so the new stop is easy to tweak.
            let last = colors.last().cloned().unwrap_or(Rgbf { r: 255.0, g: 255.0, b: 255.0 });
            colors.push(last);
            set_colors(&ui, colors);
        }
    });
    let weak = ui.as_weak();
    ui.on_remove_color(move |i| {
        let Some(ui) = weak.upgrade() else { return };
        let mut colors = get_colors(&ui);
        if colors.len() > 1 && (i as usize) < colors.len() {
            colors.remove(i as usize);
            set_colors(&ui, colors);
        }
    });

    // ---- actions --------------------------------------------------------
    let weak = ui.as_weak();
    let save_path = path.clone();
    ui.on_save(move || {
        let Some(ui) = weak.upgrade() else { return };
        let base = load_or_init(&save_path);
        match read_form(&ui).apply_to(&base) {
            Ok(new) => match save(&save_path, &new) {
                Ok(()) => ui.set_status("Guardado. Ya se aplicó al MaxMix.".into()),
                Err(e) => ui.set_status(format!("No se pudo guardar: {e}").into()),
            },
            Err(msg) => ui.set_status(msg.into()),
        }
    });

    let weak = ui.as_weak();
    ui.on_reset_defaults(move || {
        if let Some(ui) = weak.upgrade() {
            write_form(&ui, &Form::from_settings(&AppSettings::default()));
            ui.set_status("Valores por defecto cargados: apretá Guardar para aplicarlos.".into());
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
        f.colors[1] = "#ZZZZZZ".into();
        let err = f.apply_to(&s).unwrap_err();
        assert!(err.contains("Color 2"), "{err}");
    }

    #[test]
    fn out_of_range_values_are_clamped() {
        let s = AppSettings::default();
        let mut f = Form::from_settings(&s);
        f.lighting_speed = 50;
        f.acceleration = -5;
        f.dim_after = 999;
        let out = f.apply_to(&s).unwrap();
        assert_eq!(out.lighting_speed, 10);
        assert_eq!(out.acceleration, 0);
        assert_eq!(out.display_dim_after_s, 255);
    }

    #[test]
    fn palette_round_trips_through_the_form() {
        let mut s = AppSettings::default();
        s.set_palette(&[Color::new(255, 0, 0), Color::new(0, 255, 0), Color::new(0, 0, 255)]);
        let f = Form::from_settings(&s);
        assert_eq!(f.colors, vec!["#FF0000", "#00FF00", "#0000FF"]);
        let out = f.apply_to(&AppSettings::default()).unwrap();
        assert_eq!(out.palette(), s.palette());
        assert_eq!((out.color_min, out.color_max), (Color::new(255, 0, 0), Color::new(0, 0, 255)));
    }

    #[test]
    fn dim_timeout_round_trips() {
        let mut s = AppSettings::default();
        s.display_dim_after_s = 45;
        assert_eq!(Form::from_settings(&s).apply_to(&s).unwrap().display_dim_after_s, 45);
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

    #[test]
    fn preview_pauses_in_the_background() {
        assert!(preview_should_animate(Some(true), false, true), "focused: animate");
        assert!(!preview_should_animate(Some(false), false, true), "unfocused: pause");
        assert!(!preview_should_animate(Some(true), true, true), "minimised: pause");
        assert!(!preview_should_animate(Some(true), false, false), "hidden: pause");
        assert!(preview_should_animate(None, false, true), "unknown focus: animate");
    }

    #[test]
    fn off_leds_stay_visible_in_the_preview() {
        assert_ne!(led_color(Color::new(0, 0, 0)), slint::Color::from_rgb_u8(0, 0, 0));
        assert_eq!(led_color(Color::new(1, 2, 3)), slint::Color::from_rgb_u8(1, 2, 3));
    }
}
