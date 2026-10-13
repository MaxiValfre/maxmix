//! Session registry + reconciliation diff.
//!
//! The backend reports the *current* set of sessions (via events or a periodic
//! poll). The registry turns successive snapshots into a minimal change list so
//! the orchestrator only pushes real deltas to the device. This is pure logic,
//! fully unit-tested, independent of PulseAudio.

use std::collections::HashMap;

use crate::session::AudioSession;

/// A change between two reconciliations.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SessionChange {
    Added(AudioSession),
    Removed { key: String, id: u8 },
    VolumeChanged { key: String, id: u8, volume: u8, muted: bool },
    Renamed { key: String, id: u8, name: String },
    DefaultChanged { key: String, id: u8, is_default: bool },
}

/// Tracks the known sessions and produces diffs on each snapshot.
#[derive(Debug, Default)]
pub struct SessionRegistry {
    sessions: HashMap<String, AudioSession>,
}

impl SessionRegistry {
    #[must_use]
    pub fn new() -> Self {
        Self { sessions: HashMap::new() }
    }

    /// Current sessions, order unspecified.
    #[must_use]
    pub fn sessions(&self) -> impl Iterator<Item = &AudioSession> {
        self.sessions.values()
    }

    #[must_use]
    pub fn get(&self, key: &str) -> Option<&AudioSession> {
        self.sessions.get(key)
    }

    /// Replace the known set with `incoming`, returning the changes that occurred.
    pub fn reconcile(&mut self, incoming: Vec<AudioSession>) -> Vec<SessionChange> {
        let mut changes = Vec::new();
        let mut next: HashMap<String, AudioSession> = HashMap::with_capacity(incoming.len());

        for s in incoming {
            match self.sessions.get(&s.key) {
                None => changes.push(SessionChange::Added(s.clone())),
                Some(prev) => {
                    if prev.volume != s.volume || prev.muted != s.muted {
                        changes.push(SessionChange::VolumeChanged {
                            key: s.key.clone(),
                            id: s.id,
                            volume: s.volume,
                            muted: s.muted,
                        });
                    }
                    if prev.name != s.name {
                        changes.push(SessionChange::Renamed {
                            key: s.key.clone(),
                            id: s.id,
                            name: s.name.clone(),
                        });
                    }
                    if prev.is_default != s.is_default {
                        changes.push(SessionChange::DefaultChanged {
                            key: s.key.clone(),
                            id: s.id,
                            is_default: s.is_default,
                        });
                    }
                }
            }
            next.insert(s.key.clone(), s);
        }

        // Anything previously known but absent now was removed.
        for (key, prev) in &self.sessions {
            if !next.contains_key(key) {
                changes.push(SessionChange::Removed { key: key.clone(), id: prev.id });
            }
        }

        self.sessions = next;
        changes
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::session::SessionKind;

    fn app(key: &str, name: &str, vol: u8, muted: bool) -> AudioSession {
        AudioSession::new(key, name, SessionKind::Application, vol, muted, false)
    }

    #[test]
    fn first_snapshot_reports_all_as_added() {
        let mut r = SessionRegistry::new();
        let changes = r.reconcile(vec![app("spotify", "Spotify", 50, false)]);
        assert_eq!(changes.len(), 1);
        assert!(matches!(changes[0], SessionChange::Added(_)));
    }

    #[test]
    fn unchanged_snapshot_reports_nothing() {
        let mut r = SessionRegistry::new();
        r.reconcile(vec![app("spotify", "Spotify", 50, false)]);
        let changes = r.reconcile(vec![app("spotify", "Spotify", 50, false)]);
        assert!(changes.is_empty());
    }

    #[test]
    fn volume_change_is_detected() {
        let mut r = SessionRegistry::new();
        r.reconcile(vec![app("spotify", "Spotify", 50, false)]);
        let changes = r.reconcile(vec![app("spotify", "Spotify", 75, false)]);
        assert_eq!(
            changes,
            vec![SessionChange::VolumeChanged {
                key: "spotify".into(),
                id: crate::identity::stable_id("spotify"),
                volume: 75,
                muted: false,
            }]
        );
    }

    #[test]
    fn mute_change_is_detected() {
        let mut r = SessionRegistry::new();
        r.reconcile(vec![app("spotify", "Spotify", 50, false)]);
        let changes = r.reconcile(vec![app("spotify", "Spotify", 50, true)]);
        assert!(matches!(changes[0], SessionChange::VolumeChanged { muted: true, .. }));
    }

    #[test]
    fn removal_is_detected() {
        let mut r = SessionRegistry::new();
        r.reconcile(vec![app("spotify", "Spotify", 50, false)]);
        let changes = r.reconcile(vec![]);
        assert_eq!(
            changes,
            vec![SessionChange::Removed {
                key: "spotify".into(),
                id: crate::identity::stable_id("spotify"),
            }]
        );
    }

    #[test]
    fn rename_is_detected() {
        let mut r = SessionRegistry::new();
        r.reconcile(vec![app("chrome", "chrome", 30, false)]);
        let changes = r.reconcile(vec![app("chrome", "Google Chrome", 30, false)]);
        assert_eq!(changes.len(), 1);
        assert!(matches!(changes[0], SessionChange::Renamed { .. }));
    }

    #[test]
    fn add_and_remove_in_same_snapshot() {
        let mut r = SessionRegistry::new();
        r.reconcile(vec![app("a", "A", 10, false)]);
        let changes = r.reconcile(vec![app("b", "B", 20, false)]);
        assert!(changes.iter().any(|c| matches!(c, SessionChange::Added(s) if s.key == "b")));
        assert!(changes.iter().any(|c| matches!(c, SessionChange::Removed { key, .. } if key == "a")));
    }
}
