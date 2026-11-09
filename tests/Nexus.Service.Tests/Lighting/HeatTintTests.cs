using System.Collections.Generic;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Lighting.Heat;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class HeatTintTests
{
    private static HeatTintSettings Settings(bool enabled = true, float strength = 1f) => new()
    {
        Enabled = enabled,
        Strength = strength,
        Sensors = new List<SensorBinding>
        {
            new() { Category = "quick", Sensor = "summary/cpu-temp", Min = 65f, Max = 90f },
            new() { Category = "quick", Sensor = "summary/gpu-temp", Min = 58f, Max = 70f },
        },
    };

    private static float Read(SensorBinding b, float cpu, float gpu) => b.Sensor == "summary/cpu-temp" ? cpu : gpu;

    [Fact]
    public void A_cool_pc_has_no_tint_and_the_hottest_sensor_sets_it()
    {
        Assert.Equal(0f, HeatTint.Target(Settings(), b => Read(b, 60f, 50f)));
        Assert.Equal(0.5f, HeatTint.Target(Settings(), b => Read(b, 77.5f, 50f)), 3);
        Assert.Equal(1f, HeatTint.Target(Settings(), b => Read(b, 70f, 75f)), 3);
    }

    [Fact]
    public void Strength_scales_the_tint_and_off_or_missing_readings_give_none()
    {
        Assert.Equal(0.4f, HeatTint.Target(Settings(strength: 0.4f), b => Read(b, 95f, 50f)), 3);
        Assert.Equal(0f, HeatTint.Target(Settings(enabled: false), b => Read(b, 95f, 95f)));
        Assert.Equal(0f, HeatTint.Target(Settings(), _ => float.NaN));
        Assert.Equal(0f, HeatTint.Target(Settings(), _ => float.NegativeInfinity));
    }

    [Fact]
    public void The_level_glides_to_its_target_instead_of_jumping()
    {
        var after = HeatTint.Glide(0f, 1f, 250, 4000);
        Assert.InRange(after, 0.05f, 0.07f);
        Assert.Equal(0.3f, HeatTint.Glide(0.3f, 1f, 0, 4000));
    }

    [Fact]
    public void Every_prelude_effect_reads_the_heat_uniform_in_its_output_stage()
    {
        var source = ShaderLibrary.Get("kaleidoscope");
        Assert.Contains("uniform float u_heat;", source);
        Assert.Contains("clamp(u_heat, 0.0, 1.0)", source);
    }
}
