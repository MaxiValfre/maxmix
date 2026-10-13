//! Threaded serial transport: connect → handshake → ready loop, with
//! heartbeat monitoring and exponential-backoff reconnection.

use std::io;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{Receiver, Sender, TryRecvError};
use std::sync::{mpsc, Arc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use maxmix_protocol::{DeviceToHost, FrameDecoder, HostToDevice};

use crate::backoff::Backoff;
use crate::handshake::{perform_handshake, DeviceInfo};
use crate::link::SerialLink;

/// Connection lifecycle state surfaced to the UI.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConnectionState {
    Disconnected,
    Scanning,
    Connecting,
    Handshaking,
    Ready,
    Backoff,
}

/// Events emitted by the transport worker.
#[derive(Debug, Clone)]
pub enum TransportEvent {
    State(ConnectionState),
    Connected(DeviceInfo),
    Message(DeviceToHost),
    Error(String),
}

/// Tunables for the connection lifecycle.
#[derive(Debug, Clone)]
pub struct SerialConfig {
    /// CH340 asserts DTR on open and resets the MCU; wait for the bootloader.
    pub post_open_delay: Duration,
    pub handshake_timeout: Duration,
    pub heartbeat_interval: Duration,
    pub heartbeat_timeout: Duration,
}

impl Default for SerialConfig {
    fn default() -> Self {
        Self {
            post_open_delay: Duration::from_millis(1800),
            handshake_timeout: Duration::from_millis(1500),
            heartbeat_interval: Duration::from_secs(1),
            heartbeat_timeout: Duration::from_secs(3),
        }
    }
}

/// Factory that (re)opens a link on each connection attempt.
pub type LinkFactory = Box<dyn FnMut() -> io::Result<Box<dyn SerialLink>> + Send>;

/// Handle to the running transport worker.
pub struct Transport {
    outgoing: Sender<HostToDevice>,
    events: Receiver<TransportEvent>,
    stop: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl Transport {
    /// Spawn the worker thread with a custom link factory (hardware or fake).
    #[must_use]
    pub fn spawn(config: SerialConfig, factory: LinkFactory) -> Self {
        let (tx_out, rx_out) = mpsc::channel::<HostToDevice>();
        let (tx_evt, rx_evt) = mpsc::channel::<TransportEvent>();
        let stop = Arc::new(AtomicBool::new(false));
        let stop_worker = Arc::clone(&stop);

        let handle = thread::spawn(move || {
            worker_loop(config, factory, rx_out, &tx_evt, &stop_worker);
        });

        Self { outgoing: tx_out, events: rx_evt, stop, handle: Some(handle) }
    }

    /// Queue a message to send to the device (dropped if not currently Ready).
    pub fn send(&self, msg: HostToDevice) {
        let _ = self.outgoing.send(msg);
    }

    /// Non-blocking event receiver.
    #[must_use]
    pub fn events(&self) -> &Receiver<TransportEvent> {
        &self.events
    }
}

impl Drop for Transport {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

fn emit(tx: &Sender<TransportEvent>, e: TransportEvent) {
    let _ = tx.send(e);
}

fn worker_loop(
    config: SerialConfig,
    mut factory: LinkFactory,
    rx_out: Receiver<HostToDevice>,
    tx_evt: &Sender<TransportEvent>,
    stop: &AtomicBool,
) {
    let mut backoff = Backoff::default();

    while !stop.load(Ordering::SeqCst) {
        emit(tx_evt, TransportEvent::State(ConnectionState::Scanning));

        let mut link = match factory() {
            Ok(l) => l,
            Err(e) => {
                emit(tx_evt, TransportEvent::Error(format!("open failed: {e}")));
                sleep_backoff(&mut backoff, stop, tx_evt);
                continue;
            }
        };

        emit(tx_evt, TransportEvent::State(ConnectionState::Connecting));
        interruptible_sleep(config.post_open_delay, stop);

        emit(tx_evt, TransportEvent::State(ConnectionState::Handshaking));
        let info = match perform_handshake(&mut *link, config.handshake_timeout) {
            Ok(info) => info,
            Err(e) => {
                emit(tx_evt, TransportEvent::Error(format!("handshake failed: {e}")));
                sleep_backoff(&mut backoff, stop, tx_evt);
                continue;
            }
        };

        backoff.reset();
        emit(tx_evt, TransportEvent::Connected(info));
        emit(tx_evt, TransportEvent::State(ConnectionState::Ready));

        // Ready loop: pump outgoing frames, decode incoming, run heartbeat.
        if ready_loop(&config, &mut *link, &rx_out, tx_evt, stop).is_err() {
            emit(tx_evt, TransportEvent::State(ConnectionState::Disconnected));
            sleep_backoff(&mut backoff, stop, tx_evt);
        }
    }
}

fn ready_loop(
    config: &SerialConfig,
    link: &mut dyn SerialLink,
    rx_out: &Receiver<HostToDevice>,
    tx_evt: &Sender<TransportEvent>,
    stop: &AtomicBool,
) -> io::Result<()> {
    let mut decoder = FrameDecoder::new();
    let mut buf = [0u8; 128];
    let mut last_ping = Instant::now();
    let mut last_rx = Instant::now();

    while !stop.load(Ordering::SeqCst) {
        // 1. Flush any queued outgoing messages.
        loop {
            match rx_out.try_recv() {
                Ok(msg) => link.write_all(&msg.encode())?,
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => return Ok(()),
            }
        }

        // 2. Read + decode incoming.
        let n = link.read(&mut buf)?;
        if n > 0 {
            last_rx = Instant::now();
            decoder.push(&buf[..n]);
            while let Some(frame) = decoder.next_frame() {
                if let Ok(msg) = DeviceToHost::decode(&frame) {
                    // PONG just refreshes liveness; still surface everything else.
                    if !matches!(msg, DeviceToHost::Pong) {
                        emit(tx_evt, TransportEvent::Message(msg));
                    }
                }
            }
        } else {
            thread::sleep(Duration::from_millis(5));
        }

        // 3. Heartbeat.
        if last_ping.elapsed() >= config.heartbeat_interval {
            link.write_all(&HostToDevice::Ping.encode())?;
            last_ping = Instant::now();
        }
        if last_rx.elapsed() >= config.heartbeat_timeout {
            return Err(io::Error::new(io::ErrorKind::TimedOut, "heartbeat lost"));
        }
    }
    Ok(())
}

fn sleep_backoff(backoff: &mut Backoff, stop: &AtomicBool, tx_evt: &Sender<TransportEvent>) {
    emit(tx_evt, TransportEvent::State(ConnectionState::Backoff));
    interruptible_sleep(backoff.next_delay(), stop);
}

fn interruptible_sleep(total: Duration, stop: &AtomicBool) {
    let step = Duration::from_millis(50);
    let mut slept = Duration::ZERO;
    while slept < total && !stop.load(Ordering::SeqCst) {
        let remaining = total - slept;
        let this = remaining.min(step);
        thread::sleep(this);
        slept += this;
    }
}
