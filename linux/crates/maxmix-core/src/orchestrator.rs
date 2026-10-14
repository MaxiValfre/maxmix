//! The orchestration core as a pure event→action state machine.
//!
//! It owns the navigation cursor, the feedback guard, and the current settings,
//! and translates inbound events (audio snapshots, device knob/button frames)
//! into outbound actions (frames to send, audio volume/mute to apply). Keeping
//! it free of real I/O makes the bidirectional sync and the feedback guard
//! unit-testable; the daemon executes the returned actions.

use maxmix_audio::AudioSession;
use maxmix_protocol::{HostToDevice, SessionInfo, SessionSlot, VolumeData};

use crate::feedback::EchoGuard;
use crate::settings::AppSettings;
use crate::slot::SlotMapper;

/// Inbound events the orchestrator reacts to.
#[derive(Debug, Clone)]
pub enum Event {
    /// A fresh snapshot of the active application sessions (from reconciliation).
    AudioSnapshot(Vec<AudioSession>),
    /// A session's volume/mute changed in the audio server.
    AudioVolume { key: String, volume: u8, muted: bool },
    /// The device reported a knob-driven volume/mute change for a slot.
    DeviceVolume { slot: SessionSlot, volume: VolumeData },
    /// The device requested session navigation (direction 0 = prev, 1 = next).
    DeviceNavigate { direction: u8 },
}

/// Outbound actions the daemon must perform.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Action {
    /// Send a frame to the device.
    Send(HostToDevice),
    /// Apply a volume (0..=100) to an audio session.
    SetAudioVolume { key: String, volume: u8 },
    /// Apply a mute state to an audio session.
    SetAudioMute { key: String, muted: bool },
}

/// Mode byte sent in SessionInfo. We run an application-centric pipeline.
const MODE_APPLICATION: u8 = 2;

/// Pure orchestration state.
pub struct Orchestrator {
    mapper: SlotMapper,
    guard: EchoGuard,
    settings: AppSettings,
}

impl Orchestrator {
    #[must_use]
    pub fn new(settings: AppSettings) -> Self {
        Self {
            mapper: SlotMapper::new(settings.continuous_scroll),
            guard: EchoGuard::new(),
            settings,
        }
    }

    /// Frames to push on a fresh connection: device config + lighting + the
    /// current session window.
    #[must_use]
    pub fn on_connected(&self) -> Vec<Action> {
        let mut actions = vec![
            Action::Send(HostToDevice::DeviceConfig {
                acceleration: self.settings.acceleration,
                sleep_after_s: self.settings.sleep_after_s,
                continuous_scroll: self.settings.continuous_scroll,
                ring_reversed: self.settings.ring_reversed,
            }),
            Action::Send(HostToDevice::Lighting(self.settings.lighting_config())),
        ];
        actions.extend(self.session_window_frames());
        actions
    }

    /// Apply new settings, returning the frames needed to push them to the device.
    pub fn apply_settings(&mut self, settings: AppSettings) -> Vec<Action> {
        self.settings = settings;
        self.mapper.continuous_scroll = self.settings.continuous_scroll;
        vec![
            Action::Send(HostToDevice::DeviceConfig {
                acceleration: self.settings.acceleration,
                sleep_after_s: self.settings.sleep_after_s,
                continuous_scroll: self.settings.continuous_scroll,
                ring_reversed: self.settings.ring_reversed,
            }),
            Action::Send(HostToDevice::Lighting(self.settings.lighting_config())),
        ]
    }

    /// Handle one inbound event.
    pub fn handle(&mut self, event: Event) -> Vec<Action> {
        match event {
            Event::AudioSnapshot(sessions) => {
                self.mapper.set_sessions(sessions);
                self.session_window_frames()
            }
            Event::AudioVolume { key, volume, muted } => {
                // Always keep the mapper current, even for suppressed echoes.
                self.mapper.update_volume(&key, volume, muted);
                // Suppress the echo of a change we initiated from the device.
                if self.guard.should_suppress(&key, volume, muted) {
                    return Vec::new();
                }
                // A genuine audio-side change: if the session is on-screen, push it.
                self.frame_for_key(&key, volume, muted)
            }
            Event::DeviceVolume { slot, volume } => {
                let view = self.mapper.view();
                let Some(session) = view.slot(slot) else {
                    return Vec::new();
                };
                let key = session.key.clone();
                // Record the expected echo BEFORE applying, then apply to audio.
                self.guard.expect(&key, volume.volume, volume.is_muted);
                self.mapper.update_volume(&key, volume.volume, volume.is_muted);
                vec![
                    Action::SetAudioVolume { key: key.clone(), volume: volume.volume },
                    Action::SetAudioMute { key, muted: volume.is_muted },
                ]
            }
            Event::DeviceNavigate { direction } => {
                let delta = if direction == 0 { -1 } else { 1 };
                self.mapper.navigate(delta);
                self.session_window_frames()
            }
        }
    }

    /// Frames describing the current 4-slot window: SessionInfo then each slot.
    fn session_window_frames(&self) -> Vec<Action> {
        let view = self.mapper.view();
        let mut actions = vec![Action::Send(HostToDevice::SessionInfo(SessionInfo {
            mode: MODE_APPLICATION,
            current: view.current_index.min(255) as u8,
            count: view.count.min(255) as u8,
        }))];

        for slot in [
            SessionSlot::Current,
            SessionSlot::Alternate,
            SessionSlot::Previous,
            SessionSlot::Next,
        ] {
            if let Some(session) = view.slot(slot) {
                actions.push(Action::Send(HostToDevice::SessionData {
                    slot,
                    volume: session_volume(session),
                    name: session.name.clone(),
                }));
            }
        }
        actions
    }

    /// If `key` is currently shown, emit the SessionData frame for its slot.
    fn frame_for_key(&self, key: &str, volume: u8, muted: bool) -> Vec<Action> {
        let view = self.mapper.view();
        for slot in [
            SessionSlot::Current,
            SessionSlot::Alternate,
            SessionSlot::Previous,
            SessionSlot::Next,
        ] {
            if let Some(session) = view.slot(slot) {
                if session.key == key {
                    let mut v = session_volume(session);
                    v.volume = volume.min(100);
                    v.is_muted = muted;
                    return vec![Action::Send(HostToDevice::SessionData {
                        slot,
                        volume: v,
                        name: session.name.clone(),
                    })];
                }
            }
        }
        Vec::new()
    }
}

fn session_volume(s: &AudioSession) -> VolumeData {
    VolumeData {
        id: s.id,
        is_default: s.is_default,
        volume: s.volume.min(100),
        is_muted: s.muted,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use maxmix_audio::{AudioSession, SessionKind};

    fn sess(key: &str, vol: u8) -> AudioSession {
        AudioSession::new(key, key, SessionKind::Application, vol, false, false)
    }

    fn orch() -> Orchestrator {
        Orchestrator::new(AppSettings::default())
    }

    #[test]
    fn snapshot_emits_session_info_and_data() {
        let mut o = orch();
        let actions = o.handle(Event::AudioSnapshot(vec![sess("spotify", 50)]));
        assert!(actions.iter().any(|a| matches!(
            a,
            Action::Send(HostToDevice::SessionInfo(info)) if info.count == 1
        )));
        assert!(actions.iter().any(|a| matches!(
            a,
            Action::Send(HostToDevice::SessionData { name, .. }) if name == "spotify"
        )));
    }

    #[test]
    fn device_knob_sets_audio_and_does_not_echo_back() {
        let mut o = orch();
        o.handle(Event::AudioSnapshot(vec![sess("spotify", 50)]));

        // Knob moves current slot to 75%.
        let actions = o.handle(Event::DeviceVolume {
            slot: SessionSlot::Current,
            volume: VolumeData { id: 0, is_default: false, volume: 75, is_muted: false },
        });
        assert!(actions.iter().any(|a| matches!(
            a,
            Action::SetAudioVolume { key, volume: 75 } if key == "spotify"
        )));

        // The audio backend echoes 75% back → must produce NO device frame.
        let echo = o.handle(Event::AudioVolume {
            key: "spotify".into(),
            volume: 75,
            muted: false,
        });
        assert!(echo.is_empty(), "echo of device-initiated change is suppressed");
    }

    #[test]
    fn echo_within_tolerance_is_suppressed() {
        let mut o = orch();
        o.handle(Event::AudioSnapshot(vec![sess("spotify", 50)]));
        o.handle(Event::DeviceVolume {
            slot: SessionSlot::Current,
            volume: VolumeData { id: 0, is_default: false, volume: 75, is_muted: false },
        });
        // Audio reports 74 (float round-trip) — still suppressed.
        let echo = o.handle(Event::AudioVolume { key: "spotify".into(), volume: 74, muted: false });
        assert!(echo.is_empty());
    }

    #[test]
    fn genuine_audio_change_is_forwarded_to_device() {
        let mut o = orch();
        o.handle(Event::AudioSnapshot(vec![sess("spotify", 50)]));
        // No prior device action → a real Windows/pulse-side change must be sent.
        let actions = o.handle(Event::AudioVolume { key: "spotify".into(), volume: 30, muted: false });
        assert!(actions.iter().any(|a| matches!(
            a,
            Action::Send(HostToDevice::SessionData { volume, .. }) if volume.volume == 30
        )));
    }

    #[test]
    fn navigation_moves_cursor_and_reemits_window() {
        let mut o = orch();
        o.handle(Event::AudioSnapshot(vec![sess("a", 10), sess("b", 20), sess("c", 30)]));
        let actions = o.handle(Event::DeviceNavigate { direction: 1 });
        // Current index should now be 1 ("b").
        assert!(actions.iter().any(|a| matches!(
            a,
            Action::Send(HostToDevice::SessionInfo(info)) if info.current == 1
        )));
    }

    #[test]
    fn on_connected_pushes_config_lighting_and_sessions() {
        let mut o = orch();
        o.handle(Event::AudioSnapshot(vec![sess("spotify", 50)]));
        let actions = o.on_connected();
        assert!(actions.iter().any(|a| matches!(a, Action::Send(HostToDevice::DeviceConfig { .. }))));
        assert!(actions.iter().any(|a| matches!(a, Action::Send(HostToDevice::Lighting(_)))));
        assert!(actions.iter().any(|a| matches!(a, Action::Send(HostToDevice::SessionInfo(_)))));
    }
    fn sent_volume(actions: &[Action]) -> Option<u8> {
        actions.iter().find_map(|a| match a {
            Action::Send(HostToDevice::SessionData { slot: SessionSlot::Current, volume, .. }) => {
                Some(volume.volume)
            }
            _ => None,
        })
    }

    #[test]
    fn navigating_after_external_volume_change_resends_new_volume() {
        // Regression: the session window was rebuilt from the stale snapshot
        // volume, so turning the knob "reset" an app changed from the PC.
        let mut o = orch();
        o.handle(Event::AudioSnapshot(vec![sess("chrome", 100)]));
        o.handle(Event::AudioVolume { key: "chrome".into(), volume: 30, muted: false });
        let actions = o.handle(Event::DeviceNavigate { direction: 1 });
        assert_eq!(sent_volume(&actions), Some(30));
    }

    #[test]
    fn navigating_after_device_volume_change_resends_new_volume() {
        let mut o = orch();
        o.handle(Event::AudioSnapshot(vec![sess("chrome", 100)]));
        o.handle(Event::DeviceVolume {
            slot: SessionSlot::Current,
            volume: VolumeData { id: 0, is_default: false, volume: 40, is_muted: false },
        });
        // The echo from the audio server is suppressed, but the state must stick.
        o.handle(Event::AudioVolume { key: "chrome".into(), volume: 40, muted: false });
        let actions = o.handle(Event::DeviceNavigate { direction: 1 });
        assert_eq!(sent_volume(&actions), Some(40));
    }
}
