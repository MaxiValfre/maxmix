# MaxMix Linux Rewrite (Rust host + new Arduino firmware)

Cohesive from-scratch rewrite: low-footprint Rust host for Linux (PipeWire/PulseAudio)
plus a new ATmega328p firmware that owns RGB/animation autonomously. New framed+CRC
serial protocol (host and firmware versioned together). Feature parity with the
Windows/.NET app. The existing .NET codec (src/) is reference-only (protocol oracle).

## Specs

- **S1** Runtime: Rust. Always-on daemon, low idle RSS. Target Linux (CachyOS/Arch first).
- **S2** Audio: `libpulse-binding` (PulseAudio API) so it runs on PipeWire via pipewire-pulse
  and on pure PulseAudio. Per-app sessions = sink-inputs; input = source-outputs; devices = sinks/sources.
- **S3** Dynamic session enumeration: detect apps that start/stop after launch (event-driven
  via pa subscribe) + periodic reconciliation. Stable identity by executable/app-id hash → 0..127 id.
- **S4** Bidirectional volume/mute sync with feedback-loop guard (per-session expected-echo), tolerance ±2 (float→byte round-trip).
- **S5** New serial protocol v2: framed `SOF | LEN | TYPE | PAYLOAD | CRC8`, resync on CRC fail.
  No raw-length desync class (fixes historical Bug #1 structurally).
- **S6** RGB offloaded to firmware: host sends `LIGHTING_CONFIG` once (mode, colors, speed, idle behavior);
  firmware runs the animation locally. Host no longer streams interpolated frames.
- **S7** Firmware (ATmega328p, 2KB SRAM / 32KB flash): OLED render, encoder accel/debounce, LED
  animations (static/pulse/fade + idle), sleep that keeps RGB per config, configurable ring orientation.
- **S8** Connection lifecycle: handshake (HELLO/HELLO_ACK + fw version + capabilities), heartbeat
  (PING/PONG ~1s), reconnect exponential backoff 500ms→30s, COM/tty rescan.
- **S9** Settings: JSON at `~/.config/maxmix/settings.json` (XDG), schema-versioned with migration,
  corrupt-file backup+defaults. Logs under `~/.local/state/maxmix/` via `tracing`.
- **S10** UX: headless daemon + system tray (`ksni`) + on-demand config window (`slint`). Single instance
  (unix socket/lockfile). Autostart via `~/.config/autostart/maxmix.desktop`.
- **S11** Correctness oracle: port protocol test vectors from the .NET codec — VolumeData bit-packing,
  null-terminated ASCII names (≤29 + null), payload sizes, and the 5 original-bug regressions.
- **S12** Packaging: single static-ish binary; AUR PKGBUILD and/or AppImage. Serial access documented (`uucp` group on Arch).

## Tasks

| ID | Spec | Route | Commit | Status |
|----|------|-------|--------|--------|
| T1 | S5,S11 | `maxmix-protocol` crate: frames, CRC8, messages, codec + ported test vectors | 5fe56f7 | done (29 tests) |
| T2 | S8 | `maxmix-serial` crate: serialport transport, state machine, reconnect/heartbeat (fakeable) | (this) | done (8 tests) |
| T3 | S2,S3 | `maxmix-audio` crate: libpulse session manager, stable identity, reconciliation | (this) | done (14 tests + live smoke) |
| T4 | S3,S4,S6 | `maxmix-core` crate: orchestration, slot mapping, feedback guard, settings | (this) | done (23 tests) |
| T5 | S1,S10 | `maxmix-daemon` binary: wiring, tray, single-instance, autostart, logging | (this) | done (2 tests + live smoke) |
| T6 | S10 | `maxmix-ui` (slint) on-demand config window | (this) | done (4 ui + 3 core + 2 watch tests, live hot-reload) |
| T7 | S7,S6 | New firmware for ATmega328p: protocol v2, OLED, encoder, autonomous RGB | (this) | core done (42 C checks vs Rust vectors); sketch AVR compile + flash PENDING (no avr toolchain) |
| T8 | S12 | Packaging (AUR/AppImage) + docs (README Linux, serial group setup) | (this) | done: PKGBUILD + udev + desktop + docs; AppImage dropped; makepkg end-to-end not run (branch not pushed) |

## Log

- **L1** (user, original request): "me gustaria portearlo a linux o rehacer el soft a linux" → evolved to a
  cohesive clean rewrite: "si ya rehacemos el firmware mejor y con esas features, seria tambien genial
  rehacer el soft para que quede fino y de bajo consumo real, con las mismas features." Runtime decision:
  user chose a spike then withdrew ("no quiero desarrollar los dos"); accepted architect call = **Rust**.
  Hardware = **Arduino Nano ATmega328p** (current). Protocol = **framed + CRC** (firmware+host versioned together).
  Directive: "Hace todo sin review nomas y avanza derechito" (proceed without native review, advance continuously).
- **L2** Environment (this machine, CachyOS): PipeWire 1.6.9 + pipewire-pulse; `/dev/ttyUSB0` present (root:uucp,
  user `max` not in `uucp`); Rust 1.99 installed; .NET NOT installed (kept as read-only reference);
  libpulse dev + clang/gcc present.
- **L3** Evidence: 83 Rust tests + 42 firmware C checks (vs Rust-generated vectors), clippy clean. Live: PulseBackend
  enumerated a real Chrome sink-input; maxmixd idle RSS ~11.7 MB, 1.4 MB binary, 6 threads; settings hot-reload applied;
  serial open fails EACCES (user not in uucp) → backoff as designed. Open: AVR compile + flash + hardware E2E (T7).
- **L4** Hardware: Nano has the NEW bootloader (115200). Stock 1.5.6 flash backed up to firmware/backup/ (gitignored).
  v2 firmware: 18794 B flash (61%), 876 B RAM (42%). Flashed OK; maxmixd handshake → `device connected: fw 2.0` → Ready.
  Pending: physical E2E (OLED, ring, knob volume/mute, navigation) and Config.h pixel/OLED values.
