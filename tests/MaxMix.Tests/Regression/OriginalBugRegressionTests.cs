namespace MaxMix.Tests.Regression;

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.App.Orchestration;
using MaxMix.Audio.Sessions;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Protocol;
using MaxMix.Core.Settings;
using MaxMix.Serial;
using MaxMix.Tests.Orchestration;
using Xunit;

/// <summary>
/// Self-contained regression tests for every CONFIRMED original bug
/// documented in <c>"MaxMix serial protocol specification"</c>:
///
///   #1 — VOLUME command on default-device-change sent SessionData (32 bytes)
///        instead of VolumeData (2 bytes), poisoning the firmware parser.
///   #2 — Mode/index increment used <c>(int)mode + 1 % MAX</c>; operator
///        precedence binds % tighter than +, so the value never advanced.
///   #3 — 30-byte name field was NOT null-terminated; firmware uses strlen()
///        and walks past the buffer end.
///   #4 — Color constructor had typo <c>b = g</c>, so blue and green were
///        the same channel on the wire.
///   #5 — Apps were not detected after startup because the firmware dirty
///        flag was always true and the desktop never reconciled.
///
/// Each test is intentionally self-contained: no shared fixtures, no helper
/// stacks beyond what's needed. The point is "if this test passes, that
/// specific bug is provably fixed in the current build".
/// </summary>
public class OriginalBugRegressionTests
{
    // -------------------------------------------------------------------
    // Bug #1 — Protocol desync: VOLUME command must send exactly 2 bytes
    // -------------------------------------------------------------------

    [Fact]
    public void Bug1_VolumeChangeCommand_SendsExactly2BytePayload()
    {
        var codec = new BinaryProtocolCodec();
        var volume = new VolumeData(Id: 7, IsDefault: true, Volume: 75, IsMuted: false);

        // Encode every VOLUME_*_CHANGE command and check the wire shape.
        var commands = new[]
        {
            Command.VolumeCurrChange,
            Command.VolumeAltChange,
            Command.VolumePrevChange,
            Command.VolumeNextChange,
        };

        foreach (var cmd in commands)
        {
            var frame = codec.EncodeVolumeChange(cmd, volume);

            // 1 command byte + EXACTLY 2 payload bytes. Original software
            // sent SessionData (1 + 32 = 33 bytes) here.
            Assert.Equal(3, frame.Length);
            Assert.Equal((byte)cmd, frame[0]);
        }
    }

    [Fact]
    public async Task Bug1_TransportLayer_RejectsWrongSizedFramePreSend()
    {
        // The transport layer is the second line of defence: even if a buggy
        // caller hands us a frame with the wrong payload, ValidateFrame
        // throws BEFORE the bytes hit the wire.
        var codec = new BinaryProtocolCodec();
        var port  = new MaxMix.Tests.Serial.FakeSerialPortAdapter();
        var opts  = new SerialDeviceServiceOptions
        {
            PortName          = "COM5",
            PostOpenDelay     = TimeSpan.Zero,
            HandshakeTimeout  = TimeSpan.FromMilliseconds(50),
            AckTimeout        = TimeSpan.FromMilliseconds(50),
            ReadPollInterval  = TimeSpan.FromMilliseconds(5),
            InitialBackoff    = TimeSpan.FromMilliseconds(10),
            MaxBackoff        = TimeSpan.FromMilliseconds(40),
        };

        await using var svc = new SerialDeviceService(port, codec, opts);

        // Build a malformed VOLUME_CURR_CHANGE frame: 1 cmd byte + 32 payload
        // bytes (the bug-#1 shape). Transport must refuse.
        var bad = new byte[1 + 32];
        bad[0] = (byte)Command.VolumeCurrChange;

        await Assert.ThrowsAsync<PayloadSizeMismatchException>(async () =>
            await svc.SendAsync(bad));
    }

    // -------------------------------------------------------------------
    // Bug #2 — Operator precedence: (index + 1) % count, not index + 1 % count
    // -------------------------------------------------------------------

    [Fact]
    public void Bug2_SessionNavigation_CorrectModuloWraparound()
    {
        // The buggy form `index + 1 % count` evaluates as `index + (1 % count)`
        // which is `index + 1` for any count > 1 — it NEVER wraps.
        //
        // For count=4 and index=3, the buggy form returns 4 (out of range);
        // the correct form returns 0.
        Assert.Equal(0, SlotMapper.NextIndex(currentIndex: 3, count: 4));

        // Triangulate with multiple counts so the test fails distinctively if
        // someone reverts the parentheses.
        Assert.Equal(1, SlotMapper.NextIndex(0, 4));
        Assert.Equal(2, SlotMapper.NextIndex(1, 4));
        Assert.Equal(3, SlotMapper.NextIndex(2, 4));

        // PreviousIndex is the symmetric case — wraps from 0 to count-1.
        Assert.Equal(3, SlotMapper.PreviousIndex(currentIndex: 0, count: 4));
        Assert.Equal(0, SlotMapper.PreviousIndex(1, 4));
    }

    [Fact]
    public async Task Bug2_ModeCycle_AdvancesPastLastMode()
    {
        // Original bug #2 was a precedence error in the mode-cycle modulo math
        // that left the user stuck on the last mode (Game). After Batch 2 the
        // user-visible cycle has been simplified to Application only — Output /
        // Input / Game are firmware-internal modes the desktop no longer drives.
        // This test now asserts the *new* invariant: regardless of the
        // (possibly legacy) startup mode value, CycleModeAsync resolves to
        // Application and never gets stuck on Game.
        var audio  = new FakeAudioSessionManager();
        var serial = new FakeSerialConnection();
        var codec  = new BinaryProtocolCodec();

        using var orch = new StateOrchestrator(audio, serial, codec, new StateOrchestratorOptions
        {
            StartupMode = DisplayMode.Game,  // legacy persisted value
        });

        // Without awaiting StartAsync (which would call the audio manager).
        await orch.CycleModeAsync();

        Assert.Equal(DisplayMode.Application, orch.CurrentMode);
    }

    // -------------------------------------------------------------------
    // Bug #3 — Non-null-terminated names: 30-byte field MUST end in \0
    // -------------------------------------------------------------------

    [Fact]
    public void Bug3_SessionName_AlwaysNullTerminated()
    {
        var codec = new BinaryProtocolCodec();

        // Case A: short name. Last byte of the 30-byte name field is 0.
        var shortFrame = codec.EncodeSessionData(
            Command.CurrentSession,
            new SessionData("Spotify", new VolumeData(1, false, 50, false)));
        // [0]=cmd, [1..30]=name (30 bytes), [31..32]=volume.
        Assert.Equal(33, shortFrame.Length);
        Assert.Contains((byte)0, shortFrame.AsSpan(1, 30).ToArray());

        // Case B: name longer than 30 bytes — must be truncated AND still
        // null-terminated. Original software dropped the terminator under
        // truncation and the firmware's strlen ran past the buffer.
        var longName = new string('A', 50);
        var longFrame = codec.EncodeSessionData(
            Command.CurrentSession,
            new SessionData(longName, new VolumeData(1, false, 50, false)));
        Assert.Equal(33, longFrame.Length);
        // At least one byte in the 30-byte name field must be 0.
        var nameBytes = longFrame.AsSpan(1, 30).ToArray();
        Assert.Contains((byte)0, nameBytes);
        // Specifically, the LAST byte of the name field must be 0 — that's
        // the strict guarantee the firmware's strlen depends on.
        Assert.Equal((byte)0, nameBytes[29]);

        // Case C: empty name — entire field is zero (already null-terminated).
        var emptyFrame = codec.EncodeSessionData(
            Command.CurrentSession,
            new SessionData("", new VolumeData(1, false, 0, false)));
        for (int i = 1; i <= 30; i++) Assert.Equal((byte)0, emptyFrame[i]);
    }

    // -------------------------------------------------------------------
    // Bug #4 — Color transposition: RGB bytes in correct order, R/G/B distinct
    // -------------------------------------------------------------------

    [Fact]
    public void Bug4_RgbEncoding_BytesInCorrectOrder()
    {
        var codec = new BinaryProtocolCodec();

        // Use channel-distinct colours so any swap is observable.
        var settings = new DeviceSettings(
            SleepAfterSeconds: 0,
            AccelerationPercentage: 50,
            ContinuousScroll: true,
            VolumeMinColor:   new RgbColor(R: 10,  G: 20,  B: 30),
            VolumeMaxColor:   new RgbColor(R: 40,  G: 50,  B: 60),
            MixChannelAColor: new RgbColor(R: 70,  G: 80,  B: 90),
            MixChannelBColor: new RgbColor(R: 100, G: 110, B: 120));

        var frame = codec.EncodeSettings(settings);

        // [0]=cmd, [1]=sleep, [2]=accel|cont, [3..5]=VolMin, [6..8]=VolMax,
        // [9..11]=MixA, [12..14]=MixB.
        Assert.Equal(10, frame[3]); Assert.Equal(20, frame[4]); Assert.Equal(30, frame[5]);
        Assert.Equal(40, frame[6]); Assert.Equal(50, frame[7]); Assert.Equal(60, frame[8]);
        Assert.Equal(70, frame[9]); Assert.Equal(80, frame[10]); Assert.Equal(90, frame[11]);
        Assert.Equal(100, frame[12]); Assert.Equal(110, frame[13]); Assert.Equal(120, frame[14]);
    }

    [Fact]
    public void Bug4_AllPresetColors_HaveDistinctRgbChannels()
    {
        // The original "b = g" typo silently collapsed two channels. Even if
        // the codec is correct, the model itself must keep R/G/B independent
        // so the seven shipped presets aren't accidentally monochrome.
        // (Monochrome is intentionally R==G==B — exclude it.)
        foreach (var (name, profile) in ColorPresets.All)
        {
            if (name == "Monochrome") continue;

            // At least ONE of the four colours in the preset must have
            // R != G != B. If the bug were live, every channel would equal
            // the same source value.
            var distinctSomewhere =
                IsDistinct(profile.VolumeMinColor) ||
                IsDistinct(profile.VolumeMaxColor) ||
                IsDistinct(profile.MixChannelAColor) ||
                IsDistinct(profile.MixChannelBColor);

            Assert.True(distinctSomewhere,
                $"preset {name} has identical R/G/B everywhere — bug #4 regression?");
        }

        static bool IsDistinct(RgbColor c) => !(c.R == c.G && c.G == c.B);
    }

    // -------------------------------------------------------------------
    // Bug #5 — Apps not detected after startup; reconciliation catches them
    // -------------------------------------------------------------------

    [Fact]
    public async Task Bug5_LateSessionDetection_ReconciliationCatchesNewApps()
    {
        // Reproduce the bug's environment: a session that appears AFTER
        // the manager has started (so any push-only design would miss it).
        // The reconciliation pass must surface it via SessionAdded.
        var enumerator = new MaxMix.Tests.Audio.FakeAudioEnumerator();
        // Start with no sessions — like a freshly-booted system.
        enumerator.Sessions.Clear();

        using var mgr = new MaxMix.Audio.WasapiSessionManager(enumerator);

        var added = new System.Collections.Generic.List<byte>();
        mgr.SessionAdded += (_, e) => added.Add(e.Session.Id);

        await mgr.StartAsync();
        Assert.Empty(added); // baseline: nothing yet

        // Simulate Spotify launching AFTER startup. No event was raised
        // (push surface is empty). Reconciliation must still find it.
        enumerator.Sessions.Add(new RawAudioSession(
            ExecutablePath: @"c:\spotify.exe",
            DisplayName:    "Spotify",
            Volume:         50,
            IsMuted:        false,
            IsDefault:      false,
            Kind:           AudioSessionKind.Application));

        // Force a reconciliation pass — same code path the 2-second timer uses.
        mgr.ReconcileNow();

        Assert.Single(added);
    }
}
