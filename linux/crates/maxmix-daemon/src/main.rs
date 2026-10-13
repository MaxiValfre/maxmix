//! MaxMix Linux daemon (`maxmixd`).
//!
//! Wiring only — all real logic lives in the library crates:
//! * `maxmix-serial` runs the device transport on its own thread,
//! * `maxmix-audio`'s PulseBackend runs on a dedicated audio thread (it is !Send),
//! * `maxmix-core`'s pure `Orchestrator` runs on the main thread and dispatches
//!   the resulting actions to the transport and the audio thread.

mod audio_thread;
mod autostart;
mod settings_watch;
mod single_instance;
#[cfg(feature = "tray")]
mod tray;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::sync::Arc;
use std::time::Duration;

use maxmix_core::{default_settings_path, load_or_init, Action, Event, Orchestrator};
use maxmix_protocol::DeviceToHost;
use maxmix_serial::{hardware_link_factory, SerialConfig, Transport, TransportEvent};

use audio_thread::AudioCommand;

const RECONCILE_INTERVAL: Duration = Duration::from_secs(2);

fn main() {
    let _guard = init_logging();

    if std::env::args().any(|a| a == "--version" || a == "-V") {
        println!("maxmixd {}", env!("CARGO_PKG_VERSION"));
        return;
    }

    let _instance = match single_instance::acquire() {
        Some(lock) => lock,
        None => {
            tracing::error!("another MaxMix instance is already running");
            eprintln!("MaxMix is already running.");
            std::process::exit(1);
        }
    };

    let settings_path = default_settings_path();
    let settings = load_or_init(&settings_path);
    tracing::info!("loaded settings from {}", settings_path.display());

    // Reflect the autostart preference onto the XDG autostart entry.
    if let Ok(exe) = std::env::current_exe() {
        let exec = exe.to_string_lossy().to_string();
        if let Err(e) = autostart::set_autostart(settings.autostart, &exec) {
            tracing::warn!("could not update autostart entry: {e}");
        }
    }

    // ---- control flags -------------------------------------------------
    let stop = Arc::new(AtomicBool::new(false));
    let open_settings = Arc::new(AtomicBool::new(false));

    {
        let stop = Arc::clone(&stop);
        if let Err(e) = ctrlc::set_handler(move || stop.store(true, Ordering::SeqCst)) {
            tracing::warn!("could not install Ctrl-C handler: {e}");
        }
    }

    #[cfg(feature = "tray")]
    let tray = tray::spawn(tray::TrayControls {
        quit: Arc::clone(&stop),
        open_settings: Arc::clone(&open_settings),
    });

    // ---- audio thread --------------------------------------------------
    let (audio_evt_tx, audio_evt_rx) = mpsc::channel::<Event>();
    let (audio_cmd_tx, audio_cmd_rx) = mpsc::channel::<AudioCommand>();
    let audio_handle = audio_thread::spawn(
        audio_evt_tx,
        audio_cmd_rx,
        Arc::clone(&stop),
        RECONCILE_INTERVAL,
    );

    // ---- serial transport ---------------------------------------------
    let transport = Transport::spawn(SerialConfig::default(), hardware_link_factory());

    // ---- orchestrator loop (main thread) ------------------------------
    let mut orch = Orchestrator::new(settings);
    let mut settings_watch = settings_watch::FileWatch::new(&settings_path);
    let mut last_settings_check = std::time::Instant::now();

    tracing::info!("maxmixd started");
    while !stop.load(Ordering::SeqCst) {
        // Drain device/transport events.
        while let Ok(evt) = transport.events().try_recv() {
            match evt {
                TransportEvent::Connected(info) => {
                    tracing::info!("device connected: fw {}.{}", info.fw_major, info.fw_minor);
                    #[cfg(feature = "tray")]
                    tray.set_status("Connected");
                    dispatch(orch.on_connected(), &transport, &audio_cmd_tx);
                }
                TransportEvent::State(state) => {
                    tracing::debug!("serial state: {state:?}");
                    #[cfg(feature = "tray")]
                    tray.set_status(format!("{state:?}"));
                }
                TransportEvent::Message(msg) => {
                    if let Some(actions) = handle_device_message(&mut orch, msg) {
                        dispatch(actions, &transport, &audio_cmd_tx);
                    }
                }
                TransportEvent::Error(e) => tracing::debug!("serial error: {e}"),
            }
        }

        // Block briefly on audio events so the loop stays cheap when idle.
        match audio_evt_rx.recv_timeout(Duration::from_millis(50)) {
            Ok(event) => dispatch(orch.handle(event), &transport, &audio_cmd_tx),
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        }

        if open_settings.swap(false, Ordering::SeqCst) {
            if let Err(e) = settings_watch::launch_settings() {
                tracing::warn!("could not launch settings window: {e}");
            }
        }

        // Hot-reload settings written by the settings window (polled ~1/s).
        if last_settings_check.elapsed() >= Duration::from_secs(1) {
            last_settings_check = std::time::Instant::now();
            if settings_watch.changed() {
                let new_settings = load_or_init(&settings_path);
                tracing::info!("settings changed on disk; applying");
                if let Ok(exe) = std::env::current_exe() {
                    let exec = exe.to_string_lossy().to_string();
                    if let Err(e) = autostart::set_autostart(new_settings.autostart, &exec) {
                        tracing::warn!("could not update autostart entry: {e}");
                    }
                }
                dispatch(orch.apply_settings(new_settings), &transport, &audio_cmd_tx);
            }
        }
    }

    tracing::info!("shutting down");
    drop(transport); // joins the serial worker
    let _ = audio_handle.join();
}

/// Translate a device->host message into orchestrator actions.
fn handle_device_message(orch: &mut Orchestrator, msg: DeviceToHost) -> Option<Vec<Action>> {
    match msg {
        DeviceToHost::VolumeChange { slot, volume } => {
            Some(orch.handle(Event::DeviceVolume { slot, volume }))
        }
        DeviceToHost::SessionRequest { direction } => {
            Some(orch.handle(Event::DeviceNavigate { direction }))
        }
        // Buttons / handshake acks are handled elsewhere or ignored for now.
        _ => None,
    }
}

/// Execute orchestrator actions against the transport and audio thread.
fn dispatch(actions: Vec<Action>, transport: &Transport, audio_cmd_tx: &mpsc::Sender<AudioCommand>) {
    for action in actions {
        match action {
            Action::Send(frame) => transport.send(frame),
            Action::SetAudioVolume { key, volume } => {
                let _ = audio_cmd_tx.send(AudioCommand::SetVolume { key, volume });
            }
            Action::SetAudioMute { key, muted } => {
                let _ = audio_cmd_tx.send(AudioCommand::SetMute { key, muted });
            }
        }
    }
}

/// Initialise logging to stderr and a rolling daily file under the XDG state dir.
fn init_logging() -> Option<tracing_appender::non_blocking::WorkerGuard> {
    use tracing_subscriber::prelude::*;
    use tracing_subscriber::{fmt, EnvFilter};

    let filter = EnvFilter::try_from_env("MAXMIX_LOG").unwrap_or_else(|_| EnvFilter::new("info"));

    let state_dir = dirs::state_dir()
        .unwrap_or_else(std::env::temp_dir)
        .join("maxmix");
    let _ = std::fs::create_dir_all(&state_dir);

    let (file_layer, guard) = match std::fs::File::options()
        .create(true)
        .append(true)
        .open(state_dir.join("maxmixd.log"))
    {
        Ok(_) => {
            let appender = tracing_appender::rolling::daily(&state_dir, "maxmixd.log");
            let (nb, guard) = tracing_appender::non_blocking(appender);
            (Some(fmt::layer().with_ansi(false).with_writer(nb)), Some(guard))
        }
        Err(_) => (None, None),
    };

    tracing_subscriber::registry()
        .with(filter)
        .with(fmt::layer().with_writer(std::io::stderr))
        .with(file_layer)
        .init();

    guard
}
