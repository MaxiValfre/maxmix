//! MaxMix orchestration core.
//!
//! Pure, I/O-free logic that the daemon drives:
//! * [`settings`] — XDG JSON persistence with migration and corrupt recovery,
//! * [`slot`] — session→4-slot mapping and knob navigation,
//! * [`feedback`] — the per-session echo guard that breaks sync feedback loops,
//! * [`orchestrator`] — the event→action state machine tying it together.
//!
//! Everything here is unit-tested without hardware, audio, or serial I/O.

pub mod feedback;
pub mod orchestrator;
pub mod settings;
pub mod slot;

pub use feedback::{EchoGuard, ECHO_VOLUME_TOLERANCE};
pub use orchestrator::{Action, Event, Orchestrator};
pub use settings::{
    default_settings_path, load_or_init, save, AppSettings, Color, Lighting, SCHEMA_VERSION,
};
pub use slot::{SlotMapper, SlotView};
