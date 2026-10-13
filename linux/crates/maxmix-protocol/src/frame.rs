//! Byte-level framing with resynchronisation.

use crate::crc::crc8;

/// Start-of-frame marker.
pub const SOF: u8 = 0xA5;

/// Maximum payload length. Bounded well under the ATmega328p serial RX buffer
/// so a full frame always fits on the firmware side.
pub const MAX_PAYLOAD: usize = 40;

/// A decoded frame: its message TYPE and raw payload bytes (CRC already verified).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RawFrame {
    pub msg_type: u8,
    pub payload: Vec<u8>,
}

/// Encode a framed message. Panics if `payload` exceeds [`MAX_PAYLOAD`] — callers
/// construct payloads from fixed-size message types, so an overflow is a bug.
#[must_use]
pub fn encode_frame(msg_type: u8, payload: &[u8]) -> Vec<u8> {
    assert!(
        payload.len() <= MAX_PAYLOAD,
        "payload {} exceeds MAX_PAYLOAD {}",
        payload.len(),
        MAX_PAYLOAD
    );
    let len = payload.len() as u8;
    let mut frame = Vec::with_capacity(4 + payload.len());
    frame.push(SOF);
    frame.push(len);
    frame.push(msg_type);
    frame.extend_from_slice(payload);
    // CRC covers LEN | TYPE | PAYLOAD (everything after SOF, except the CRC).
    let crc = crc8(&frame[1..]);
    frame.push(crc);
    frame
}

/// Streaming frame decoder. Feed raw serial bytes with [`FrameDecoder::push`]
/// and pull verified frames with [`FrameDecoder::next_frame`]. On any
/// corruption it discards one byte and keeps hunting for the next valid SOF,
/// so the stream self-heals.
#[derive(Debug, Default)]
pub struct FrameDecoder {
    buf: Vec<u8>,
}

impl FrameDecoder {
    #[must_use]
    pub fn new() -> Self {
        Self { buf: Vec::new() }
    }

    /// Append freshly received bytes.
    pub fn push(&mut self, bytes: &[u8]) {
        self.buf.extend_from_slice(bytes);
    }

    /// Pull the next complete, CRC-valid frame if one is available.
    ///
    /// Returns `None` when more bytes are needed. Call it in a loop until it
    /// returns `None` to drain everything currently decodable.
    pub fn next_frame(&mut self) -> Option<RawFrame> {
        loop {
            // Drop leading garbage until a SOF is at the front.
            match self.buf.iter().position(|&b| b == SOF) {
                None => {
                    self.buf.clear();
                    return None;
                }
                Some(0) => {}
                Some(sof_at) => {
                    self.buf.drain(..sof_at);
                }
            }

            // Need SOF + LEN + TYPE + CRC at minimum (payload may be zero).
            if self.buf.len() < 4 {
                return None;
            }

            let len = self.buf[1] as usize;
            if len > MAX_PAYLOAD {
                // Implausible length: this SOF was spurious. Skip it and resync.
                self.buf.remove(0);
                continue;
            }

            let total = 4 + len;
            if self.buf.len() < total {
                return None; // wait for the rest of the frame
            }

            // CRC over LEN | TYPE | PAYLOAD == buf[1..3+len]; CRC byte at buf[3+len].
            let crc_calc = crc8(&self.buf[1..3 + len]);
            let crc_rx = self.buf[3 + len];
            if crc_calc == crc_rx {
                let msg_type = self.buf[2];
                let payload = self.buf[3..3 + len].to_vec();
                self.buf.drain(..total);
                return Some(RawFrame { msg_type, payload });
            }

            // Bad CRC: this SOF did not start a real frame. Skip one byte, resync.
            self.buf.remove(0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trip_single_frame() {
        let encoded = encode_frame(0x12, &[1, 2, 3]);
        let mut d = FrameDecoder::new();
        d.push(&encoded);
        let f = d.next_frame().expect("one frame");
        assert_eq!(f.msg_type, 0x12);
        assert_eq!(f.payload, vec![1, 2, 3]);
        assert!(d.next_frame().is_none());
    }

    #[test]
    fn zero_length_payload() {
        let encoded = encode_frame(0x02, &[]);
        assert_eq!(encoded.len(), 4);
        let mut d = FrameDecoder::new();
        d.push(&encoded);
        let f = d.next_frame().unwrap();
        assert_eq!(f.msg_type, 0x02);
        assert!(f.payload.is_empty());
    }

    #[test]
    fn two_back_to_back_frames() {
        let mut stream = encode_frame(0x10, &[9]);
        stream.extend(encode_frame(0x11, &[7, 7]));
        let mut d = FrameDecoder::new();
        d.push(&stream);
        assert_eq!(d.next_frame().unwrap().msg_type, 0x10);
        assert_eq!(d.next_frame().unwrap().payload, vec![7, 7]);
        assert!(d.next_frame().is_none());
    }

    #[test]
    fn leading_garbage_is_skipped() {
        let mut stream = vec![0x00, 0xFF, 0x7E];
        stream.extend(encode_frame(0x20, &[42]));
        let mut d = FrameDecoder::new();
        d.push(&stream);
        let f = d.next_frame().expect("recovers after garbage");
        assert_eq!(f.msg_type, 0x20);
        assert_eq!(f.payload, vec![42]);
    }

    #[test]
    fn corrupted_crc_is_dropped_and_stream_recovers() {
        let mut bad = encode_frame(0x10, &[1, 2, 3]);
        let last = bad.len() - 1;
        bad[last] ^= 0xFF; // smash the CRC
        let good = encode_frame(0x11, &[5]);
        let mut d = FrameDecoder::new();
        d.push(&bad);
        d.push(&good);
        // The bad frame must not surface; the good one that follows must.
        let f = d.next_frame().expect("recovers to next good frame");
        assert_eq!(f.msg_type, 0x11);
        assert_eq!(f.payload, vec![5]);
    }

    #[test]
    fn partial_frame_waits_for_more_bytes() {
        let encoded = encode_frame(0x12, &[1, 2, 3, 4]);
        let (head, tail) = encoded.split_at(3);
        let mut d = FrameDecoder::new();
        d.push(head);
        assert!(d.next_frame().is_none(), "incomplete frame yields nothing");
        d.push(tail);
        assert_eq!(d.next_frame().unwrap().payload, vec![1, 2, 3, 4]);
    }

    #[test]
    fn spurious_sof_in_length_position_resyncs() {
        // A stray SOF followed by an implausible length then a real frame.
        let mut stream = vec![SOF, 0xFF]; // len 0xFF > MAX_PAYLOAD
        stream.extend(encode_frame(0x01, &[1]));
        let mut d = FrameDecoder::new();
        d.push(&stream);
        let f = d.next_frame().expect("resyncs past the bogus length");
        assert_eq!(f.msg_type, 0x01);
    }
}
