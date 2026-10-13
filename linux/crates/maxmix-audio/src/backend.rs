//! Audio backend abstraction so the session logic is independent of PulseAudio.

use crate::session::AudioSession;

/// Errors from an audio backend.
#[derive(Debug)]
pub enum AudioError {
    /// The backend could not reach the audio server.
    Unavailable(String),
    /// A requested session was not found.
    NotFound(String),
    /// Any other backend-specific failure.
    Backend(String),
}

impl std::fmt::Display for AudioError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            AudioError::Unavailable(m) => write!(f, "audio server unavailable: {m}"),
            AudioError::NotFound(m) => write!(f, "session not found: {m}"),
            AudioError::Backend(m) => write!(f, "audio backend error: {m}"),
        }
    }
}

impl std::error::Error for AudioError {}

/// A source of audio sessions and a sink for control commands.
///
/// Implementations snapshot the current sessions on [`AudioBackend::list_sessions`]
/// (used for periodic reconciliation) and apply volume/mute via the setters. The
/// stable 7-bit id is derived by the registry, so backends work in their own
/// native identifiers keyed by [`AudioSession::key`].
///
/// Not `Send`: the PulseAudio backend owns a libpulse mainloop (via `Rc`) that
/// must stay on one thread. The daemon creates and drives it on a dedicated
/// audio thread and never moves it across threads.
pub trait AudioBackend {
    /// Snapshot the current application playback sessions.
    fn list_sessions(&mut self) -> Result<Vec<AudioSession>, AudioError>;

    /// Set a session's volume (0..=100) by its identity key.
    fn set_volume(&mut self, key: &str, volume: u8) -> Result<(), AudioError>;

    /// Set a session's mute state by its identity key.
    fn set_mute(&mut self, key: &str, muted: bool) -> Result<(), AudioError>;
}
