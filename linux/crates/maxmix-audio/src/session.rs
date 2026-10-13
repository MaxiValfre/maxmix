//! Audio session domain types.

use crate::identity::stable_id;

/// The kind of audio endpoint a session represents.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionKind {
    /// A per-application playback stream (PulseAudio sink-input).
    Application,
    /// A per-application capture stream (PulseAudio source-output).
    Input,
    /// An output device (sink).
    OutputDevice,
    /// An input device (source).
    InputDevice,
}

/// A logical audio session as MaxMix sees it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AudioSession {
    /// Stable identity key (binary path / app id / device name). Backend-owned.
    pub key: String,
    /// Stable 7-bit id derived from `key`.
    pub id: u8,
    /// Human-readable display name (pre-sanitisation).
    pub name: String,
    pub kind: SessionKind,
    /// Volume 0..=100.
    pub volume: u8,
    pub muted: bool,
    pub is_default: bool,
}

impl AudioSession {
    /// Build a session, deriving the stable id from `key`.
    #[must_use]
    pub fn new(
        key: impl Into<String>,
        name: impl Into<String>,
        kind: SessionKind,
        volume: u8,
        muted: bool,
        is_default: bool,
    ) -> Self {
        let key = key.into();
        let id = stable_id(&key);
        Self {
            key,
            id,
            name: name.into(),
            kind,
            volume: volume.min(100),
            muted,
            is_default,
        }
    }
}
