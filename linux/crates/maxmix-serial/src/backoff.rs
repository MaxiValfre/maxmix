//! Exponential reconnect backoff: 500ms, doubling, capped at 30s; reset on success.

use std::time::Duration;

/// Produces reconnect delays that double each attempt up to a cap.
#[derive(Debug, Clone)]
pub struct Backoff {
    min: Duration,
    max: Duration,
    current: Option<Duration>,
}

impl Backoff {
    #[must_use]
    pub fn new(min: Duration, max: Duration) -> Self {
        Self { min, max, current: None }
    }

    /// Return the delay to wait before the next attempt, then grow the series.
    pub fn next_delay(&mut self) -> Duration {
        let d = self.current.unwrap_or(self.min);
        let doubled = d.saturating_mul(2).min(self.max);
        self.current = Some(doubled);
        d
    }

    /// Reset the series after a successful connection.
    pub fn reset(&mut self) {
        self.current = None;
    }
}

impl Default for Backoff {
    fn default() -> Self {
        Self::new(Duration::from_millis(500), Duration::from_secs(30))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn follows_expected_doubling_sequence_and_caps() {
        let mut b = Backoff::default();
        let secs_ms = |d: Duration| d.as_millis();
        assert_eq!(secs_ms(b.next_delay()), 500);
        assert_eq!(secs_ms(b.next_delay()), 1_000);
        assert_eq!(secs_ms(b.next_delay()), 2_000);
        assert_eq!(secs_ms(b.next_delay()), 4_000);
        assert_eq!(secs_ms(b.next_delay()), 8_000);
        assert_eq!(secs_ms(b.next_delay()), 16_000);
        assert_eq!(secs_ms(b.next_delay()), 30_000, "cap at 30s");
        assert_eq!(secs_ms(b.next_delay()), 30_000, "stays capped");
    }

    #[test]
    fn reset_returns_to_minimum() {
        let mut b = Backoff::default();
        b.next_delay();
        b.next_delay();
        b.reset();
        assert_eq!(b.next_delay().as_millis(), 500);
    }
}
