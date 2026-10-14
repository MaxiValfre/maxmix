# MaxMix

Companion software for the [MaxMix](https://github.com/t3knomanzer/maxmix-software)
hardware audio mixer: a knob, a small OLED screen and an LED ring that control
the volume of each application on your PC.

This repository has two editions:

| Edition | Status | Firmware on the device | Download |
| --- | --- | --- | --- |
| **Linux** (Rust) | `linux-v1.0.0` | **v2 firmware** from this repo | [Release](https://github.com/MaxiValfre/maxmix/releases/tag/linux-v1.0.0) |
| **Windows** (.NET) | `v0.1.0` | Stock 1.5.x firmware from the kit | [Release](https://github.com/MaxiValfre/maxmix/releases/tag/v0.1.0) · [docs](docs/windows.md) |

> [!IMPORTANT]
> The two editions speak **different protocols**. The Linux edition needs the
> v2 firmware flashed to the MaxMix, and with the v2 firmware the Windows
> edition (and the original MaxMix app) stops working. You can always flash the
> stock firmware back; see [Flash the firmware](#2-flash-the-v2-firmware).

---

## MaxMix for Linux

A tiny background daemon links the MaxMix to per-application audio through the
PulseAudio API, so it works on **PipeWire** (via `pipewire-pulse`) and on plain
PulseAudio. The lighting runs on the device itself; the PC only sends the
configuration once.

### Features

**On the device**
- OLED screens modelled on the original firmware: big app name (scrolls when
  long), selection arrows, volume bar and one dot per app.
- Lighting modes: **Static, Pulse, Fade, Rainbow and Carousel**, driven by a
  palette of **1 to 8 colours**, plus a separate idle mode.
- **Volume ring**: while you change the volume the ring fills from a *low*
  colour to a *high* colour with a smooth animation. It stays on while editing,
  and breathes dimly while the app is muted.
- Gestures: turn, tap, **double tap to mute**, long press to mute.
- Encoder acceleration ported from the original firmware.
- OLED **burn-in protection**: the screen dims after a configurable idle time.

**On the PC**
- Every app that plays audio shows up automatically, including ones started
  later. Voice streams from Discord and other Electron apps show their real
  name instead of "WEBRTC VoiceEngine".
- Volume and mute stay in sync both ways (knob ↔ desktop mixer).
- Tray icon with the connection status.
- Settings window with a **live preview of the LED ring**. It only animates
  while focused.
- Light: the daemon uses about **12 MB of RAM and 0.2 % CPU**.

### 1. Install

**Arch Linux, CachyOS, Manjaro and other Arch-based distros**

Download `maxmix-1.0.0-1-x86_64.pkg.tar.zst` from the
[release](https://github.com/MaxiValfre/maxmix/releases/tag/linux-v1.0.0) and run:

```bash
sudo pacman -U maxmix-1.0.0-1-x86_64.pkg.tar.zst
```

It installs `maxmixd`, `maxmix-settings`, the menu entries, the icon and a udev
rule that gives your desktop user access to the device (as `/dev/maxmix`).

Or build the package yourself:

```bash
git clone https://github.com/MaxiValfre/maxmix.git
cd maxmix/linux/packaging
makepkg -si
```

**Other distributions** — build from source; see [`linux/README.md`](linux/README.md).

### 2. Flash the v2 firmware

The release includes a precompiled `maxmix-firmware-v2.hex` built for the
original kit (Arduino Nano, 8-LED ring, 128×32 SSD1306 OLED at `0x3C`). Different
hardware? Edit [`firmware/MaxMix/Config.h`](firmware/MaxMix/Config.h) and build
it yourself (see [`firmware/README.md`](firmware/README.md)).

You need `avrdude` (`sudo pacman -S avrdude` on Arch). Quit `maxmixd` first so the
serial port is free.

```bash
# Optional but recommended: back up the stock firmware first.
avrdude -p m328p -c arduino -P /dev/ttyUSB0 -b 115200 -U flash:r:maxmix-stock.hex:i

# Flash the v2 firmware.
avrdude -p m328p -c arduino -P /dev/ttyUSB0 -b 115200 -D -U flash:w:maxmix-firmware-v2.hex:i
```

If avrdude cannot sync (`not in sync` / `programmer is not responding`), your
Nano has the **old bootloader**: use `-b 57600` instead of `-b 115200`.

To go back to the stock firmware, flash your backup the same way:
`-U flash:w:maxmix-stock.hex:i`.

### 3. Use it

Start **MaxMix** from the application menu (or run `maxmixd`). The tray icon
turns coral when the device is connected. Open **MaxMix Settings** from the
tray menu or the application menu.

| On the knob | Action |
| --- | --- |
| Turn | Switch between apps, or change the volume in edit mode |
| Tap | Toggle between *navigate* and *edit volume* |
| Double tap | Mute / unmute the current app |
| Long press | Mute / unmute the current app |

The settings window has three tabs:

- **Luces / Lighting** — mode, speed, the colour palette (up to 8 colours),
  idle mode and idle timeout.
- **Volumen / Volume** — the low and high colours of the volume ring.
- **Dispositivo / Device** — knob acceleration, screen dim and off timeouts,
  continuous scrolling between apps, ring direction and start at login.

Changes apply to the device as soon as you press **Save**.

### Troubleshooting

| Symptom | Fix |
| --- | --- |
| The screen says **Waiting for host** | `maxmixd` is not running or cannot open the port. Start it and check the tray icon. |
| `open failed: Permission denied` in the log | The udev rule is missing. Reinstall the package, or add yourself to `uucp` (Arch) / `dialout` (Debian) and log in again. |
| An app is missing | Linux only lists apps while they have an audio stream open. Play something in it. |
| Debug logs | `MAXMIX_LOG=debug maxmixd`; log files live in `~/.local/state/maxmix/`. |

Settings are stored in `~/.config/maxmix/settings.json`.

---

## MaxMix for Windows

The Windows edition (`v0.1.0`) is a .NET 8 rewrite of the original desktop app.
It keeps the **stock 1.5.x firmware** and its protocol, and fixes five bugs of
the original app. Download `MaxMixSetup.exe` from the
[v0.1.0 release](https://github.com/MaxiValfre/maxmix/releases/tag/v0.1.0). Build
instructions, configuration and architecture are in
[`docs/windows.md`](docs/windows.md).

## Repository layout

| Path | Contents |
| --- | --- |
| [`linux/`](linux/README.md) | Rust workspace: daemon, settings window, protocol, packaging |
| [`firmware/`](firmware/README.md) | v2 firmware for the Arduino Nano, with host tests |
| `src/`, `tests/`, `installer/` | Windows edition ([docs](docs/windows.md)) |

## Credits and license

MaxMix hardware, original firmware and the "M" logo by
[t3knomanzer](https://github.com/t3knomanzer/maxmix-software). The v2 firmware
OLED layout and encoder acceleration follow the original 1.5.x firmware.

Licensed under the Apache License 2.0, like the original project. See
[LICENSE](LICENSE).
