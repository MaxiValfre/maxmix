//! PulseAudio backend (libpulse).
//!
//! Targets the PulseAudio client API on purpose: it works on pure PulseAudio
//! and on PipeWire via `pipewire-pulse`, giving the widest Linux coverage.
//! Per-application playback streams are PulseAudio *sink-inputs*; we key each
//! session by its application identity (`application.process.binary` /
//! `application.name`) so the stable id survives restarts.
//!
//! All calls drive a blocking mainloop iteration to completion, which is fine
//! for MaxMix's low-frequency control traffic (a human turning a knob).

use std::cell::RefCell;
use std::rc::Rc;

use libpulse_binding::callbacks::ListResult;
use libpulse_binding::context::introspect::SinkInputInfo;
use libpulse_binding::context::{Context, FlagSet as ContextFlagSet, State as ContextState};
use libpulse_binding::mainloop::standard::{IterateResult, Mainloop};
use libpulse_binding::volume::{ChannelVolumes, Volume};

use crate::backend::{AudioBackend, AudioError};
use crate::session::{AudioSession, SessionKind};

/// PulseAudio volume range maps 0..=100 (%) onto NORMAL..MUTED linear volume.
fn percent_to_volume(percent: u8) -> Volume {
    let p = f64::from(percent.min(100)) / 100.0;
    let raw = (f64::from(Volume::NORMAL.0) * p).round() as u32;
    Volume(raw)
}

fn volume_to_percent(v: &ChannelVolumes) -> u8 {
    let avg = v.avg().0;
    let pct = (f64::from(avg) / f64::from(Volume::NORMAL.0) * 100.0).round();
    pct.clamp(0.0, 100.0) as u8
}

/// Derive the stable identity key for a sink-input from its proplist.
fn session_key(info: &SinkInputInfo) -> String {
    let pl = &info.proplist;
    pl.get_str("application.process.binary")
        .or_else(|| pl.get_str("application.name"))
        .unwrap_or_else(|| {
            info.name
                .as_ref()
                .map_or_else(|| format!("sink-input-{}", info.index), |n| n.to_string())
        })
}

fn session_name(info: &SinkInputInfo) -> String {
    let pl = &info.proplist;
    pl.get_str("application.name")
        .or_else(|| info.name.as_ref().map(ToString::to_string))
        .unwrap_or_else(|| "Unknown".to_string())
}

/// A PulseAudio-backed audio session source/controller.
///
/// Field order matters: `context` is declared before `mainloop` so it is
/// dropped first, releasing its registered IO events before the mainloop is
/// freed. [`Drop`] additionally disconnects and pumps the loop, avoiding the
/// `mainloop_io_free` assertion libpulse raises on an abrupt teardown.
pub struct PulseBackend {
    context: Context,
    mainloop: Mainloop,
}

impl Drop for PulseBackend {
    fn drop(&mut self) {
        self.context.disconnect();
        for _ in 0..20 {
            match self.mainloop.iterate(false) {
                IterateResult::Success(_) => {}
                _ => break,
            }
        }
    }
}

impl PulseBackend {
    /// Connect to the default PulseAudio server and wait until ready.
    pub fn connect() -> Result<Self, AudioError> {
        let mut mainloop = Mainloop::new()
            .ok_or_else(|| AudioError::Unavailable("failed to create mainloop".into()))?;
        let mut context = Context::new(&mainloop, "MaxMix")
            .ok_or_else(|| AudioError::Unavailable("failed to create context".into()))?;

        context
            .connect(None, ContextFlagSet::NOFLAGS, None)
            .map_err(|e| AudioError::Unavailable(format!("connect: {e}")))?;

        // Pump the mainloop until the context is Ready or fails.
        loop {
            match mainloop.iterate(true) {
                IterateResult::Quit(_) | IterateResult::Err(_) => {
                    return Err(AudioError::Unavailable("mainloop stopped during connect".into()));
                }
                IterateResult::Success(_) => {}
            }
            match context.get_state() {
                ContextState::Ready => break,
                ContextState::Failed | ContextState::Terminated => {
                    return Err(AudioError::Unavailable("context failed".into()));
                }
                _ => {}
            }
        }

        Ok(Self { mainloop, context })
    }

    /// Run the mainloop until `done` flips true (an introspection op finished).
    fn pump_until(&mut self, done: &Rc<RefCell<bool>>) -> Result<(), AudioError> {
        while !*done.borrow() {
            match self.mainloop.iterate(true) {
                IterateResult::Quit(_) | IterateResult::Err(_) => {
                    return Err(AudioError::Backend("mainloop stopped".into()));
                }
                IterateResult::Success(_) => {}
            }
        }
        Ok(())
    }
}

impl AudioBackend for PulseBackend {
    fn list_sessions(&mut self) -> Result<Vec<AudioSession>, AudioError> {
        let collected: Rc<RefCell<Vec<AudioSession>>> = Rc::new(RefCell::new(Vec::new()));
        let done = Rc::new(RefCell::new(false));

        let collected_cb = Rc::clone(&collected);
        let done_cb = Rc::clone(&done);

        let op = self.context.introspect().get_sink_input_info_list(move |res| match res {
            ListResult::Item(info) => {
                collected_cb.borrow_mut().push(AudioSession::new(
                    session_key(info),
                    session_name(info),
                    SessionKind::Application,
                    volume_to_percent(&info.volume),
                    info.mute,
                    false,
                ));
            }
            ListResult::End | ListResult::Error => {
                *done_cb.borrow_mut() = true;
            }
        });

        self.pump_until(&done)?;
        drop(op);
        Ok(Rc::try_unwrap(collected).map_or_else(|rc| rc.borrow().clone(), RefCell::into_inner))
    }

    fn set_volume(&mut self, key: &str, volume: u8) -> Result<(), AudioError> {
        let index = self.find_index(key)?;
        let mut cv = ChannelVolumes::default();
        cv.set(2, percent_to_volume(volume)); // stereo; Pulse scales to real channels
        let done = Rc::new(RefCell::new(false));
        let done_cb = Rc::clone(&done);
        let op = self
            .context
            .introspect()
            .set_sink_input_volume(index, &cv, Some(Box::new(move |_success| {
                *done_cb.borrow_mut() = true;
            })));
        self.pump_until(&done)?;
        drop(op);
        Ok(())
    }

    fn set_mute(&mut self, key: &str, muted: bool) -> Result<(), AudioError> {
        let index = self.find_index(key)?;
        let done = Rc::new(RefCell::new(false));
        let done_cb = Rc::clone(&done);
        let op = self
            .context
            .introspect()
            .set_sink_input_mute(index, muted, Some(Box::new(move |_success| {
                *done_cb.borrow_mut() = true;
            })));
        self.pump_until(&done)?;
        drop(op);
        Ok(())
    }
}

impl PulseBackend {
    /// Resolve a session identity key to its current PulseAudio sink-input index.
    fn find_index(&mut self, key: &str) -> Result<u32, AudioError> {
        let found: Rc<RefCell<Option<u32>>> = Rc::new(RefCell::new(None));
        let done = Rc::new(RefCell::new(false));
        let found_cb = Rc::clone(&found);
        let done_cb = Rc::clone(&done);
        let want = key.to_string();

        let op = self.context.introspect().get_sink_input_info_list(move |res| match res {
            ListResult::Item(info) => {
                if session_key(info) == want {
                    *found_cb.borrow_mut() = Some(info.index);
                }
            }
            ListResult::End | ListResult::Error => {
                *done_cb.borrow_mut() = true;
            }
        });
        self.pump_until(&done)?;
        drop(op);

        let idx = *found.borrow();
        idx.ok_or_else(|| AudioError::NotFound(key.to_string()))
    }
}
