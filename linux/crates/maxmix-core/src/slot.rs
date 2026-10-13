//! Maps an ordered session list onto the firmware's 4 display slots and handles
//! knob navigation. Pure logic, fully unit-tested.

use maxmix_audio::AudioSession;
use maxmix_protocol::SessionSlot;

/// A view of the sessions mapped to the firmware's four slots around a cursor.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SlotView {
    pub current: Option<AudioSession>,
    pub alternate: Option<AudioSession>,
    pub previous: Option<AudioSession>,
    pub next: Option<AudioSession>,
    /// Index of the current session within the ordered list.
    pub current_index: usize,
    /// Total session count.
    pub count: usize,
}

impl SlotView {
    /// Session for a given firmware slot.
    #[must_use]
    pub fn slot(&self, slot: SessionSlot) -> Option<&AudioSession> {
        match slot {
            SessionSlot::Current => self.current.as_ref(),
            SessionSlot::Alternate => self.alternate.as_ref(),
            SessionSlot::Previous => self.previous.as_ref(),
            SessionSlot::Next => self.next.as_ref(),
        }
    }
}

/// Holds the ordered sessions and the navigation cursor.
#[derive(Debug, Default)]
pub struct SlotMapper {
    sessions: Vec<AudioSession>,
    cursor: usize,
    /// When true the cursor wraps around the ends; otherwise it clamps.
    pub continuous_scroll: bool,
}

impl SlotMapper {
    #[must_use]
    pub fn new(continuous_scroll: bool) -> Self {
        Self { sessions: Vec::new(), cursor: 0, continuous_scroll }
    }

    /// Replace the ordered session list, preserving the cursor's session when
    /// possible (by key), else clamping it into range.
    pub fn set_sessions(&mut self, sessions: Vec<AudioSession>) {
        let current_key = self.sessions.get(self.cursor).map(|s| s.key.clone());
        self.sessions = sessions;
        self.cursor = current_key
            .and_then(|k| self.sessions.iter().position(|s| s.key == k))
            .unwrap_or(0)
            .min(self.sessions.len().saturating_sub(1));
    }

    #[must_use]
    pub fn len(&self) -> usize {
        self.sessions.len()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.sessions.is_empty()
    }

    /// The session the cursor currently points at.
    #[must_use]
    pub fn current(&self) -> Option<&AudioSession> {
        self.sessions.get(self.cursor)
    }

    /// Move the cursor by `delta` (negative = previous). Respects wrap/clamp.
    pub fn navigate(&mut self, delta: i32) {
        if self.sessions.is_empty() {
            return;
        }
        let len = self.sessions.len() as i32;
        let raw = self.cursor as i32 + delta;
        self.cursor = if self.continuous_scroll {
            raw.rem_euclid(len) as usize
        } else {
            raw.clamp(0, len - 1) as usize
        };
    }

    /// Build the slot view around the current cursor.
    #[must_use]
    pub fn view(&self) -> SlotView {
        let len = self.sessions.len();
        let get = |i: usize| self.sessions.get(i).cloned();
        let rel = |delta: i32| -> Option<AudioSession> {
            if len == 0 {
                return None;
            }
            let len_i = len as i32;
            let idx = self.cursor as i32 + delta;
            if self.continuous_scroll {
                get(idx.rem_euclid(len_i) as usize)
            } else if idx >= 0 && idx < len_i {
                get(idx as usize)
            } else {
                None
            }
        };
        SlotView {
            current: get(self.cursor),
            alternate: rel(1),
            previous: rel(-1),
            next: rel(1),
            current_index: self.cursor,
            count: len,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use maxmix_audio::SessionKind;

    fn s(key: &str) -> AudioSession {
        AudioSession::new(key, key, SessionKind::Application, 50, false, false)
    }

    #[test]
    fn empty_view_is_all_none() {
        let m = SlotMapper::new(false);
        let v = m.view();
        assert!(v.current.is_none() && v.previous.is_none() && v.next.is_none());
        assert_eq!(v.count, 0);
    }

    #[test]
    fn clamp_navigation_stops_at_ends() {
        let mut m = SlotMapper::new(false);
        m.set_sessions(vec![s("a"), s("b"), s("c")]);
        m.navigate(-1); // already at 0, clamps
        assert_eq!(m.current().unwrap().key, "a");
        m.navigate(5); // clamps to last
        assert_eq!(m.current().unwrap().key, "c");
    }

    #[test]
    fn continuous_navigation_wraps() {
        let mut m = SlotMapper::new(true);
        m.set_sessions(vec![s("a"), s("b"), s("c")]);
        m.navigate(-1); // wraps to last
        assert_eq!(m.current().unwrap().key, "c");
        m.navigate(1); // wraps back to first
        assert_eq!(m.current().unwrap().key, "a");
    }

    #[test]
    fn cursor_follows_session_across_reorder() {
        let mut m = SlotMapper::new(false);
        m.set_sessions(vec![s("a"), s("b"), s("c")]);
        m.navigate(1); // on "b"
        m.set_sessions(vec![s("c"), s("b"), s("a")]); // reordered
        assert_eq!(m.current().unwrap().key, "b", "cursor tracks the session");
    }

    #[test]
    fn view_slots_around_cursor() {
        let mut m = SlotMapper::new(false);
        m.set_sessions(vec![s("a"), s("b"), s("c")]);
        m.navigate(1); // on "b"
        let v = m.view();
        assert_eq!(v.current.unwrap().key, "b");
        assert_eq!(v.previous.unwrap().key, "a");
        assert_eq!(v.next.unwrap().key, "c");
        assert_eq!(v.current_index, 1);
    }

    #[test]
    fn removing_current_session_clamps_cursor() {
        let mut m = SlotMapper::new(false);
        m.set_sessions(vec![s("a"), s("b"), s("c")]);
        m.navigate(2); // on "c" (index 2)
        m.set_sessions(vec![s("a"), s("b")]); // "c" gone
        assert!(m.current().is_some());
        assert_eq!(m.current().unwrap().key, "a");
    }
}
