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

impl ksni::Tray for MaxMixTray {
    fn icon_name(&self) -> String {
        "audio-volume-high".into()
    }

    fn title(&self) -> String {
        format!("MaxMix — {}", self.status)
    }

    fn id(&self) -> String {
        "maxmix".into()
    }

    fn menu(&self) -> Vec<ksni::MenuItem<Self>> {
        use ksni::menu::{MenuItem, StandardItem};
        vec![
            StandardItem {
                label: format!("Status: {}", self.status),
                enabled: false,
                ..Default::default()
            }
            .into(),
            MenuItem::Separator,
            StandardItem {
                label: "Settings…".into(),
                activate: Box::new(|this: &mut Self| {
                    this.controls.open_settings.store(true, Ordering::SeqCst);
                }),
                ..Default::default()
            }
            .into(),
            StandardItem {
                label: "Quit".into(),
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
