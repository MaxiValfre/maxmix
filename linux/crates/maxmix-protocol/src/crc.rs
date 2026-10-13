//! CRC-8 (Dallas/Maxim): polynomial 0x31 (reflected 0x8C), init 0x00.
//!
//! Cheap to compute on an ATmega328p and strong enough for short serial frames.
//! The reflected bit-at-a-time form matches the classic 1-Wire implementation,
//! so the C firmware can use the exact same routine.

/// Compute the CRC-8/MAXIM checksum over `data`.
#[must_use]
pub fn crc8(data: &[u8]) -> u8 {
    let mut crc: u8 = 0x00;
    for &byte in data {
        crc ^= byte;
        for _ in 0..8 {
            if crc & 0x01 != 0 {
                crc = (crc >> 1) ^ 0x8C;
            } else {
                crc >>= 1;
            }
        }
    }
    crc
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_is_zero() {
        assert_eq!(crc8(&[]), 0x00);
    }

    #[test]
    fn known_maxim_vector() {
        // CRC-8/MAXIM of the ASCII string "123456789" is 0xA1 (reference vector).
        assert_eq!(crc8(b"123456789"), 0xA1);
    }

    #[test]
    fn single_bit_flip_changes_crc() {
        let a = crc8(&[0x10, 0x20, 0x30]);
        let b = crc8(&[0x10, 0x20, 0x31]);
        assert_ne!(a, b);
    }

    #[test]
    fn order_matters() {
        assert_ne!(crc8(&[0x01, 0x02]), crc8(&[0x02, 0x01]));
    }
}
