# MaxMix

A Windows desktop companion for the **MaxMix** Arduino-based hardware audio
mixer. Lets you control per-application Windows audio (volume, mute) from a
physical knob + screen plugged into your PC over USB-Serial.

This is a **clean rewrite** of the original `MaxMix-Software` desktop app.
The firmware (Embedded/) is untouched — the wire protocol is preserved
byte-for-byte. The five well-known bugs in the original desktop are
explicitly regression-tested in
[`tests/MaxMix.Tests/Regression/OriginalBugRegressionTests.cs`](tests/MaxMix.Tests/Regression/OriginalBugRegressionTests.cs).

## Features

- Strict binary protocol codec — no desync from the original Bug #1.
- WASAPI session enumeration with **2-second reconciliation** so apps that
  launch AFTER MaxMix are picked up (fixes original Bug #5).
- Per-app, per-mode RGB color profiles (Default, Ocean, Sunset, Monochrome,
  Fire, Ice, Neon).
- WPF system-tray UI with single-instance enforcement and Windows auto-start.
- Structured logging to `%LOCALAPPDATA%/MaxMix/logs/maxmix-<date>.log`.

## Install (end users)

Download the latest `MaxMixSetup.exe` from the
[Releases](https://github.com/MaxiValfre/maxmix/releases) page and run it.
No .NET runtime needed — the installer is fully self-contained.

## Prerequisites (developers)

- Windows 10 or 11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- A MaxMix device on a CH340-based USB-Serial adapter (the firmware shipped
  with the original kit). Baud is fixed at **76800**, 8N1.

## Build

```powershell
# From the repo root.
dotnet build MaxMix.sln
```

The solution has 5 projects:

| Project          | Role                                                                 |
| ---------------- | -------------------------------------------------------------------- |
| `MaxMix.Core`    | Domain models, abstractions, settings (`net8.0`, sink-agnostic).     |
| `MaxMix.Serial`  | Binary codec + serial transport with state machine + backoff.        |
| `MaxMix.Audio`   | WASAPI session manager (NAudio) with reconciliation timer.           |
| `MaxMix.App`     | WPF shell, DI composition, Serilog wiring, system tray (Windows).    |
| `MaxMix.Tests`   | xUnit test suite — 284 tests covering every layer + regression suite. |

## Build the installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) installed and
`ISCC.exe` on PATH (or at the default `Program Files` location).

```powershell
.\installer\build-installer.ps1
```

This publishes a self-contained win-x64 build and packages it into
`installer/output/MaxMixSetup.exe`.

## Run tests

```powershell
dotnet test
```

Expected: **284 tests, 0 failures**. Tests are pure unit/integration tests
backed by in-memory fakes — no real serial port or audio device is required.

## Run the app

```powershell
dotnet run --project src/MaxMix.App
```

Or build and launch the produced executable:

```powershell
dotnet build -c Release
src\MaxMix.App\bin\Release\net8.0-windows\MaxMix.exe
```

The app opens a window plus a system-tray icon. Closing the window minimises
to the tray; quit via the tray menu or by sending Ctrl+C if you ran it from
a console.

## Connecting the device

1. Plug the MaxMix hardware into a USB port. Windows assigns a COM port
   (`Device Manager → Ports (COM & LPT)`).
2. Launch MaxMix. The app **scans available COM ports** and prefers the one
   whose driver matches CH340 (the chip used by the official MaxMix kit).
3. Connection state surfaces in the main window header:
   - `Disconnected`, `Scanning`, `Connecting`, `Handshaking` → not yet ready
   - `Ready` → live; turn the knob and you should see Windows volume react
   - `Backoff` → recently failed, waiting before retry

If no MaxMix is found, the transport keeps retrying with **exponential
backoff** (500ms → 1s → 2s → … capped at 30s).

## Configuration

Settings are persisted as JSON at:

```
%LOCALAPPDATA%\MaxMix\settings.json
```

The file is created on first launch with conservative defaults. Open the
**Settings** window from the tray menu to edit:

- General: auto-start with Windows, start minimised, close-to-tray, knob
  acceleration, sleep timeout.
- Device: last-connected COM port (manual override).
- Color profile: per-mode RGB selection (presets + RGB sliders).

The schema is versioned. Migrations run automatically when the file's
`SchemaVersion` is older than the current code's.

If the file gets corrupted, MaxMix renames it to `settings.json.bak` and
writes a fresh defaults file (the corrupted state is logged).

## Logs

Rolling daily file at:

```
%LOCALAPPDATA%\MaxMix\logs\maxmix-YYYYMMDD.log
```

The console (when run from a terminal) shows Information+ messages; the
file captures Debug+ for post-mortem analysis. 14 days of history are
retained.

## Project layout

```
MaxMix/
├── src/
│   ├── MaxMix.Core/           # Pure abstractions & domain types
│   │   ├── Abstractions/      #   IProtocolCodec, ISerialConnection, ...
│   │   ├── Models/            #   Command, VolumeData, SessionData, ...
│   │   ├── Events/            #   SessionAdded, VolumeChanged, ...
│   │   ├── Protocol/          #   IProtocolCodec contract
│   │   └── Settings/          #   AppSettings, ColorPresets, JsonSettingsService
│   │
│   ├── MaxMix.Serial/         # Wire format + transport
│   │   ├── BinaryProtocolCodec.cs
│   │   ├── SerialDeviceService.cs
│   │   ├── SystemSerialPortAdapter.cs
│   │   └── ConnectionStateMachine.cs
│   │
│   ├── MaxMix.Audio/          # WASAPI bridge
│   │   ├── WasapiSessionManager.cs
│   │   ├── Sessions/          #   SessionRegistry, AudioSession, RawAudioSession
│   │   └── Wasapi/            #   IAudioEnumerator + NAudio impl
│   │
│   └── MaxMix.App/            # WPF shell + composition root
│       ├── Orchestration/     #   StateOrchestrator (the glue)
│       ├── ViewModels/        #   ShellViewModel, SettingsViewModel
│       ├── Views/             #   MainWindow.xaml, SettingsWindow.xaml
│       ├── Services/          #   AutoStart, SingleInstance, SystemTray
│       └── Composition/       #   DI registration
│
└── tests/
    └── MaxMix.Tests/
        ├── Protocol/          # Codec + RGB encoding tests
        ├── Serial/            # Transport + connection state machine
        ├── Audio/             # Session registry, identity, manager
        ├── Orchestration/     # State sync, mode cycling, slot mapping
        ├── Settings/          # JSON persistence, presets, mapper
        ├── Logging/           # Verifies ILogger emits at lifecycle edges
        └── Regression/        # Original 5 bugs — explicit lock-in tests
```

## Original-bug regression tests

The five confirmed bugs in the original desktop software are each pinned by
a self-contained test in `tests/MaxMix.Tests/Regression/OriginalBugRegressionTests.cs`:

| Bug | Description                                                                       | Test method                                                  |
| --- | --------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| 1   | VOLUME command sent 32-byte SessionData payload (should be 2-byte VolumeData).    | `Bug1_VolumeChangeCommand_SendsExactly2BytePayload`           |
| 2   | `(int)mode + 1 % MAX` operator-precedence bug — mode never wraps.                 | `Bug2_SessionNavigation_CorrectModuloWraparound`              |
| 3   | 30-byte name field not null-terminated; firmware's `strlen` walked off the end.   | `Bug3_SessionName_AlwaysNullTerminated`                       |
| 4   | `Color` constructor typo (`b = g`); blue and green were the same channel.         | `Bug4_RgbEncoding_BytesInCorrectOrder`                        |
| 5   | Apps launched after startup were never detected.                                  | `Bug5_LateSessionDetection_ReconciliationCatchesNewApps`      |

If any of these fail, that specific class of bug has regressed.

## License

See [LICENSE](LICENSE) (matches the original repo).
