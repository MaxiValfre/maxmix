//! Audio worker thread.
//!
//! `PulseBackend` is `!Send` (it owns a libpulse mainloop), so it is created and
//! driven entirely inside this thread. The thread periodically reconciles the
//! session set, forwarding structural changes as `AudioSnapshot` and per-session
//! volume/mute deltas as `AudioVolume` to the orchestrator, and applies inbound
//! volume/mute commands to the backend.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{Receiver, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use maxmix_audio::{AudioBackend, PulseBackend, SessionChange, SessionRegistry};
use maxmix_core::Event;

/// Commands the orchestrator sends to the audio backend.
#[derive(Debug, Clone)]
pub enum AudioCommand {
    SetVolume { key: String, volume: u8 },
    SetMute { key: String, muted: bool },
}

/// Spawn the audio worker. Reconciles every `reconcile_interval`.
pub fn spawn(
    events: Sender<Event>,
    commands: Receiver<AudioCommand>,
    stop: Arc<AtomicBool>,
    reconcile_interval: Duration,
) -> JoinHandle<()> {
    thread::spawn(move || run(&events, &commands, &stop, reconcile_interval))
}

fn run(
    events: &Sender<Event>,
    commands: &Receiver<AudioCommand>,
    stop: &AtomicBool,
    reconcile_interval: Duration,
) {
    // Connect, retrying a few times so a slow audio server start is tolerated.
    let mut backend = loop {
        if stop.load(Ordering::SeqCst) {
            return;
        }
        match PulseBackend::connect() {
            Ok(b) => break b,
            Err(e) => {
                tracing::warn!("audio connect failed: {e}; retrying");
                thread::sleep(Duration::from_secs(2));
            }
        }
    };
    tracing::info!("audio backend connected");

    let mut registry = SessionRegistry::new();
    let mut last_reconcile = Instant::now() - reconcile_interval;

    while !stop.load(Ordering::SeqCst) {
        // Apply any queued commands promptly.
        while let Ok(cmd) = commands.try_recv() {
            let res = match &cmd {
                AudioCommand::SetVolume { key, volume } => backend.set_volume(key, *volume),
                AudioCommand::SetMute { key, muted } => backend.set_mute(key, *muted),
            };
            if let Err(e) = res {
                tracing::debug!("audio command {cmd:?} failed: {e}");
            }
        }

        // Periodic reconciliation.
        if last_reconcile.elapsed() >= reconcile_interval {
            last_reconcile = Instant::now();
            match backend.list_sessions() {
                Ok(sessions) => {
                    let changes = registry.reconcile(sessions.clone());
                    let structural = changes.iter().any(|c| {
                        matches!(
                            c,
                            SessionChange::Added(_)
                                | SessionChange::Removed { .. }
                                | SessionChange::Renamed { .. }
                                | SessionChange::DefaultChanged { .. }
                        )
                    });
                    if structural {
                        let _ = events.send(Event::AudioSnapshot(sessions));
                    }
                    for change in changes {
                        if let SessionChange::VolumeChanged { key, volume, muted, .. } = change {
                            let _ = events.send(Event::AudioVolume { key, volume, muted });
                        }
                    }
                }
                Err(e) => tracing::warn!("list_sessions failed: {e}"),
            }
        }

        thread::sleep(Duration::from_millis(50));
    }
    tracing::info!("audio thread stopping");
}
