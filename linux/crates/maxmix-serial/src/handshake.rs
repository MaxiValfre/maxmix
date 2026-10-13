//! Protocol v2 handshake: host sends `Hello`, device answers `HelloAck`.

use std::io;
use std::time::{Duration, Instant};

use maxmix_protocol::{DeviceToHost, FrameDecoder, HostToDevice, PROTO_VERSION};

use crate::link::SerialLink;

/// The device's handshake reply.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DeviceInfo {
    pub fw_major: u8,
    pub fw_minor: u8,
    pub capabilities: u8,
}

/// Incremental handshake matcher: feed received bytes until a `HelloAck` arrives.
/// Kept separate from I/O so it is unit-testable without timing or hardware.
#[derive(Debug, Default)]
pub struct Handshaker {
    decoder: FrameDecoder,
}

impl Handshaker {
    #[must_use]
    pub fn new() -> Self {
        Self { decoder: FrameDecoder::new() }
    }

    /// The bytes the host must send to start the handshake.
    #[must_use]
    pub fn hello_bytes() -> Vec<u8> {
        HostToDevice::Hello { proto_version: PROTO_VERSION }.encode()
    }

    /// Feed received bytes; returns `Some(DeviceInfo)` once a `HelloAck` decodes.
    pub fn on_bytes(&mut self, bytes: &[u8]) -> Option<DeviceInfo> {
        self.decoder.push(bytes);
        while let Some(frame) = self.decoder.next_frame() {
            if let Ok(DeviceToHost::HelloAck { fw_major, fw_minor, capabilities }) =
                DeviceToHost::decode(&frame)
            {
                return Some(DeviceInfo { fw_major, fw_minor, capabilities });
            }
        }
        None
    }
}

/// Drive a full handshake over `link` within `timeout`. Sends `Hello`, then
/// reads until a `HelloAck` is decoded or the deadline passes.
pub fn perform_handshake<L: SerialLink + ?Sized>(
    link: &mut L,
    timeout: Duration,
) -> io::Result<DeviceInfo> {
    link.write_all(&Handshaker::hello_bytes())?;

    let mut hs = Handshaker::new();
    let mut buf = [0u8; 64];
    let deadline = Instant::now() + timeout;

    while Instant::now() < deadline {
        let n = link.read(&mut buf)?;
        if n > 0 {
            if let Some(info) = hs.on_bytes(&buf[..n]) {
                return Ok(info);
            }
        } else {
            std::thread::sleep(Duration::from_millis(5));
        }
    }
    Err(io::Error::new(io::ErrorKind::TimedOut, "handshake timed out"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::link::FakeLink;

    #[test]
    fn handshaker_matches_ack_split_across_reads() {
        let ack = DeviceToHost::HelloAck { fw_major: 2, fw_minor: 1, capabilities: 0x03 }.encode();
        let (a, b) = ack.split_at(2);
        let mut hs = Handshaker::new();
        assert_eq!(hs.on_bytes(a), None, "partial yields nothing");
        let info = hs.on_bytes(b).expect("ack completes");
        assert_eq!(info, DeviceInfo { fw_major: 2, fw_minor: 1, capabilities: 0x03 });
    }

    #[test]
    fn perform_handshake_succeeds_against_reactive_fake() {
        let ack = DeviceToHost::HelloAck { fw_major: 2, fw_minor: 0, capabilities: 1 }.encode();
        let mut link = FakeLink::new(ack);
        let info = perform_handshake(&mut link, Duration::from_millis(500)).unwrap();
        assert_eq!(info.fw_major, 2);
        // The host must have actually sent a Hello.
        assert!(!link.written.is_empty());
    }

    #[test]
    fn perform_handshake_times_out_without_reply() {
        let mut link = FakeLink::new(Vec::new()); // never replies
        let err = perform_handshake(&mut link, Duration::from_millis(30)).unwrap_err();
        assert_eq!(err.kind(), io::ErrorKind::TimedOut);
    }
}
