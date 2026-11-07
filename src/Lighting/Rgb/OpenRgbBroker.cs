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
    }

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

    private async Task RunSessionAsync(long id, TcpClient client, CancellationToken ct)
    {
        using var clientConn = client;
        using var upstream = new TcpClient();
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Session? session = null;
        try
        {
            await upstream.ConnectAsync(IPAddress.Loopback, _daemonPort, sessionCts.Token).ConfigureAwait(false);
            session = new Session(clientConn.GetStream(), upstream.GetStream());
            lock (_gate)
            {
                _sessions[id] = session;
            }
            var down = PumpUpstreamAsync(session, sessionCts.Token);
            var up = PumpClientAsync(id, session, sessionCts.Token);
            await Task.WhenAny(down, up).ConfigureAwait(false);
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
                    s.ClientVersion = Math.Min(asked, MaxProtocolVersion);
                    await s.SendUpstreamAsync(dev, packet, OpenRgbProtocol.BuildProtocolVersionBody(s.ClientVersion), ct).ConfigureAwait(false);
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
                    s.SetView(_leases.ViewFor(s.Name ?? "", _devices()));
                    var reply = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(reply, (uint)s.ViewCount);
                    await s.SendToClientAsync(0, packet, reply).ConfigureAwait(false);
                    break;
                }
                case OpenRgbProtocol.PacketId.SetClientFlags:
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
    private static async Task PumpUpstreamAsync(Session s, CancellationToken ct)
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        while (!ct.IsCancellationRequested)
        {
            await s.Upstream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var (dev, packet, size) = OpenRgbProtocol.ReadHeader(header);
            if (size > MaxPacketBytes)
            {
                throw new InvalidOperationException("oversized packet");
            }
            var body = new byte[size];
            await s.Upstream.ReadExactlyAsync(body, ct).ConfigureAwait(false);

            if (packet == OpenRgbProtocol.PacketId.RequestProtocolVersion && body.Length >= 4)
            {
                var server = BinaryPrimitives.ReadUInt32LittleEndian(body);
                BinaryPrimitives.WriteUInt32LittleEndian(body, Math.Min(server, MaxProtocolVersion));
                await s.SendToClientAsync(dev, packet, body).ConfigureAwait(false);
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

        public Session(Stream client, Stream upstream)
        {
            Client = client;
            Upstream = upstream;
        }

        public Stream Client { get; }
        public Stream Upstream { get; }
        public string? Name { get; set; }
        public uint ClientVersion { get; set; }
        public int ViewCount => _view.Length;

        public void SetView(IReadOnlyList<RgbDevice> devices)
        {
            var view = new int[devices.Count];
            for (var i = 0; i < view.Length; i++)
            {
                view[i] = devices[i].Index;
            }
            _view = view;
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

        public Task SendUpstreamAsync(uint dev, OpenRgbProtocol.PacketId packet, ReadOnlyMemory<byte> body, CancellationToken ct) =>
            SendAsync(Upstream, _upstreamWrite, dev, packet, body, ct);

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
