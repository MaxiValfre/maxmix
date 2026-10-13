//! Feedback-loop guard.
//!
//! The orchestrator listens to both the audio backend and the device. Without a
//! guard, a knob turn → set audio volume → audio reports a volume change → send
//! it back to the device → oscillation. When we apply a volume/mute to audio
//! *because of* a device event, we record the expected echo; when audio reports
//! that same value back, we consume the marker and suppress the device send.
//!
//! Volume tolerance is ±2 because audio volume is a float round-tripped through
//! a 0..=100 byte, so the echo can return off-by-one or two. (Ported from the
//! .NET StateOrchestrator per-session expected-echo design.)

use std::collections::HashMap;

/// Default volume echo tolerance (percent points).
pub const ECHO_VOLUME_TOLERANCE: u8 = 2;

#[derive(Debug, Clone, Copy)]
struct Expected {
    volume: u8,
    muted: bool,
}

/// Per-session expected-echo markers.
#[derive(Debug, Default)]
pub struct EchoGuard {
    expected: HashMap<String, Expected>,
    tolerance: u8,
}

impl EchoGuard {
    #[must_use]
    pub fn new() -> Self {
        Self { expected: HashMap::new(), tolerance: ECHO_VOLUME_TOLERANCE }
    }

    #[must_use]
    pub fn with_tolerance(tolerance: u8) -> Self {
        Self { expected: HashMap::new(), tolerance }
    }

    /// Record that we are about to apply `volume`/`muted` to audio for `key`
    /// because of a device event. Call this BEFORE touching the audio backend,
    /// since the backend's notification can fire synchronously.
    pub fn expect(&mut self, key: &str, volume: u8, muted: bool) {
        self.expected.insert(key.to_string(), Expected { volume, muted });
    }

    /// Given an audio-reported change, return true if it should be SUPPRESSED
    /// (it is the echo of a device-initiated change we already know about).
    /// Consuming is single-shot: the marker is removed on match.
    pub fn should_suppress(&mut self, key: &str, volume: u8, muted: bool) -> bool {
        if let Some(exp) = self.expected.get(key).copied() {
            let vol_ok = volume.abs_diff(exp.volume) <= self.tolerance;
            let mute_ok = muted == exp.muted;
            if vol_ok && mute_ok {
                self.expected.remove(key);
                return true;
            }
        }
        false
    }

    /// Drop a marker without matching (e.g. the session disappeared).
    pub fn forget(&mut self, key: &str) {
        self.expected.remove(key);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn suppresses_exact_echo_once() {
        let mut g = EchoGuard::new();
        g.expect("spotify", 75, false);
        assert!(g.should_suppress("spotify", 75, false), "echo suppressed");
        assert!(
            !g.should_suppress("spotify", 75, false),
            "single-shot: second identical report is a real change"
        );
    }

    #[test]
    fn suppresses_within_tolerance() {
        let mut g = EchoGuard::new();
        g.expect("spotify", 75, false);
        assert!(g.should_suppress("spotify", 73, false), "±2 tolerated");
    }

    #[test]
    fn does_not_suppress_outside_tolerance() {
        let mut g = EchoGuard::new();
        g.expect("spotify", 75, false);
        assert!(!g.should_suppress("spotify", 60, false), "real change passes through");
        // The marker remains until it actually matches.
        assert!(g.should_suppress("spotify", 75, false));
    }

    #[test]
    fn mute_mismatch_is_not_suppressed() {
        let mut g = EchoGuard::new();
        g.expect("spotify", 50, true);
        assert!(!g.should_suppress("spotify", 50, false), "mute must match exactly");
    }

    #[test]
    fn unknown_session_never_suppressed() {
        let mut g = EchoGuard::new();
        assert!(!g.should_suppress("ghost", 50, false));
    }

    #[test]
    fn per_session_markers_are_independent() {
        let mut g = EchoGuard::new();
        g.expect("a", 10, false);
        g.expect("b", 20, false);
        assert!(g.should_suppress("b", 20, false));
        assert!(g.should_suppress("a", 10, false));
    }
}
