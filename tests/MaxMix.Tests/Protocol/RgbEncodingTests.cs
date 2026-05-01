namespace MaxMix.Tests.Protocol;

using MaxMix.Core.Models;
using MaxMix.Serial;

/// <summary>
/// Bug #4 regression tests at the codec layer. The original software wrote
/// `b = g` for some RGB fields, transposing channels on the wire. These tests
/// pin every byte position in the SETTINGS payload so any future re-introduction
/// of that bug fails fast.
///
/// Wire layout (per DeviceSettings.cs — payload byte indices):
///   byte 0     : sleep
///   byte 1     : accel|continuous
///   bytes 2-4  : VolumeMinColor   R,G,B
///   bytes 5-7  : VolumeMaxColor   R,G,B
///   bytes 8-10 : MixChannelAColor R,G,B
///   bytes 11-13: MixChannelBColor R,G,B
///
/// FRAME byte indices (= payload index + 1, because frame[0] is the command byte):
///   frame[0]      : 0x03 (Settings)
///   frame[1]      : sleep
///   frame[2]      : accel|continuous
///   frame[3..5]   : VolumeMinColor
///   frame[6..8]   : VolumeMaxColor
///   frame[9..11]  : MixChannelAColor
///   frame[12..14] : MixChannelBColor
/// </summary>
public class RgbEncodingTests
{
    private static readonly BinaryProtocolCodec Codec = new();

    [Fact]
    public void EncodeSettings_PutsRgbInRightOrder_NotTransposed()
    {
        // Distinct asymmetric bytes per channel per field — no two bytes equal
        // anywhere in the 12-byte color region. Any swap is detectable.
        var settings = new DeviceSettings(
            SleepAfterSeconds: 0,
            AccelerationPercentage: 0,
            ContinuousScroll: false,
            VolumeMinColor:   new RgbColor(0xA1, 0xA2, 0xA3),
            VolumeMaxColor:   new RgbColor(0xB1, 0xB2, 0xB3),
            MixChannelAColor: new RgbColor(0xC1, 0xC2, 0xC3),
            MixChannelBColor: new RgbColor(0xD1, 0xD2, 0xD3));

        var frame = Codec.EncodeSettings(settings);

        // Frame layout: [cmd][sleep][accel|cont][12 RGB bytes]
        Assert.Equal(15, frame.Length);

        // VolumeMinColor at frame[3..5]
        Assert.Equal((byte)0xA1, frame[3]);
        Assert.Equal((byte)0xA2, frame[4]);
        Assert.Equal((byte)0xA3, frame[5]);

        // VolumeMaxColor at frame[6..8]
        Assert.Equal((byte)0xB1, frame[6]);
        Assert.Equal((byte)0xB2, frame[7]);
        Assert.Equal((byte)0xB3, frame[8]);

        // MixChannelAColor at frame[9..11]
        Assert.Equal((byte)0xC1, frame[9]);
        Assert.Equal((byte)0xC2, frame[10]);
        Assert.Equal((byte)0xC3, frame[11]);

        // MixChannelBColor at frame[12..14]
        Assert.Equal((byte)0xD1, frame[12]);
        Assert.Equal((byte)0xD2, frame[13]);
        Assert.Equal((byte)0xD3, frame[14]);
    }

    [Theory]
    [InlineData(255, 128,   0)]  // common test vector from the brief.
    [InlineData(  0, 255,   0)]  // green only — exercises G channel.
    [InlineData(  0,   0, 255)]  // blue only  — exercises B channel.
    [InlineData(255,   0,   0)]  // red only   — exercises R channel.
    [InlineData( 17,  34,  51)]  // arbitrary — proves no special-case logic.
    public void EncodeSettings_VolumeMinColor_PreservesEachChannel(byte r, byte g, byte b)
    {
        var settings = new DeviceSettings(
            SleepAfterSeconds: 0,
            AccelerationPercentage: 0,
            ContinuousScroll: false,
            VolumeMinColor:   new RgbColor(r, g, b),
            VolumeMaxColor:   default,
            MixChannelAColor: default,
            MixChannelBColor: default);

        var frame = Codec.EncodeSettings(settings);

        // VolumeMinColor → frame[3..5] (frame[0]=cmd, [1]=sleep, [2]=accel).
        Assert.Equal(r, frame[3]);
        Assert.Equal(g, frame[4]);
        Assert.Equal(b, frame[5]);
    }

    [Fact]
    public void RgbColor_PreservesChannelsExactly_AsConstructed()
    {
        // Sanity check at the model layer — the original bug was here too.
        var c = new RgbColor(255, 128, 0);
        Assert.Equal((byte)255, c.R);
        Assert.Equal((byte)128, c.G);
        Assert.Equal((byte)0,   c.B);
    }
}
