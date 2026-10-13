//! MaxMix serial transport layer.
//!
//! Pure, hardware-free pieces ([`Backoff`], [`Handshaker`], port selection) are
//! unit-tested; [`Transport`] wires them onto a worker thread with reconnect
//! and heartbeat. Hardware access goes through the [`SerialLink`] trait so tests
//! use an in-memory reactive fake.

pub mod backoff;
pub mod handshake;
pub mod link;
pub mod ports;
pub mod transport;

use std::io::{self, Read, Write};
use std::time::Duration;

pub use backoff::Backoff;
pub use handshake::{perform_handshake, DeviceInfo, Handshaker};
pub use link::SerialLink;
pub use ports::{best_port, scan_ports, PortCandidate};
pub use transport::{ConnectionState, LinkFactory, SerialConfig, Transport, TransportEvent};

/// Fixed baud for the MaxMix firmware (chosen to be clone-friendly on CH340).
pub const BAUD: u32 = 76_800;

/// A real serial link backed by the `serialport` crate.
pub struct RealLink {
    port: Box<dyn serialport::SerialPort>,
}

impl RealLink {
    /// Open `path` at the MaxMix baud with a short read timeout so the ready
    /// loop stays responsive.
    pub fn open(path: &str) -> io::Result<Self> {
        let port = serialport::new(path, BAUD)
            .timeout(Duration::from_millis(50))
            .open()
            .map_err(|e| io::Error::new(io::ErrorKind::Other, e))?;
        Ok(Self { port })
    }
}

impl SerialLink for RealLink {
    fn write_all(&mut self, data: &[u8]) -> io::Result<()> {
        Write::write_all(&mut self.port, data)?;
        self.port.flush()
    }

    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        match Read::read(&mut self.port, buf) {
            Ok(n) => Ok(n),
            // A read timeout just means "no bytes yet", not a failure.
            Err(e) if e.kind() == io::ErrorKind::TimedOut => Ok(0),
            Err(e) => Err(e),
        }
    }
}

/// Default hardware link factory: scan ports each attempt, prefer CH340, open it.
#[must_use]
pub fn hardware_link_factory() -> LinkFactory {
    Box::new(|| {
        let candidates = scan_ports();
        let path = best_port(&candidates)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "no serial port found"))?;
        let link = RealLink::open(&path)?;
        Ok(Box::new(link) as Box<dyn SerialLink>)
    })
}
