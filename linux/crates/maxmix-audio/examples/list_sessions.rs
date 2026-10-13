//! Smoke test: connect to the local PulseAudio/PipeWire server and print the
//! current per-application playback sessions.
//!
//! Run with: `cargo run -p maxmix-audio --features pulse --example list_sessions`

use maxmix_audio::{AudioBackend, PulseBackend};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut backend = PulseBackend::connect()?;
    let sessions = backend.list_sessions()?;
    println!("connected OK — {} playback session(s):", sessions.len());
    for s in &sessions {
        println!(
            "  id={:3}  vol={:3}%  mute={}  key={:<24}  name={}",
            s.id, s.volume, s.muted, s.key, s.name
        );
    }
    Ok(())
}
