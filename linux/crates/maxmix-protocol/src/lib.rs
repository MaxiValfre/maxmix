//! MaxMix serial protocol v2 (host side).
//!
//! Wire format is **framed and CRC-checked**, unlike the original raw
//! command+payload protocol whose single-byte misalignment desynced the
//! firmware permanently (historical Bug #1). A frame is:
//!
//! ```text
//! +------+------+------+--------------------+------+
//! | SOF  | LEN  | TYPE | PAYLOAD (LEN bytes)| CRC8 |
//! | 0xA5 | u8   | u8   | ...                | u8   |
//! +------+------+------+--------------------+------+
//! ```
//!
//! * `LEN` is the payload length only (0..=[`MAX_PAYLOAD`]).
//! * `CRC8` (Dallas/Maxim, poly 0x31, init 0x00) covers `LEN | TYPE | PAYLOAD`.
//! * The decoder resynchronises by discarding bytes until a valid frame parses,
//!   so a corrupted or spurious byte can never poison the stream permanently.

pub mod crc;
pub mod frame;
pub mod message;
pub mod sanitize;

pub use crc::crc8;
pub use frame::{encode_frame, FrameDecoder, RawFrame, MAX_PAYLOAD, SOF};
pub use message::{
    DeviceToHost, HostToDevice, LightingConfig, LightingMode, Rgb, SessionInfo, SessionSlot,
    VolumeData, MAX_PALETTE, PROTO_VERSION,
};
pub use sanitize::to_ascii_name;

/// Errors produced while decoding a typed message from a raw frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DecodeError {
    /// The payload length did not match what the message type requires.
    BadLength { msg_type: u8, got: usize },
    /// The TYPE byte is not a known message for this direction.
    UnknownType(u8),
    /// A field held a value outside its valid range.
    BadField(&'static str),
}

impl core::fmt::Display for DecodeError {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            DecodeError::BadLength { msg_type, got } => {
                write!(f, "bad payload length {got} for type 0x{msg_type:02X}")
            }
            DecodeError::UnknownType(t) => write!(f, "unknown message type 0x{t:02X}"),
            DecodeError::BadField(name) => write!(f, "invalid field: {name}"),
        }
    }
}

impl std::error::Error for DecodeError {}
