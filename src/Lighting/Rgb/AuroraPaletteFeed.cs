using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>The few colours the lighting shows most right now, for an app that paints its own devices to match.</summary>
public static class LightingPalette
{
    public const int Colours = 4;
    private const int Bins = 24;

    /// <summary>
    /// Up to <see cref="Colours"/> hue peaks of the given LEDs, each as its weighted mean colour at full
    /// brightness, ordered by hue so a slot keeps its place from frame to frame; fewer peaks repeat.
    /// Writes r,g,b triples 0..1 into <paramref name="into"/>; false when nothing is lit.
    /// </summary>
    public static bool Extract(IEnumerable<ReadOnlyMemory<byte>> ledSets, Span<float> into)
    {
        Span<double> weight = stackalloc double[Bins];
        Span<double> r = stackalloc double[Bins];
        Span<double> g = stackalloc double[Bins];
        Span<double> b = stackalloc double[Bins];
        weight.Clear(); r.Clear(); g.Clear(); b.Clear();
        foreach (var set in ledSets)
        {
            var leds = set.Span;
            for (var i = 0; i + 2 < leds.Length; i += 3)
            {
                int cr = leds[i], cg = leds[i + 1], cb = leds[i + 2];
                var max = Math.Max(cr, Math.Max(cg, cb));
                var min = Math.Min(cr, Math.Min(cg, cb));
                var delta = max - min;
                if (max < 8 || delta == 0)
                {
                    continue;
                }
                double h = max == cr ? ((cg - cb) / (double)delta + 6) % 6 : max == cg ? (cb - cr) / (double)delta + 2 : (cr - cg) / (double)delta + 4;
                var bin = Math.Min(Bins - 1, (int)(h / 6 * Bins));
                var w = delta / (double)max * (max / 255.0);
                weight[bin] += w; r[bin] += cr * w; g[bin] += cg * w; b[bin] += cb * w;
            }
        }

        Span<int> peaks = stackalloc int[Colours];
        var found = 0;
        Span<bool> taken = stackalloc bool[Bins];
        taken.Clear();
        while (found < Colours)
        {
            var best = -1;
            var bestScore = 0.0;
            for (var i = 0; i < Bins; i++)
            {
                if (taken[i])
                {
                    continue;
                }
                var score = weight[(i + Bins - 1) % Bins] * 0.5 + weight[i] + weight[(i + 1) % Bins] * 0.5;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            if (best < 0 || bestScore < 0.05)
            {
                break;
            }
            peaks[found++] = best;
            for (var d = -2; d <= 2; d++)
            {
                taken[(best + d + Bins) % Bins] = true;
            }
        }
        if (found == 0)
        {
            return false;
        }
        peaks[..found].Sort();
        for (var slot = 0; slot < Colours; slot++)
        {
            var bin = peaks[slot % found];
            double sr = 0, sg = 0, sb = 0;
            for (var d = -1; d <= 1; d++)
            {
                var k = (bin + d + Bins) % Bins;
                sr += r[k]; sg += g[k]; sb += b[k];
            }
            var peak = Math.Max(sr, Math.Max(sg, sb));
            into[slot * 3] = (float)(sr / peak);
            into[slot * 3 + 1] = (float)(sg / peak);
            into[slot * 3 + 2] = (float)(sb / peak);
        }
        return true;
    }
}

/// <summary>
/// Hands Aurora the lighting's main colours through its <c>/variables</c> endpoint as numbers
/// <c>nexus_c1_r</c> .. <c>nexus_c4_b</c> (0..1), so a profile's background layers can take their colour
/// from them. Devices leased to an SDK app are left out: they show that app's colours, not ours.
/// </summary>
public sealed class AuroraPaletteFeed : BackgroundService
{
    private const int TickMs = 250;
    private const int BackoffMs = 5000;
    private const double TauMs = 600.0;
    private const float SendDelta = 0.012f;

    private readonly IConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly OpenRgbLeases _leases;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(800) };
    private readonly float[] _target = new float[LightingPalette.Colours * 3];
    private readonly float[] _shown = new float[LightingPalette.Colours * 3];
    private readonly float[] _sent = new float[LightingPalette.Colours * 3];
    private bool _seeded;

    public AuroraPaletteFeed(IConfigStore store, LightingEngine engine, OpenRgbLeases leases)
    {
        _store = store;
        _engine = engine;
        _leases = leases;
        Array.Fill(_sent, -1f);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = Environment.TickCount64;
        var failed = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TickMs;
            var settings = _store.Load().Lighting.AuroraSync ?? new AuroraSyncSettings();
            if (settings.Enabled)
            {
                var now = Environment.TickCount64;
                if (LightingPalette.Extract(OwnLeds(), _target))
                {
                    Glide(now - last);
                    if (Changed())
                    {
                        var ok = await SendAsync(settings.Port, stoppingToken).ConfigureAwait(false);
                        if (!ok)
                        {
                            delay = BackoffMs;
                            if (!failed)
                            {
                                ServiceLog.Info($"[aurora-sync] Aurora does not answer on port {settings.Port}; retrying every {BackoffMs / 1000} s");
                            }
                        }
                        else if (failed)
                        {
                            ServiceLog.Info("[aurora-sync] Aurora takes the palette again");
                        }
                        failed = !ok;
                    }
                }
                last = now;
            }
            else
            {
                _seeded = false;
                Array.Fill(_sent, -1f);
                delay = 2000;
            }
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private IEnumerable<ReadOnlyMemory<byte>> OwnLeds()
    {
        var leased = _leases.Active;
        foreach (var frame in _engine.Devices)
        {
            if (frame.LedCount == 0 || leased.Contains(frame.Id))
            {
                continue;
            }
            yield return frame.LedBytes.ToArray();
        }
    }

    private void Glide(double dtMs)
    {
        var alpha = _seeded ? (float)(1.0 - Math.Exp(-Math.Max(0.0, dtMs) / TauMs)) : 1f;
        for (var i = 0; i < _shown.Length; i++)
        {
            _shown[i] += (_target[i] - _shown[i]) * alpha;
        }
        _seeded = true;
    }

    private bool Changed()
    {
        for (var i = 0; i < _shown.Length; i++)
        {
            if (Math.Abs(_shown[i] - _sent[i]) > SendDelta)
            {
                return true;
            }
        }
        return false;
    }

    private async Task<bool> SendAsync(int port, CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        for (var c = 0; c < LightingPalette.Colours; c++)
        {
            for (var ch = 0; ch < 3; ch++)
            {
                if (sb.Length > 1)
                {
                    sb.Append(',');
                }
                sb.Append("\"nexus_c").Append(c + 1).Append('_').Append("rgb"[ch]).Append("\":")
                    .Append(_shown[c * 3 + ch].ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
        sb.Append('}');
        try
        {
            using var content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"http://127.0.0.1:{port}/variables", content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            _shown.CopyTo(_sent, 0);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
