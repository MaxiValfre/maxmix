//! Serial port discovery with CH340 preference (the chip on the MaxMix kit).

use serialport::{SerialPortType, UsbPortInfo};

/// CH340 / CH341 USB-serial vendor id (WCH).
const CH340_VID: u16 = 0x1A86;

/// A candidate serial port and whether it looks like a CH340 adapter.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PortCandidate {
    pub path: String,
    pub is_ch340: bool,
}

fn is_ch340(usb: &UsbPortInfo) -> bool {
    usb.vid == CH340_VID
}

/// Enumerate serial ports, flagging CH340 adapters.
#[must_use]
pub fn scan_ports() -> Vec<PortCandidate> {
    let ports = serialport::available_ports().unwrap_or_default();
    ports
        .into_iter()
        .map(|p| match p.port_type {
            SerialPortType::UsbPort(usb) => PortCandidate {
                path: p.port_name,
                is_ch340: is_ch340(&usb),
            },
            // Keep raw tty devices too (some adapters don't surface USB metadata).
            SerialPortType::Unknown | SerialPortType::PciPort | SerialPortType::BluetoothPort => {
                PortCandidate { path: p.port_name, is_ch340: false }
            }
        })
        .collect()
}

/// Pick the best candidate: a CH340 adapter if present, else the first port.
#[must_use]
pub fn best_port(candidates: &[PortCandidate]) -> Option<String> {
    candidates
        .iter()
        .find(|c| c.is_ch340)
        .or_else(|| candidates.first())
        .map(|c| c.path.clone())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prefers_ch340_over_other_ports() {
        let cands = vec![
            PortCandidate { path: "/dev/ttyACM0".into(), is_ch340: false },
            PortCandidate { path: "/dev/ttyUSB0".into(), is_ch340: true },
        ];
        assert_eq!(best_port(&cands).as_deref(), Some("/dev/ttyUSB0"));
    }

    #[test]
    fn falls_back_to_first_when_no_ch340() {
        let cands = vec![
            PortCandidate { path: "/dev/ttyACM0".into(), is_ch340: false },
            PortCandidate { path: "/dev/ttyS0".into(), is_ch340: false },
        ];
        assert_eq!(best_port(&cands).as_deref(), Some("/dev/ttyACM0"));
    }

    #[test]
    fn none_when_empty() {
        assert_eq!(best_port(&[]), None);
    }
}
