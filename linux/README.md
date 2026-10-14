# MaxMix for Linux

A small Rust daemon (`maxmixd`) that links the MaxMix hardware mixer to
per-application audio on Linux, plus a settings window (`maxmix-settings`)
that only runs while you have it open.

> Requires the **v2 firmware** in [`../firmware`](../firmware). The v2 protocol
> adds framing and a CRC and is **not** compatible with the stock 1.5.6 firmware.

## How it works

| Part | Responsibility |
| --- | --- |
| Firmware (Nano) | OLED, encoder, LED ring animations and palette, volume ring, idle lighting, screen dim/sleep |
| `maxmixd` | Audio sessions (PulseAudio API), session→slot mapping, volume/mute sync, tray |
| `maxmix-settings` | Edits `~/.config/maxmix/settings.json` with a live ring preview. The daemon reloads it automatically |

The lighting runs on the device. The host sends one `LightingConfig` message
and never streams colour frames.

Audio goes through the PulseAudio client API, so it works on PipeWire
(through `pipewire-pulse`) and on plain PulseAudio.

## Measured footprint

Measured on CachyOS with PipeWire, device connected:

| Process | RSS | CPU |
| --- | --- | --- |
| `maxmixd` (always running) | ~11.7 MB | ~0.2 % of one core |
| `maxmix-settings`, focused (ring preview at 30 fps) | ~22 MB | ~1.4 % |
| `maxmix-settings`, in the background | ~22 MB | 0 % (preview paused) |

The settings window is a separate process: closing it frees everything.

## Build

Requirements: Rust (stable), `libpulse`, `systemd-libs` (libudev) and `dbus`.

```bash
cd linux
cargo build --release -p maxmix-daemon -p maxmix-ui
cargo test --workspace
```

The binaries end up in `target/release/maxmixd` and `target/release/maxmix-settings`.

## Install (Arch / CachyOS)

Prebuilt package: see the [release](https://github.com/MaxiValfre/maxmix/releases/tag/linux-v1.0.0)
and the [main README](../README.md#1-install). To build the package yourself:

```bash
cd linux/packaging
makepkg -si
```

The package installs both binaries, the desktop entries, the icon and a udev
rule. The PKGBUILD builds the `linux-v$pkgver` tag with `--locked`.

## Install (other distributions)

Build as above, then copy the files the PKGBUILD installs:

```bash
sudo install -Dm755 target/release/maxmixd target/release/maxmix-settings -t /usr/local/bin
sudo install -Dm644 packaging/60-maxmix.rules /usr/lib/udev/rules.d/60-maxmix.rules
sudo install -Dm644 packaging/maxmix.desktop packaging/maxmix-settings.desktop -t /usr/share/applications
sudo install -Dm644 packaging/icons/maxmix.svg /usr/share/icons/hicolor/scalable/apps/maxmix.svg
sudo udevadm control --reload && sudo udevadm trigger
```

Build dependencies on Debian/Ubuntu: `libpulse-dev libudev-dev libdbus-1-dev
libxkbcommon-dev libfontconfig-dev pkg-config`.

## Serial port access

The Nano's CH340 adapter shows up as `/dev/ttyUSB*`. On Arch this device
belongs to the `uucp` group (`dialout` on Debian/Ubuntu). Use one of these:

- **Recommended:** install the udev rule. It grants access to the user logged
  into the desktop and creates `/dev/maxmix`:
  ```bash
  sudo install -Dm644 packaging/60-maxmix.rules /usr/lib/udev/rules.d/60-maxmix.rules
  sudo udevadm control --reload && sudo udevadm trigger
  ```
- Alternatively, add yourself to the group and log out and back in:
  `sudo usermod -aG uucp $USER`.

Without access, the daemon logs `open failed: Permission denied` and keeps
retrying with backoff.

## Run

```bash
maxmixd                  # tray icon appears; Settings… opens the settings window
MAXMIX_LOG=debug maxmixd # verbose logging
```

| Item | Location |
| --- | --- |
| Settings | `~/.config/maxmix/settings.json` |
| Logs | `~/.local/state/maxmix/` |
| Autostart | toggled in Settings; writes `~/.config/autostart/maxmix.desktop` |

Only one instance runs at a time (lock file in `$XDG_RUNTIME_DIR`).

## Workspace layout

| Crate | Purpose |
| --- | --- |
| `maxmix-protocol` | v2 codec: frames, CRC-8/MAXIM, messages; generates the firmware's C test vectors |
| `maxmix-serial` | Port scan (prefers CH340), handshake, heartbeat, reconnect with backoff |
| `maxmix-audio` | Stable session ids, reconciliation, `PulseBackend` |
| `maxmix-core` | Pure orchestration: slot mapping, echo guard, settings |
| `maxmix-daemon` | `maxmixd` wiring, tray, autostart, logging |
| `maxmix-ui` | `maxmix-settings` (Slint) and `preview.rs`, a Rust port of the firmware lighting for the live ring preview |

Live check that audio enumeration works:
`cargo run -p maxmix-audio --example list_sessions`.
