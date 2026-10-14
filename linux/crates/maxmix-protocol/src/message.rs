//! Typed messages for protocol v2 and their (de)serialisation to raw frames.

use crate::frame::{encode_frame, RawFrame};
use crate::sanitize::{decode_name_field, encode_name_field, NAME_FIELD_BYTES};
use crate::DecodeError;

/// Protocol version advertised in the handshake. Firmware and host are versioned
/// together; a mismatch is a hard incompatibility.
pub const PROTO_VERSION: u8 = 2;

// ---- Message type bytes -------------------------------------------------

// Host -> Device
const T_HELLO: u8 = 0x01;
const T_PING: u8 = 0x02;
const T_SESSION_INFO: u8 = 0x10;
const T_SESSION_DATA: u8 = 0x11;
const T_VOLUME_SET: u8 = 0x12;
const T_LIGHTING_CONFIG: u8 = 0x20;
const T_DEVICE_CONFIG: u8 = 0x21;

// Device -> Host
const T_HELLO_ACK: u8 = 0x81;
const T_PONG: u8 = 0x82;
const T_VOLUME_CHANGE: u8 = 0x90;
const T_BUTTON: u8 = 0x91;
const T_SESSION_REQUEST: u8 = 0x92;

const SEVEN_BIT: u8 = 0x7F;
const HIGH_BIT: u8 = 0x80;

/// An RGB colour (no alpha).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Rgb {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Rgb {
    #[must_use]
    pub const fn new(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }
}

/// Per-session volume state, packed into 2 bytes on the wire:
/// `byte0 = id(7) | isDefault(hi)`, `byte1 = volume(7) | isMuted(hi)`.
/// This layout is inherited verbatim from the firmware contract.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VolumeData {
    /// Stable session id, 0..=127.
    pub id: u8,
    pub is_default: bool,
    /// Volume 0..=100.
    pub volume: u8,
    pub is_muted: bool,
}

impl VolumeData {
    #[must_use]
    pub fn pack(self) -> [u8; 2] {
        [
            (self.id & SEVEN_BIT) | if self.is_default { HIGH_BIT } else { 0 },
            (self.volume & SEVEN_BIT) | if self.is_muted { HIGH_BIT } else { 0 },
        ]
    }

    #[must_use]
    pub fn unpack(bytes: [u8; 2]) -> Self {
        Self {
            id: bytes[0] & SEVEN_BIT,
            is_default: bytes[0] & HIGH_BIT != 0,
            volume: bytes[1] & SEVEN_BIT,
            is_muted: bytes[1] & HIGH_BIT != 0,
        }
    }
}

/// The firmware's four display slots.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionSlot {
    Current = 0,
    Alternate = 1,
    Previous = 2,
    Next = 3,
}

impl SessionSlot {
    #[must_use]
    pub fn from_u8(v: u8) -> Option<Self> {
        match v {
            0 => Some(Self::Current),
            1 => Some(Self::Alternate),
            2 => Some(Self::Previous),
            3 => Some(Self::Next),
            _ => None,
        }
    }
}

/// Firmware-side lighting mode. The firmware runs the animation autonomously;
/// the host sets the config once rather than streaming colour frames.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LightingMode {
    Static = 0,
    Pulse = 1,
    Fade = 2,
    /// Full-ring hue wheel rotating around the ring (ignores volume).
    Rainbow = 3,
    /// The min->max->min gradient rotating around the ring (ignores volume).
    Carousel = 4,
}

impl LightingMode {
    #[must_use]
    pub fn from_u8(v: u8) -> Option<Self> {
        match v {
            0 => Some(Self::Static),
            1 => Some(Self::Pulse),
            2 => Some(Self::Fade),
            3 => Some(Self::Rainbow),
            4 => Some(Self::Carousel),
            _ => None,
        }
    }
}

/// Maximum number of colours in the lighting palette (one per ring LED).
pub const MAX_PALETTE: usize = 8;

/// Lighting configuration pushed to the firmware once; it owns the animation.
///
/// Wire layout (11 + 3n bytes for n palette colours, 1..=8):
/// `mode, speed, n, colour[n] (rgb), idle_mode, idle_timeout_s, volume_min, volume_max`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LightingConfig {
    pub mode: LightingMode,
    /// Animation speed 1..=10.
    pub speed: u8,
    /// Palette; only the first `color_count` entries are used (unused are black).
    pub colors: [Rgb; MAX_PALETTE],
    pub color_count: u8,
    /// Lighting mode to use while idle (device autonomous).
    pub idle_mode: LightingMode,
    /// Seconds of inactivity before idle lighting kicks in (0 = disabled).
    pub idle_timeout_s: u8,
    /// Gradient of the volume gauge shown while the volume changes.
    pub volume_min: Rgb,
    pub volume_max: Rgb,
}

impl LightingConfig {
    /// Pack up to [`MAX_PALETTE`] colours (extra ones are dropped; an empty
    /// palette becomes a single black entry so the firmware always has one).
    #[must_use]
    pub fn palette_from(colors: &[Rgb]) -> ([Rgb; MAX_PALETTE], u8) {
        let mut out = [Rgb::new(0, 0, 0); MAX_PALETTE];
        let n = colors.len().clamp(1, MAX_PALETTE);
        for (slot, c) in out.iter_mut().zip(colors.iter()) {
            *slot = *c;
        }
        (out, n as u8)
    }

    /// The colours actually in use.
    #[must_use]
    pub fn palette(&self) -> &[Rgb] {
        &self.colors[..usize::from(self.color_count).clamp(1, MAX_PALETTE)]
    }
}

/// Session counts + current selection, sent before the per-slot data.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SessionInfo {
    pub mode: u8,
    pub current: u8,
    pub count: u8,
}

/// Messages the host sends to the device.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HostToDevice {
    Hello { proto_version: u8 },
    Ping,
    SessionInfo(SessionInfo),
    SessionData {
        slot: SessionSlot,
        volume: VolumeData,
        name: String,
    },
    VolumeSet {
        slot: SessionSlot,
        volume: VolumeData,
    },
    Lighting(LightingConfig),
    DeviceConfig {
        acceleration: u8,
        sleep_after_s: u8,
        continuous_scroll: bool,
        ring_reversed: bool,
        /// Seconds without input before the OLED dims (anti burn-in, 0 = never).
        dim_after_s: u8,
    },
}

impl HostToDevice {
    /// Serialise into a complete framed byte buffer ready for the serial port.
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        match self {
            HostToDevice::Hello { proto_version } => encode_frame(T_HELLO, &[*proto_version]),
            HostToDevice::Ping => encode_frame(T_PING, &[]),
            HostToDevice::SessionInfo(info) => {
                encode_frame(T_SESSION_INFO, &[info.mode, info.current, info.count])
            }
            HostToDevice::SessionData { slot, volume, name } => {
                let mut p = Vec::with_capacity(1 + 2 + NAME_FIELD_BYTES);
                p.push(*slot as u8);
                p.extend_from_slice(&volume.pack());
                p.extend_from_slice(&encode_name_field(name));
                encode_frame(T_SESSION_DATA, &p)
            }
            HostToDevice::VolumeSet { slot, volume } => {
                let mut p = Vec::with_capacity(3);
                p.push(*slot as u8);
                p.extend_from_slice(&volume.pack());
                encode_frame(T_VOLUME_SET, &p)
            }
            HostToDevice::Lighting(cfg) => {
                let palette = cfg.palette();
                let mut p = vec![cfg.mode as u8, cfg.speed, palette.len() as u8];
                for c in palette {
                    p.extend_from_slice(&[c.r, c.g, c.b]);
                }
                p.extend_from_slice(&[
                    cfg.idle_mode as u8,
                    cfg.idle_timeout_s,
                    cfg.volume_min.r,
                    cfg.volume_min.g,
                    cfg.volume_min.b,
                    cfg.volume_max.r,
                    cfg.volume_max.g,
                    cfg.volume_max.b,
                ]);
                encode_frame(T_LIGHTING_CONFIG, &p)
            }
            HostToDevice::DeviceConfig {
                acceleration,
                sleep_after_s,
                continuous_scroll,
                ring_reversed,
                dim_after_s,
            } => {
                let flags = (u8::from(*continuous_scroll)) | (u8::from(*ring_reversed) << 1);
                encode_frame(T_DEVICE_CONFIG, &[*acceleration, *sleep_after_s, flags, *dim_after_s])
            }
        }
    }
}

/// Messages the device sends to the host.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DeviceToHost {
    HelloAck {
        fw_major: u8,
        fw_minor: u8,
        capabilities: u8,
    },
    Pong,
    /// Knob turn / press changed a slot's volume or mute.
    VolumeChange {
        slot: SessionSlot,
        volume: VolumeData,
    },
    /// A physical button event (0 = mode, 1 = select, ...).
    Button {
        event: u8,
    },
    /// The user scrolled the knob to navigate sessions (0 = prev, 1 = next).
    SessionRequest {
        direction: u8,
    },
}

impl DeviceToHost {
    /// Serialise into a complete framed byte buffer (used by firmware tests/fakes).
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        match self {
            DeviceToHost::HelloAck {
                fw_major,
                fw_minor,
                capabilities,
            } => encode_frame(T_HELLO_ACK, &[*fw_major, *fw_minor, *capabilities]),
            DeviceToHost::Pong => encode_frame(T_PONG, &[]),
            DeviceToHost::VolumeChange { slot, volume } => {
                let mut p = Vec::with_capacity(3);
                p.push(*slot as u8);
                p.extend_from_slice(&volume.pack());
                encode_frame(T_VOLUME_CHANGE, &p)
            }
            DeviceToHost::Button { event } => encode_frame(T_BUTTON, &[*event]),
            DeviceToHost::SessionRequest { direction } => {
                encode_frame(T_SESSION_REQUEST, &[*direction])
            }
        }
    }

    /// Parse a device->host message from a verified raw frame.
    pub fn decode(frame: &RawFrame) -> Result<Self, DecodeError> {
        let p = &frame.payload;
        let need = |n: usize| -> Result<(), DecodeError> {
            if p.len() == n {
                Ok(())
            } else {
                Err(DecodeError::BadLength {
                    msg_type: frame.msg_type,
                    got: p.len(),
                })
            }
        };
        match frame.msg_type {
            T_HELLO_ACK => {
                need(3)?;
                Ok(DeviceToHost::HelloAck {
                    fw_major: p[0],
                    fw_minor: p[1],
                    capabilities: p[2],
                })
            }
            T_PONG => {
                need(0)?;
                Ok(DeviceToHost::Pong)
            }
            T_VOLUME_CHANGE => {
                need(3)?;
                let slot = SessionSlot::from_u8(p[0]).ok_or(DecodeError::BadField("slot"))?;
                Ok(DeviceToHost::VolumeChange {
                    slot,
                    volume: VolumeData::unpack([p[1], p[2]]),
                })
            }
            T_BUTTON => {
                need(1)?;
                Ok(DeviceToHost::Button { event: p[0] })
            }
            T_SESSION_REQUEST => {
                need(1)?;
                Ok(DeviceToHost::SessionRequest { direction: p[0] })
            }
            other => Err(DecodeError::UnknownType(other)),
        }
    }
}

/// Parse a host->device message from a verified raw frame (used by firmware
/// tests and to assert host encodings round-trip).
pub fn decode_host_to_device(frame: &RawFrame) -> Result<HostToDevice, DecodeError> {
    let p = &frame.payload;
    let need = |n: usize| -> Result<(), DecodeError> {
        if p.len() == n {
            Ok(())
        } else {
            Err(DecodeError::BadLength {
                msg_type: frame.msg_type,
                got: p.len(),
            })
        }
    };
    match frame.msg_type {
        T_HELLO => {
            need(1)?;
            Ok(HostToDevice::Hello { proto_version: p[0] })
        }
        T_PING => {
            need(0)?;
            Ok(HostToDevice::Ping)
        }
        T_SESSION_INFO => {
            need(3)?;
            Ok(HostToDevice::SessionInfo(SessionInfo {
                mode: p[0],
                current: p[1],
                count: p[2],
            }))
        }
        T_SESSION_DATA => {
            need(1 + 2 + NAME_FIELD_BYTES)?;
            let slot = SessionSlot::from_u8(p[0]).ok_or(DecodeError::BadField("slot"))?;
            let volume = VolumeData::unpack([p[1], p[2]]);
            let name = decode_name_field(&p[3..3 + NAME_FIELD_BYTES]);
            Ok(HostToDevice::SessionData { slot, volume, name })
        }
        T_VOLUME_SET => {
            need(3)?;
            let slot = SessionSlot::from_u8(p[0]).ok_or(DecodeError::BadField("slot"))?;
            Ok(HostToDevice::VolumeSet {
                slot,
                volume: VolumeData::unpack([p[1], p[2]]),
            })
        }
        T_LIGHTING_CONFIG => {
            let n = usize::from(*p.get(2).ok_or(DecodeError::BadField("color_count"))?);
            if !(1..=MAX_PALETTE).contains(&n) {
                return Err(DecodeError::BadField("color_count"));
            }
            need(11 + 3 * n)?;
            let mode = LightingMode::from_u8(p[0]).ok_or(DecodeError::BadField("mode"))?;
            let rest = &p[3 + 3 * n..];
            let idle_mode =
                LightingMode::from_u8(rest[0]).ok_or(DecodeError::BadField("idle_mode"))?;
            let mut colors = [Rgb::new(0, 0, 0); MAX_PALETTE];
            for (i, c) in colors.iter_mut().take(n).enumerate() {
                let o = 3 + 3 * i;
                *c = Rgb::new(p[o], p[o + 1], p[o + 2]);
            }
            Ok(HostToDevice::Lighting(LightingConfig {
                mode,
                speed: p[1],
                colors,
                color_count: n as u8,
                idle_mode,
                idle_timeout_s: rest[1],
                volume_min: Rgb::new(rest[2], rest[3], rest[4]),
                volume_max: Rgb::new(rest[5], rest[6], rest[7]),
            }))
        }
        T_DEVICE_CONFIG => {
            need(4)?;
            Ok(HostToDevice::DeviceConfig {
                acceleration: p[0],
                sleep_after_s: p[1],
                continuous_scroll: p[2] & 0x01 != 0,
                ring_reversed: p[2] & 0x02 != 0,
                dim_after_s: p[3],
            })
        }
        other => Err(DecodeError::UnknownType(other)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn animated_lighting_modes_round_trip() {
        for mode in [LightingMode::Rainbow, LightingMode::Carousel] {
            let cfg = LightingConfig {
                mode,
                speed: 5,
                colors: LightingConfig::palette_from(&[Rgb::new(1, 2, 3), Rgb::new(4, 5, 6)]).0,
                color_count: 2,
                idle_mode: mode,
                idle_timeout_s: 10,
                volume_min: Rgb::new(7, 8, 9),
                volume_max: Rgb::new(10, 11, 12),
            };
            let msg = HostToDevice::Lighting(cfg);
            let frame = one_frame(&msg.encode());
            assert_eq!(decode_host_to_device(&frame).unwrap(), msg);
        }
        assert_eq!(LightingMode::from_u8(3), Some(LightingMode::Rainbow));
        assert_eq!(LightingMode::from_u8(4), Some(LightingMode::Carousel));
        assert_eq!(LightingMode::from_u8(5), None);
    }
    use crate::frame::FrameDecoder;

    /// Decode exactly one frame out of a byte buffer, for round-trip asserts.
    fn one_frame(bytes: &[u8]) -> RawFrame {
        let mut d = FrameDecoder::new();
        d.push(bytes);
        d.next_frame().expect("a frame")
    }

    // ---- VolumeData packing oracle (ported from the .NET codec spec) ----

    #[test]
    fn volumedata_basic_pack_matches_firmware_contract() {
        // Spotify-like: id=2, not default, 75%, not muted -> [0x02, 75].
        let v = VolumeData { id: 2, is_default: false, volume: 75, is_muted: false };
        assert_eq!(v.pack(), [0x02, 75]);
    }

    #[test]
    fn volumedata_high_bits_set() {
        // id=5 default, 100% muted -> byte0 = 5|0x80, byte1 = 100|0x80.
        let v = VolumeData { id: 5, is_default: true, volume: 100, is_muted: true };
        assert_eq!(v.pack(), [0x85, 0xE4]);
    }

    #[test]
    fn volumedata_round_trips() {
        for id in [0u8, 1, 63, 127] {
            for vol in [0u8, 50, 100, 127] {
                for d in [false, true] {
                    for m in [false, true] {
                        let v = VolumeData { id, is_default: d, volume: vol, is_muted: m };
                        assert_eq!(VolumeData::unpack(v.pack()), v);
                    }
                }
            }
        }
    }

    #[test]
    fn volumedata_id_masked_to_7_bits() {
        // id only occupies the low 7 bits; the high bit is the default flag.
        let v = VolumeData { id: 0x7F, is_default: false, volume: 1, is_muted: false };
        assert_eq!(v.pack()[0] & 0x80, 0, "default flag must stay clear");
    }

    // ---- Message round-trips ----

    #[test]
    fn session_data_round_trip_with_name() {
        let msg = HostToDevice::SessionData {
            slot: SessionSlot::Current,
            volume: VolumeData { id: 2, is_default: false, volume: 75, is_muted: false },
            name: "Spotify".into(),
        };
        let frame = one_frame(&msg.encode());
        assert_eq!(decode_host_to_device(&frame).unwrap(), msg);
    }

    #[test]
    fn session_data_payload_is_fixed_33_bytes() {
        let msg = HostToDevice::SessionData {
            slot: SessionSlot::Next,
            volume: VolumeData { id: 0, is_default: true, volume: 0, is_muted: false },
            name: "x".into(),
        };
        let frame = one_frame(&msg.encode());
        // slot(1) + volume(2) + name field(30) = 33.
        assert_eq!(frame.payload.len(), 33);
    }

    #[test]
    fn lighting_config_round_trip() {
        let msg = HostToDevice::Lighting(LightingConfig {
            mode: LightingMode::Pulse,
            speed: 7,
            colors: LightingConfig::palette_from(&[Rgb::new(0, 0, 32), Rgb::new(0, 128, 255)]).0,
            color_count: 2,
            idle_mode: LightingMode::Fade,
            idle_timeout_s: 30,
            volume_min: Rgb::new(0, 255, 64),
            volume_max: Rgb::new(255, 0, 0),
        });
        let frame = one_frame(&msg.encode());
        assert_eq!(decode_host_to_device(&frame).unwrap(), msg);
    }

    #[test]
    fn lighting_palette_sizes_round_trip() {
        for n in 1..=MAX_PALETTE {
            let colors: Vec<Rgb> = (0..n as u8).map(|i| Rgb::new(i, i * 2, i * 3)).collect();
            let (colors, color_count) = LightingConfig::palette_from(&colors);
            let msg = HostToDevice::Lighting(LightingConfig {
                mode: LightingMode::Carousel,
                speed: 3,
                colors,
                color_count,
                idle_mode: LightingMode::Rainbow,
                idle_timeout_s: 9,
                volume_min: Rgb::new(1, 1, 1),
                volume_max: Rgb::new(2, 2, 2),
            });
            let frame = one_frame(&msg.encode());
            assert_eq!(frame.payload.len(), 11 + 3 * n);
            assert_eq!(decode_host_to_device(&frame).unwrap(), msg);
        }
    }

    #[test]
    fn lighting_rejects_bad_palette_sizes() {
        for n in [0u8, 9] {
            let mut payload = vec![0, 5, n];
            payload.resize(11 + 3 * usize::from(n), 0);
            let bytes = crate::frame::encode_frame(T_LIGHTING_CONFIG, &payload);
            assert!(decode_host_to_device(&one_frame(&bytes)).is_err(), "n = {n}");
        }
    }

    #[test]
    fn palette_from_clamps_to_one_and_eight() {
        assert_eq!(LightingConfig::palette_from(&[]).1, 1);
        assert_eq!(LightingConfig::palette_from(&[Rgb::new(1, 1, 1); 12]).1, 8);
    }

    #[test]
    fn device_config_flags_pack_independently() {
        let msg = HostToDevice::DeviceConfig {
            acceleration: 3,
            sleep_after_s: 5,
            continuous_scroll: true,
            ring_reversed: true,
            dim_after_s: 45,
        };
        let frame = one_frame(&msg.encode());
        assert_eq!(decode_host_to_device(&frame).unwrap(), msg);
        // flags byte: bit0 scroll, bit1 ring -> 0b11 = 3.
        assert_eq!(frame.payload[2], 0b11);
        assert_eq!(frame.payload[3], 45, "dim_after_s is the 4th byte");
    }

    #[test]
    fn device_to_host_volume_change_round_trip() {
        let msg = DeviceToHost::VolumeChange {
            slot: SessionSlot::Current,
            volume: VolumeData { id: 9, is_default: false, volume: 42, is_muted: true },
        };
        let frame = one_frame(&msg.encode());
        assert_eq!(DeviceToHost::decode(&frame).unwrap(), msg);
    }

    #[test]
    fn hello_handshake_round_trip() {
        let hello = HostToDevice::Hello { proto_version: PROTO_VERSION };
        let frame = one_frame(&hello.encode());
        assert_eq!(decode_host_to_device(&frame).unwrap(), hello);

        let ack = DeviceToHost::HelloAck { fw_major: 2, fw_minor: 0, capabilities: 0x01 };
        let frame = one_frame(&ack.encode());
        assert_eq!(DeviceToHost::decode(&frame).unwrap(), ack);
    }

    #[test]
    fn wrong_length_is_rejected() {
        // A PONG frame must carry no payload; a padded one is rejected.
        let bogus = RawFrame { msg_type: 0x82, payload: vec![0] };
        assert!(matches!(
            DeviceToHost::decode(&bogus),
            Err(DecodeError::BadLength { .. })
        ));
    }

    #[test]
    fn unknown_type_is_rejected() {
        let bogus = RawFrame { msg_type: 0x77, payload: vec![] };
        assert!(matches!(
            DeviceToHost::decode(&bogus),
            Err(DecodeError::UnknownType(0x77))
        ));
    }
}
