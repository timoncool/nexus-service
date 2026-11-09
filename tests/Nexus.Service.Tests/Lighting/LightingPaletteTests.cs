using System;
using Nexus.Service.Lighting.Rgb;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class LightingPaletteTests
{
    private static ReadOnlyMemory<byte> Leds(params (byte R, byte G, byte B, int Count)[] runs)
    {
        var total = 0;
        foreach (var run in runs) total += run.Count;
        var bytes = new byte[total * 3];
        var o = 0;
        foreach (var (r, g, b, count) in runs)
        {
            for (var i = 0; i < count; i++)
            {
                bytes[o++] = r; bytes[o++] = g; bytes[o++] = b;
            }
        }
        return bytes;
    }

    [Fact]
    public void Dark_or_white_leds_give_no_palette()
    {
        var into = new float[LightingPalette.Colours * 3];
        Assert.False(LightingPalette.Extract(new[] { Leds((0, 0, 0, 20), (200, 200, 200, 20)) }, into));
    }

    [Fact]
    public void Two_hues_come_out_at_full_brightness_in_hue_order_and_repeat_to_fill()
    {
        var into = new float[LightingPalette.Colours * 3];
        Assert.True(LightingPalette.Extract(new[] { Leds((0, 0, 60, 30)), Leds((90, 0, 0, 50)) }, into));
        Assert.Equal(new[] { 1f, 0f, 0f }, into[..3]);
        Assert.Equal(new[] { 0f, 0f, 1f }, into[3..6]);
        Assert.Equal(new[] { 1f, 0f, 0f }, into[6..9]);
        Assert.Equal(new[] { 0f, 0f, 1f }, into[9..12]);
    }

    [Fact]
    public void Near_hues_merge_into_one_peak()
    {
        var into = new float[LightingPalette.Colours * 3];
        Assert.True(LightingPalette.Extract(new[] { Leds((255, 0, 0, 10), (255, 20, 0, 10), (0, 255, 0, 10)) }, into));
        Assert.Equal(1f, into[0]);
        Assert.InRange(into[1], 0f, 0.1f);
        Assert.Equal(new[] { 0f, 1f, 0f }, into[3..6]);
    }
}
