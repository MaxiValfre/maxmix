//! Abstraction over a byte-oriented serial link so the transport logic is
//! testable without real hardware.

use std::io;

/// A bidirectional byte link. `read` is expected to be non-blocking-ish:
/// it may return `Ok(0)` when no bytes are available within the port timeout.
pub trait SerialLink: Send {
    fn write_all(&mut self, data: &[u8]) -> io::Result<()>;
    /// Read available bytes into `buf`, returning the count (0 = none yet).
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize>;
}

/// A reactive in-memory link for tests: it mirrors the real firmware by only
/// replying *after* it observes a `Hello` frame, exactly like the device's
/// handshake. Queue additional device->host bytes with [`FakeLink::enqueue`].
#[cfg(any(test, feature = "testing"))]
pub struct FakeLink {
    pub written: Vec<u8>,
    incoming: std::collections::VecDeque<u8>,
    hello_reply: Vec<u8>,
    hello_seen: bool,
}

#[cfg(any(test, feature = "testing"))]
impl FakeLink {
    /// `hello_reply` is injected the first time a `Hello` (TYPE 0x01) frame is written.
    #[must_use]
    pub fn new(hello_reply: Vec<u8>) -> Self {
        Self {
            written: Vec::new(),
            incoming: std::collections::VecDeque::new(),
            hello_reply,
            hello_seen: false,
        }
    }

    /// Queue bytes the host will read on subsequent `read` calls.
    pub fn enqueue(&mut self, bytes: &[u8]) {
        self.incoming.extend(bytes.iter().copied());
    }
}

#[cfg(any(test, feature = "testing"))]
impl SerialLink for FakeLink {
    fn write_all(&mut self, data: &[u8]) -> io::Result<()> {
        self.written.extend_from_slice(data);
        // Detect a Hello frame: SOF, LEN=1, TYPE=0x01 somewhere in the stream.
        if !self.hello_seen {
            for w in self.written.windows(3) {
                if w[0] == maxmix_protocol::SOF && w[2] == 0x01 {
                    self.hello_seen = true;
                    let reply = self.hello_reply.clone();
                    self.enqueue(&reply);
                    break;
                }
            }
        }
        Ok(())
    }

    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        let mut n = 0;
        while n < buf.len() {
            match self.incoming.pop_front() {
                Some(b) => {
                    buf[n] = b;
                    n += 1;
                }
                None => break,
            }
        }
        Ok(n)
    }
}
