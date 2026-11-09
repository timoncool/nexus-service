using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Answers the OpenRGB SDK port in place of the daemon, so Nexus is the one holder of the hardware
/// and other SDK clients get only what is leased to them (<see cref="OpenRgbLeases"/>). Each client
/// gets its own daemon connection; its device list is the leased subset, device indices are
/// translated both ways, and packets for any other device, rescans and profile loads never reach
/// the daemon. Protocol is held at 5, where devices are addressed by list index.
/// </summary>
public sealed class OpenRgbBroker : BackgroundService
{
    internal const uint MaxProtocolVersion = 5;
    private const int MaxPacketBytes = 8 * 1024 * 1024;

    private readonly OpenRgbLeases _leases;
    private readonly Func<IReadOnlyList<RgbDevice>> _devices;
    private readonly int _daemonPort;
    private readonly int _listenPort;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private readonly Dictionary<long, Session> _sessions = new();
    private long _nextSession;

    public OpenRgbBroker(OpenRgbLeases leases, RgbBridge bridge, OpenRgbProcessManager proc, IConfigStore store)
        : this(leases, () => bridge.Devices, proc.DaemonPort, OpenRgbProcessManager.DefaultPort,
            store.Load().Devices.OpenRgbBroker && proc.DaemonPort != OpenRgbProcessManager.DefaultPort)
    {
        _proc = proc;
        SettleFor = TimeSpan.FromSeconds(6);
    }

    // The daemon the clients proxy to. A client (Aurora) may connect while Nexus has the daemon
    // stopped (blackout, idle): the broker brings it back up so the client always reaches its devices.
    private readonly OpenRgbProcessManager? _proc;

    internal OpenRgbBroker(OpenRgbLeases leases, Func<IReadOnlyList<RgbDevice>> devices, int daemonPort, int listenPort, bool enabled)
    {
        _leases = leases;
        _devices = devices;
        _daemonPort = daemonPort;
        _listenPort = listenPort;
        _enabled = enabled;
        _leases.ViewChanged += OnViewChanged;
    }

    /// <summary>Set once the listener is up; tests wait on it.</summary>
    internal TaskCompletionSource Listening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_enabled)
        {
            return;
        }
        TcpListener? listener = null;
        while (!ct.IsCancellationRequested && listener is null)
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, _listenPort);
                listener.Start();
                ServiceLog.Info($"[openrgb-broker] listening on {_listenPort}, daemon on {_daemonPort}");
                Listening.TrySetResult();
            }
            catch (SocketException ex)
            {
                listener = null;
                ServiceLog.Warn($"[openrgb-broker] port {_listenPort} busy ({ex.SocketErrorCode}); retrying");
                await Task.Delay(5000, ct).ConfigureAwait(false);
            }
        }
        if (listener is null)
        {
            return;
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _nextSession);
                _ = Task.Run(() => RunSessionAsync(id, client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private void OnViewChanged(IReadOnlyCollection<string> clients)
    {
        List<Session> affected;
        lock (_gate)
        {
            affected = new List<Session>();
            foreach (var s in _sessions.Values)
            {
                foreach (var c in clients)
                {
                    if (string.Equals(s.Name, c, StringComparison.Ordinal))
                    {
                        affected.Add(s);
                        break;
                    }
                }
            }
        }
        foreach (var s in affected)
        {
            _ = s.SendToClientAsync(0, OpenRgbProtocol.PacketId.DeviceListUpdated, ReadOnlyMemory<byte>.Empty);
        }
    }

    // The daemon may be down when a client connects (Nexus stopped it on blackout/idle, or it is still
    // starting). Ask the process manager to (re)start it and retry the connect until its port answers.
    // A failed ConnectAsync leaves the socket unusable, so each attempt gets a fresh TcpClient.
    private async Task<TcpClient> ConnectUpstreamAsync(CancellationToken ct)
    {
        const int attempts = 40; // ~8s: the daemon's own detection pass takes a few seconds to open the port
        for (var i = 0; ; i++)
        {
            _proc?.Start();
            var upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, _daemonPort, ct).ConfigureAwait(false);
                return upstream;
            }
            catch (SocketException) when (i < attempts && !ct.IsCancellationRequested)
            {
                upstream.Dispose();
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch
            {
                upstream.Dispose();
                throw;
            }
        }
    }

    /// <summary>How long a freshly started daemon is left alone before a client is let through to it; zero skips the wait.</summary>
    internal TimeSpan SettleFor { get; set; } = TimeSpan.Zero;

    private static readonly TimeSpan SettleCap = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UpstreamRetry = TimeSpan.FromMilliseconds(500);
    // A client reads its list right after connecting; the daemon link of a running daemon is up within this.
    private static readonly TimeSpan AttachWait = TimeSpan.FromMilliseconds(800);

    // What the daemon last answered a version request with; a client that connects while the daemon is down is answered from it.
    private uint _serverVersion;

    private async Task RunSessionAsync(long id, TcpClient client, CancellationToken ct)
    {
        using var clientConn = client;
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Session? session = null;
        try
        {
            session = new Session(clientConn.GetStream());
            lock (_gate)
            {
                _sessions[id] = session;
            }
            var link = KeepUpstreamAsync(session, sessionCts.Token);
            var up = PumpClientAsync(id, session, sessionCts.Token);
            await Task.WhenAny(link, up).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidOperationException)
        {
        }
        finally
        {
            sessionCts.Cancel();
            lock (_gate)
            {
                _sessions.Remove(id);
            }
            if (session?.Name is { } name)
            {
                ServiceLog.Info($"[openrgb-broker] '{name}' disconnected");
                _leases.ClientDisconnected(id);
            }
        }
    }

    // The client's connection outlives the daemon's: a daemon that exits is restarted and joined again,
    // and the client only sees its device list empty and fill back. A client let through while the
    // daemon is still detecting hardware has crashed the daemon, so the link waits for the list to settle.
    private async Task KeepUpstreamAsync(Session s, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? upstream = null;
            try
            {
                upstream = await ConnectUpstreamAsync(ct).ConfigureAwait(false);
                await WaitDaemonSettledAsync(ct).ConfigureAwait(false);
                var stream = upstream.GetStream();
                var (askVersion, name, flags, enumerated) = s.Attach(stream);
                if (askVersion)
                {
                    await s.SendUpstreamAsync(0, OpenRgbProtocol.PacketId.RequestProtocolVersion, OpenRgbProtocol.BuildProtocolVersionBody(s.ClientVersion), ct).ConfigureAwait(false);
                }
                if (name is not null)
                {
                    await s.SendUpstreamAsync(0, OpenRgbProtocol.PacketId.SetClientName, Encoding.UTF8.GetBytes(name + "\0"), ct).ConfigureAwait(false);
                }
                if (flags is not null)
                {
                    await s.SendUpstreamAsync(0, OpenRgbProtocol.PacketId.SetClientFlags, flags, ct).ConfigureAwait(false);
                }
                if (enumerated)
                {
                    await s.SendToClientAsync(0, OpenRgbProtocol.PacketId.DeviceListUpdated, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
                }
                await PumpUpstreamAsync(s, stream, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException && !ct.IsCancellationRequested)
            {
            }
            finally
            {
                upstream?.Dispose();
            }
            if (s.Detach())
            {
                ServiceLog.Info($"[openrgb-broker] daemon link of '{s.Name}' lost; rejoining");
                await s.SendToClientAsync(0, OpenRgbProtocol.PacketId.DeviceListUpdated, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            }
            await Task.Delay(UpstreamRetry, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitDaemonSettledAsync(CancellationToken ct)
    {
        if (_proc is null || SettleFor <= TimeSpan.Zero)
        {
            return;
        }
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < SettleCap.TotalMilliseconds)
        {
            var up = _proc.Uptime;
            if (up >= SettleFor)
            {
                return;
            }
            await Task.Delay(up > TimeSpan.Zero ? SettleFor - up : UpstreamRetry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Client to daemon: what the client may do passes with its device index translated; the rest is answered here or dropped.</summary>
    private async Task PumpClientAsync(long id, Session s, CancellationToken ct)
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        while (!ct.IsCancellationRequested)
        {
            await s.Client.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var (dev, packet, size) = OpenRgbProtocol.ReadHeader(header);
            if (size > MaxPacketBytes)
            {
                throw new InvalidOperationException("oversized packet");
            }
            var body = new byte[size];
            await s.Client.ReadExactlyAsync(body, ct).ConfigureAwait(false);

            switch (packet)
            {
                case OpenRgbProtocol.PacketId.RequestProtocolVersion:
                {
                    var asked = body.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(body) : 0;
                    var version = Math.Min(asked, MaxProtocolVersion);
                    if (s.AskVersion(version, _serverVersion != 0))
                    {
                        await s.SendUpstreamAsync(dev, packet, OpenRgbProtocol.BuildProtocolVersionBody(version), ct).ConfigureAwait(false);
                    }
                    else if (_serverVersion != 0)
                    {
                        await s.SendToClientAsync(0, packet, OpenRgbProtocol.BuildProtocolVersionBody(Math.Min(_serverVersion, MaxProtocolVersion))).ConfigureAwait(false);
                    }
                    break;
                }
                case OpenRgbProtocol.PacketId.SetClientName:
                {
                    var name = Encoding.UTF8.GetString(body).TrimEnd('\0');
                    if (s.Name is null)
                    {
                        s.Name = name;
                        ServiceLog.Info($"[openrgb-broker] '{name}' connected");
                        _leases.ClientConnected(id, name);
                    }
                    await s.SendUpstreamAsync(dev, packet, body, ct).ConfigureAwait(false);
                    break;
                }
                case OpenRgbProtocol.PacketId.RequestControllerCount:
                {
                    await s.WaitAttachedAsync(AttachWait, ct).ConfigureAwait(false);
                    s.Enumerate(_leases.ViewFor(s.Name ?? "", _devices()));
                    var reply = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(reply, (uint)s.ViewCount);
                    await s.SendToClientAsync(0, packet, reply).ConfigureAwait(false);
                    break;
                }
                case OpenRgbProtocol.PacketId.SetClientFlags:
                    s.Flags = body;
                    await s.SendUpstreamAsync(dev, packet, body, ct).ConfigureAwait(false);
                    break;
                default:
                    if (IsDeviceScoped(packet) && s.TryUpstreamIndex(dev, out var upstreamIndex))
                    {
                        await s.SendUpstreamAsync(upstreamIndex, packet, body, ct).ConfigureAwait(false);
                    }
                    break;
            }
        }
    }

    /// <summary>Daemon to client: replies and notices go back with the device index translated to the client's view.</summary>
    private async Task PumpUpstreamAsync(Session s, Stream upstream, CancellationToken ct)
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        while (!ct.IsCancellationRequested)
        {
            await upstream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var (dev, packet, size) = OpenRgbProtocol.ReadHeader(header);
            if (size > MaxPacketBytes)
            {
                throw new InvalidOperationException("oversized packet");
            }
            var body = new byte[size];
            await upstream.ReadExactlyAsync(body, ct).ConfigureAwait(false);

            if (packet == OpenRgbProtocol.PacketId.RequestProtocolVersion && body.Length >= 4)
            {
                var server = BinaryPrimitives.ReadUInt32LittleEndian(body);
                _serverVersion = server;
                if (s.TakeVersionAnswer())
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(body, Math.Min(server, MaxProtocolVersion));
                    await s.SendToClientAsync(dev, packet, body).ConfigureAwait(false);
                }
            }
            else if (IsDeviceScoped(packet))
            {
                if (s.TryViewIndex(dev, out var viewIndex))
                {
                    await s.SendToClientAsync(viewIndex, packet, body).ConfigureAwait(false);
                }
            }
            else
            {
                await s.SendToClientAsync(dev, packet, body).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsDeviceScoped(OpenRgbProtocol.PacketId packet) =>
        packet == OpenRgbProtocol.PacketId.RequestControllerData || ((uint)packet >= 1000 && (uint)packet < 2000);

    internal sealed class Session
    {
        private readonly SemaphoreSlim _clientWrite = new(1, 1);
        private readonly SemaphoreSlim _upstreamWrite = new(1, 1);
        private volatile int[] _view = Array.Empty<int>();

        private readonly object _sync = new();
        private Stream? _upstream;
        private bool _versionAsked;
        private bool _versionOwed;
        private bool _enumerated;
        private TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Session(Stream client)
        {
            Client = client;
        }

        public Stream Client { get; }
        public string? Name { get; set; }
        public byte[]? Flags { get; set; }
        public uint ClientVersion { get; private set; }
        public int ViewCount => _view.Length;

        /// <summary>The daemon is joined: what of the client's handshake has to be repeated to it.</summary>
        public (bool AskVersion, string? Name, byte[]? Flags, bool Enumerated) Attach(Stream upstream)
        {
            lock (_sync)
            {
                _upstream = upstream;
                _attached.TrySetResult();
                return (_versionAsked, Name, Flags, _enumerated);
            }
        }

        /// <summary>The daemon is gone; true when the client had a list it must read again.</summary>
        public bool Detach()
        {
            lock (_sync)
            {
                var had = _upstream is not null;
                _upstream = null;
                if (had)
                {
                    _attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                _view = Array.Empty<int>();
                return had && _enumerated;
            }
        }

        /// <summary>Waits for the daemon link, no longer than <paramref name="limit"/>.</summary>
        public async Task WaitAttachedAsync(TimeSpan limit, CancellationToken ct)
        {
            Task attached;
            lock (_sync)
            {
                attached = _attached.Task;
            }
            if (!attached.IsCompleted)
            {
                await Task.WhenAny(attached, Task.Delay(limit, ct)).ConfigureAwait(false);
            }
        }

        /// <summary>The client asked for the protocol version; true when the daemon is there to answer it.</summary>
        public bool AskVersion(uint version, bool answerKnown)
        {
            lock (_sync)
            {
                ClientVersion = version;
                _versionAsked = true;
                _versionOwed = _upstream is not null || !answerKnown;
                return _upstream is not null;
            }
        }

        /// <summary>A version answer from the daemon; true when the client still waits for one.</summary>
        public bool TakeVersionAnswer()
        {
            lock (_sync)
            {
                var owed = _versionOwed;
                _versionOwed = false;
                return owed;
            }
        }

        /// <summary>The client reads its device list: the leased devices while the daemon is joined, none otherwise.</summary>
        public void Enumerate(IReadOnlyList<RgbDevice> devices)
        {
            lock (_sync)
            {
                _enumerated = true;
                if (_upstream is null)
                {
                    _view = Array.Empty<int>();
                    return;
                }
                var view = new int[devices.Count];
                for (var i = 0; i < view.Length; i++)
                {
                    view[i] = devices[i].Index;
                }
                _view = view;
            }
        }

        public bool TryUpstreamIndex(uint viewIndex, out uint upstreamIndex)
        {
            var view = _view;
            upstreamIndex = viewIndex < view.Length ? (uint)view[viewIndex] : 0;
            return viewIndex < view.Length;
        }

        public bool TryViewIndex(uint upstreamIndex, out uint viewIndex)
        {
            var view = _view;
            for (var i = 0; i < view.Length; i++)
            {
                if (view[i] == upstreamIndex)
                {
                    viewIndex = (uint)i;
                    return true;
                }
            }
            viewIndex = 0;
            return false;
        }

        /// <summary>Sends to the daemon; a packet for a daemon that is not joined is dropped.</summary>
        public Task SendUpstreamAsync(uint dev, OpenRgbProtocol.PacketId packet, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            Stream? upstream;
            lock (_sync)
            {
                upstream = _upstream;
            }
            return upstream is null ? Task.CompletedTask : SendAsync(upstream, _upstreamWrite, dev, packet, body, ct);
        }

        public Task SendToClientAsync(uint dev, OpenRgbProtocol.PacketId packet, ReadOnlyMemory<byte> body) =>
            SendAsync(Client, _clientWrite, dev, packet, body, CancellationToken.None);

        private static async Task SendAsync(Stream stream, SemaphoreSlim gate, uint dev, OpenRgbProtocol.PacketId packet,
            ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            var buffer = new byte[OpenRgbProtocol.HeaderSize + body.Length];
            OpenRgbProtocol.WriteHeader(buffer, dev, packet, (uint)body.Length);
            body.CopyTo(buffer.AsMemory(OpenRgbProtocol.HeaderSize));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
