using System;
using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink turning the overlay's raw BGRA frames into JPEG pushes on one fan screen. Frames
/// are fixed-size, so the stream is cut into whole frames before encoding. A frame is dropped
/// once the screen leaves panel mode: a session lingers after that, and the content worker owns
/// the screen by then (<see cref="IFanLcdScreens.SetPanelOwned"/>).
/// </summary>
public sealed class FanLcdPanelTransport : IStreamedPanelTransport, IOrientablePanelTransport
{
    /// <summary>Interval after which an unchanged frame is encoded and pushed again, as ApexOledStreamTransport does; it also bounds how late a rotation change shows.</summary>
    internal const long KeepAliveMs = 1000;

    private readonly IFanLcdScreens _screens;
    private readonly string _screenSerial;
    private readonly BgraJpegEncoder _encoder = new(FanLcdContent.Size, FanLcdContent.Size);
    private readonly byte[] _frame;
    private readonly byte[] _shown;
    private bool _hasShown;
    private long _lastPushMs;
    private readonly Func<long> _clock;
    private readonly PanelOrientationFilter _orientation = new();
    private int _filled;
    private bool _disposed;
    private bool _encodeFailLogged;

    public FanLcdPanelTransport(IFanLcdScreens screens, string screenSerial, string panelSerial, Func<long>? clock = null)
    {
        _screens = screens;
        _screenSerial = screenSerial;
        Serial = panelSerial;
        _frame = new byte[_encoder.FrameBytes];
        _shown = new byte[_encoder.FrameBytes];
        _clock = clock ?? (() => Environment.TickCount64);
        if (clock is null)
        {
            _keepAwake = new System.Threading.Timer(_ => KeepAwake(), null, StarvationMs, StarvationMs / 2);
        }
    }

    public string Serial { get; }

    public bool IsOpen => !_disposed && Attached();

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source) => _orientation.Bind(source);

    public void Open()
    {
        if (!Attached())
        {
            throw new IOException($"{_screens.LogTag} screen {_screenSerial} not attached");
        }
        _filled = 0;
    }

    /// <summary>The screen has no player to start; the first pushed frame is the start.</summary>
    public void StartPlayer()
    {
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!payload.IsEmpty)
        {
            int take = Math.Min(_frame.Length - _filled, payload.Length);
            payload[..take].CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            payload = payload[take..];
            if (_filled < _frame.Length)
            {
                continue;
            }
            _filled = 0;
            PushFrame();
        }
    }

    private void PushFrame()
    {
        var now = _clock();
        if (_hasShown && now - _lastPushMs < KeepAliveMs && _frame.AsSpan().SequenceEqual(_shown))
        {
            return;
        }
        byte[] jpeg;
        try
        {
            jpeg = _encoder.Encode(_orientation.Apply(_frame, FanLcdContent.Size, FanLcdContent.Size)).ToArray();
        }
        catch (Exception ex)
        {
            if (!_encodeFailLogged)
            {
                _encodeFailLogged = true;
                ServiceLog.Warn($"[{_screens.LogTag}] panel frame encode failed for {_screenSerial}: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }
        _encodeFailLogged = false;
        lock (_pushGate)
        {
            if (!_screens.PushPanelFrame(_screenSerial, jpeg))
            {
                _hasShown = false;
                _lastJpeg = null;
                return;
            }
            _lastJpeg = jpeg;
            _lastPushMs = now;
        }
        _frame.CopyTo(_shown, 0);
        _hasShown = true;
    }

    // The overlay sends nothing while a panel's picture stands still, and a wireless TL LCD left
    // without traffic drops off USB (uni-wireless-sync keeps it awake with periodic traffic), so
    // the last frame is pushed again whenever nothing else went out for StarvationMs.
    internal const long StarvationMs = 2000;
    private readonly object _pushGate = new();
    private byte[]? _lastJpeg;
    private System.Threading.Timer? _keepAwake;

    private void KeepAwake()
    {
        lock (_pushGate)
        {
            if (_disposed || _lastJpeg is not { } jpeg || _clock() - _lastPushMs < StarvationMs)
            {
                return;
            }
            if (_screens.PushPanelFrame(_screenSerial, jpeg))
            {
                _lastPushMs = _clock();
            }
            else
            {
                _lastJpeg = null;
            }
        }
    }

    /// <summary>One keep-awake pass, for tests.</summary>
    internal void KeepAwakeTick() => KeepAwake();

    private bool Attached() => _screens.AttachedSerials().Contains(_screenSerial, StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        lock (_pushGate)
        {
            _disposed = true;
        }
        _keepAwake?.Dispose();
        _encoder.Dispose();
    }
}
