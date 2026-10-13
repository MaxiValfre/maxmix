//! MaxMix audio session management.
//!
//! The pure core — stable [`identity`], the [`session`] model, and the
//! reconciliation [`registry`] — is independent of any audio server and fully
//! unit-tested. Real audio access is behind the [`AudioBackend`] trait; the
//! PulseAudio implementation lives in [`pulse`] (feature `pulse`, default on)
//! and works on PipeWire via `pipewire-pulse` as well as on pure PulseAudio.

pub mod backend;
pub mod identity;
pub mod registry;
pub mod session;

#[cfg(feature = "pulse")]
pub mod pulse;

pub use backend::{AudioBackend, AudioError};
pub use identity::stable_id;
pub use registry::{SessionChange, SessionRegistry};
pub use session::{AudioSession, SessionKind};

#[cfg(feature = "pulse")]
pub use pulse::PulseBackend;

/// An in-memory backend for tests and for running the daemon without audio.
#[derive(Debug, Default)]
pub struct FakeBackend {
    pub sessions: Vec<AudioSession>,
}

impl FakeBackend {
    #[must_use]
    pub fn new(sessions: Vec<AudioSession>) -> Self {
        Self { sessions }
    }
}

impl AudioBackend for FakeBackend {
    fn list_sessions(&mut self) -> Result<Vec<AudioSession>, AudioError> {
        Ok(self.sessions.clone())
    }

    fn set_volume(&mut self, key: &str, volume: u8) -> Result<(), AudioError> {
        let s = self
            .sessions
            .iter_mut()
            .find(|s| s.key == key)
            .ok_or_else(|| AudioError::NotFound(key.to_string()))?;
        s.volume = volume.min(100);
        Ok(())
    }

    fn set_mute(&mut self, key: &str, muted: bool) -> Result<(), AudioError> {
        let s = self
            .sessions
            .iter_mut()
            .find(|s| s.key == key)
            .ok_or_else(|| AudioError::NotFound(key.to_string()))?;
        s.muted = muted;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fake_backend_applies_volume_and_mute() {
        let mut b = FakeBackend::new(vec![AudioSession::new(
            "spotify",
            "Spotify",
            SessionKind::Application,
            50,
            false,
            false,
        )]);
        b.set_volume("spotify", 80).unwrap();
        b.set_mute("spotify", true).unwrap();
        let s = &b.list_sessions().unwrap()[0];
        assert_eq!(s.volume, 80);
        assert!(s.muted);
    }

    #[test]
    fn fake_backend_rejects_unknown_key() {
        let mut b = FakeBackend::default();
        assert!(matches!(b.set_volume("nope", 10), Err(AudioError::NotFound(_))));
    }
}
