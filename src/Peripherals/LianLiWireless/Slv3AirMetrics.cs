using System.Diagnostics.Metrics;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>Radio metrics of the L-Wireless dongle.</summary>
public static class Slv3AirMetrics
{
    public const string MeterName = "Nexus.LianLiWireless";
    public const string RfPayloadsName = "rf.payloads";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>RF payloads written to the TX dongle.</summary>
    public static readonly Counter<long> RfPayloads =
        Meter.CreateCounter<long>(RfPayloadsName, unit: "{payload}", description: "RF payloads written to the L-Wireless TX dongle");
}

/// <summary>
/// The device page's air count: while on, listens to <see cref="Slv3AirMetrics.RfPayloads"/> and
/// puts each minute's total and its busiest second into the hub state and the service log.
/// While off nothing listens, so the counter costs next to nothing.
/// </summary>
public sealed class Slv3AirCount : IDisposable
{
    private const int ReportMs = 60_000;

    private readonly Slv3State _state;
    private readonly Func<long> _nowMs;
    private readonly object _gate = new();
    private MeterListener? _listener;
    private long _reportStartMs;
    private long _secondStartMs;
    private int _count;
    private int _secondCount;
    private int _peak;

    public Slv3AirCount(Slv3State state, Func<long> nowMs)
    {
        _state = state;
        _nowMs = nowMs;
    }

    public void SetEnabled(bool on)
    {
        lock (_gate)
        {
            _state.AirCount = on;
            if (on == (_listener is not null))
            {
                return;
            }
            if (!on)
            {
                _listener!.Dispose();
                _listener = null;
                _state.AirPayloadsPerMinute = null;
                _state.AirPeakPerSecond = null;
                return;
            }
            _reportStartMs = _secondStartMs = _nowMs();
            _count = _secondCount = _peak = 0;
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == Slv3AirMetrics.MeterName && instrument.Name == Slv3AirMetrics.RfPayloadsName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Note(value));
            _listener.Start();
        }
    }

    private void Note(long value)
    {
        lock (_gate)
        {
            if (_listener is null)
            {
                return;
            }
            var now = _nowMs();
            if (now - _secondStartMs >= 1000)
            {
                _secondStartMs = now;
                _secondCount = 0;
            }
            _secondCount += (int)value;
            _peak = Math.Max(_peak, _secondCount);
            _count += (int)value;
            if (now - _reportStartMs < ReportMs)
            {
                return;
            }
            var perMinute = (int)(_count * 60_000L / (now - _reportStartMs));
            _state.AirPayloadsPerMinute = perMinute;
            _state.AirPeakPerSecond = _peak;
            ServiceLog.Info($"[lianli-wireless] air: {perMinute} RF payloads a minute, peak {_peak}/s");
            _reportStartMs = now;
            _count = 0;
            _peak = 0;
        }
    }

    public void Dispose() => SetEnabled(false);
}
