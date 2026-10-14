//! System tray via `ksni` (StatusNotifierItem over D-Bus).
//!
//! Minimal by design: the RGB lives in the firmware now, so the tray only shows
//! connection status and offers Settings / Quit. Compiled only with the `tray`
//! feature so headless/server use needs no D-Bus.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

/// Shared tray-driven control flags.
#[derive(Clone)]
pub struct TrayControls {
    pub quit: Arc<AtomicBool>,
    pub open_settings: Arc<AtomicBool>,
}

struct MaxMixTray {
    controls: TrayControls,
    status: String,
}

impl MaxMixTray {
    /// Connected once the handshake succeeded ("Connected"/"Ready"); grey otherwise.
    fn connected(&self) -> bool {
        matches!(self.status.as_str(), "Connected" | "Ready")
    }

    /// Spanish label for the raw connection state.
    fn status_label(&self) -> &str {
        status_label(&self.status)
    }
}

fn status_label(raw: &str) -> &str {
    match raw {
        "Connected" | "Ready" => "Conectado",
        "Scanning" => "Buscando el MaxMix…",
        "Connecting" | "Handshaking" => "Conectando…",
        "Backoff" | "Disconnected" => "Desconectado",
        "Starting" => "Iniciando…",
        other => other,
    }
}

impl ksni::Tray for MaxMixTray {
    // No theme name: hosts would prefer it over the pixmap and lose the
    // connected/disconnected colour.
    fn icon_name(&self) -> String {
        String::new()
    }

    fn icon_pixmap(&self) -> Vec<ksni::Icon> {
        let rgb = if self.connected() {
            crate::icon::CONNECTED_RGB
        } else {
            crate::icon::DISCONNECTED_RGB
        };
        [22u32, 32, 48, 64]
            .into_iter()
            .map(|size| ksni::Icon {
                width: size as i32,
                height: size as i32,
                data: crate::icon::m_mark_argb(size, rgb),
            })
            .collect()
    }

    fn title(&self) -> String {
        format!("MaxMix — {}", self.status_label())
    }

    fn id(&self) -> String {
        "maxmix".into()
    }

    fn menu(&self) -> Vec<ksni::MenuItem<Self>> {
        use ksni::menu::{MenuItem, StandardItem};
        vec![
            StandardItem {
                label: format!("Estado: {}", self.status_label()),
                enabled: false,
                ..Default::default()
            }
            .into(),
            MenuItem::Separator,
            StandardItem {
                label: "Configuración…".into(),
                activate: Box::new(|this: &mut Self| {
                    this.controls.open_settings.store(true, Ordering::SeqCst);
                }),
                ..Default::default()
            }
            .into(),
            StandardItem {
                label: "Salir".into(),
                activate: Box::new(|this: &mut Self| {
                    this.controls.quit.store(true, Ordering::SeqCst);
                }),
                ..Default::default()
            }
            .into(),
        ]
    }
}

/// A handle to update the tray's displayed status.
pub struct TrayHandle {
    handle: ksni::Handle<MaxMixTray>,
}

impl TrayHandle {
    /// Update the connection status label shown in the tray.
    pub fn set_status(&self, status: impl Into<String>) {
        let status = status.into();
        self.handle.update(move |t: &mut MaxMixTray| t.status = status.clone());
    }
}

/// Start the tray service in its own thread. Returns a handle for status updates.
#[must_use]
pub fn spawn(controls: TrayControls) -> TrayHandle {
    let tray = MaxMixTray { controls, status: "Starting".into() };
    let service = ksni::TrayService::new(tray);
    let handle = service.handle();
    service.spawn();
    TrayHandle { handle }
}

#[cfg(test)]
mod tests {
    use super::status_label;

    #[test]
    fn states_are_shown_in_spanish() {
        assert_eq!(status_label("Ready"), "Conectado");
        assert_eq!(status_label("Scanning"), "Buscando el MaxMix…");
        assert_eq!(status_label("Disconnected"), "Desconectado");
        assert_eq!(status_label("Algo"), "Algo", "unknown states pass through");
    }
}
