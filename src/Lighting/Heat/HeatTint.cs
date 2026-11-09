using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Heat;

/// <summary>
/// How far a warm PC pulls every prelude effect's output toward red, 0..1. Shaders read it as
/// <c>u_heat</c> in <c>gainOut()</c>; 0 leaves an effect exactly as it renders.
/// </summary>
public static class HeatTint
{
    private static float _level;

    public static float Level
    {
        get => Volatile.Read(ref _level);
        internal set => Volatile.Write(ref _level, value);
    }

    /// <summary>The hottest of the bound readings, each over its own range, scaled by the strength.</summary>
    public static float Target(HeatTintSettings settings, Func<SensorBinding, float> read)
    {
        if (!settings.Enabled)
        {
            return 0f;
        }
        var heat = 0f;
        foreach (var binding in settings.Sensors)
        {
            if (binding.IsUsable)
            {
                heat = Math.Max(heat, binding.Normalize(read(binding)));
            }
        }
        return heat * Math.Clamp(settings.Strength, 0f, 1f);
    }

    /// <summary>A step of the glide toward the target; temperature jitter must not flicker the room.</summary>
    public static float Glide(float current, float target, double dtMs, double tauMs)
    {
        if (dtMs <= 0.0)
        {
            return current;
        }
        var alpha = 1.0 - Math.Exp(-dtMs / tauMs);
        return current + (float)((target - current) * alpha);
    }
}

/// <summary>Keeps <see cref="HeatTint.Level"/> following the bound sensors while the setting is on.</summary>
public sealed class HeatTintController : BackgroundService
{
    private const int TickMs = 250;
    private const double TauMs = 4000.0;

    private readonly IConfigStore _store;
    private readonly SensorFeed _feed;
    private readonly Dictionary<string, SensorFeed.Channel> _channels = new(StringComparer.Ordinal);

    public HeatTintController(IConfigStore store, SensorFeed feed)
    {
        _store = store;
        _feed = feed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = Environment.TickCount64;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _store.Load().Lighting.HeatTint ?? new HeatTintSettings();
            var target = HeatTint.Target(settings, Read);
            var now = Environment.TickCount64;
            var level = HeatTint.Glide(HeatTint.Level, target, now - last, TauMs);
            HeatTint.Level = level < 0.002f && target == 0f ? 0f : level;
            last = now;
            try
            {
                await Task.Delay(TickMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private float Read(SensorBinding binding)
    {
        if (!_channels.TryGetValue(binding.FeedKey, out var channel))
        {
            channel = _feed.Acquire(binding);
            _channels[binding.FeedKey] = channel;
        }
        return channel.Read();
    }
}
