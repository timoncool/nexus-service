using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class OpenRgbBrokerTests
{
    private sealed class MemoryStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    /// <summary>Daemon stand-in: answers protocol version 6 and controller data with the index it was asked for, records every packet.</summary>
    private sealed class FakeDaemon : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public ConcurrentQueue<(uint Dev, OpenRgbProtocol.PacketId Packet)> Received { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeDaemon()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(c));
            }
        }

        private async Task ServeAsync(TcpClient c)
        {
            using var _ = c;
            var s = c.GetStream();
            var header = new byte[OpenRgbProtocol.HeaderSize];
            try
            {
                while (true)
                {
                    await s.ReadExactlyAsync(header);
                    var (dev, packet, size) = OpenRgbProtocol.ReadHeader(header);
                    var body = new byte[size];
                    await s.ReadExactlyAsync(body);
                    Received.Enqueue((dev, packet));
                    if (packet == OpenRgbProtocol.PacketId.RequestProtocolVersion)
                    {
                        await WriteAsync(s, 0, packet, OpenRgbProtocol.BuildProtocolVersionBody(6));
                    }
                    else if (packet == OpenRgbProtocol.PacketId.RequestControllerData)
                    {
                        await WriteAsync(s, dev, packet, new[] { (byte)dev });
                    }
                }
            }
            catch { }
        }

        public void Dispose() => _listener.Stop();
    }

    private static async Task WriteAsync(System.IO.Stream s, uint dev, OpenRgbProtocol.PacketId packet, byte[] body)
    {
        var buffer = new byte[OpenRgbProtocol.HeaderSize + body.Length];
        OpenRgbProtocol.WriteHeader(buffer, dev, packet, (uint)body.Length);
        body.CopyTo(buffer, OpenRgbProtocol.HeaderSize);
        await s.WriteAsync(buffer);
    }

    private static async Task<(uint Dev, OpenRgbProtocol.PacketId Packet, byte[] Body)> ReadAsync(System.IO.Stream s)
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await s.ReadExactlyAsync(header, cts.Token);
        var (dev, packet, size) = OpenRgbProtocol.ReadHeader(header);
        var body = new byte[size];
        await s.ReadExactlyAsync(body, cts.Token);
        return (dev, packet, body);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task A_client_sees_and_drives_only_its_leased_device_and_the_lease_ends_with_the_connection()
    {
        var store = new MemoryStore();
        var leases = new OpenRgbLeases(store);
        var devices = new List<RgbDevice>
        {
            new() { Index = 0, Name = "RAM", Serial = "ram" },
            new() { Index = 1, Name = "Mouse", Serial = "mouse" },
            new() { Index = 2, Name = "Board", Serial = "board" },
        };
        leases.SetOwner("openrgb-s-mouse", "Aurora (RGB.NET)");
        using var daemon = new FakeDaemon();
        var listenPort = FreePort();
        var broker = new OpenRgbBroker(leases, () => devices, daemon.Port, listenPort, enabled: true);
        using var cts = new CancellationTokenSource();
        await broker.StartAsync(cts.Token);
        await broker.Listening.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, listenPort);
            var s = client.GetStream();

            await WriteAsync(s, 0, OpenRgbProtocol.PacketId.RequestProtocolVersion, OpenRgbProtocol.BuildProtocolVersionBody(6));
            var version = await ReadAsync(s);
            Assert.Equal(OpenRgbBroker.MaxProtocolVersion, BinaryPrimitives.ReadUInt32LittleEndian(version.Body));

            await WriteAsync(s, 0, OpenRgbProtocol.PacketId.SetClientName, OpenRgbProtocol.BuildSetClientNameBody("Aurora (RGB.NET)"));
            await WriteAsync(s, 0, OpenRgbProtocol.PacketId.RequestControllerCount, Array.Empty<byte>());
            var count = await ReadAsync(s);
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(count.Body));

            await WriteAsync(s, 0, OpenRgbProtocol.PacketId.RequestControllerData, OpenRgbProtocol.BuildRequestControllerDataBody(5));
            var data = await ReadAsync(s);
            Assert.Equal(0u, data.Dev);
            Assert.Equal(1, data.Body[0]);

            await WriteAsync(s, 0, OpenRgbProtocol.PacketId.RgbControllerUpdateLeds, new byte[] { 1, 2, 3 });
            await WriteAsync(s, 1, OpenRgbProtocol.PacketId.RgbControllerUpdateLeds, new byte[] { 1, 2, 3 });
            await WriteAsync(s, 0, OpenRgbProtocol.PacketId.SetCustomMode, Array.Empty<byte>());
            await WriteAsync(s, 0, OpenRgbProtocol.PacketId.RequestRescanDevices, Array.Empty<byte>());

            await WaitUntilAsync(() => daemon.Received.Count >= 5);
            await Task.Delay(100);
            Assert.True(leases.Active.Contains("openrgb-s-mouse"));
        }

        var got = daemon.Received.ToArray();
        Assert.DoesNotContain(got, p => p.Packet == OpenRgbProtocol.PacketId.RequestControllerCount);
        Assert.DoesNotContain(got, p => p.Packet == OpenRgbProtocol.PacketId.RequestRescanDevices);
        Assert.Contains(got, p => p.Packet == OpenRgbProtocol.PacketId.RequestControllerData && p.Dev == 1);
        Assert.Single(got, p => p.Packet == OpenRgbProtocol.PacketId.RgbControllerUpdateLeds);
        Assert.Contains(got, p => p.Packet == OpenRgbProtocol.PacketId.RgbControllerUpdateLeds && p.Dev == 1);
        Assert.Contains(got, p => p.Packet == OpenRgbProtocol.PacketId.SetCustomMode && p.Dev == 1);
        Assert.All(got, p => Assert.True(!OpenRgbBroker.IsDeviceScoped(p.Packet) || p.Dev == 1, $"{p.Packet} reached device {p.Dev}"));

        await WaitUntilAsync(() => !leases.Active.Contains("openrgb-s-mouse"));
        Assert.Contains("Aurora (RGB.NET)", leases.KnownClients);
        cts.Cancel();
        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_unnamed_or_unleased_client_sees_no_devices()
    {
        var store = new MemoryStore();
        var leases = new OpenRgbLeases(store);
        var devices = new List<RgbDevice> { new() { Index = 0, Name = "RAM", Serial = "ram" } };
        using var daemon = new FakeDaemon();
        var listenPort = FreePort();
        var broker = new OpenRgbBroker(leases, () => devices, daemon.Port, listenPort, enabled: true);
        using var cts = new CancellationTokenSource();
        await broker.StartAsync(cts.Token);
        await broker.Listening.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        var s = client.GetStream();
        await WriteAsync(s, 0, OpenRgbProtocol.PacketId.SetClientName, OpenRgbProtocol.BuildSetClientNameBody("Artemis"));
        await WriteAsync(s, 0, OpenRgbProtocol.PacketId.RequestControllerCount, Array.Empty<byte>());
        var count = await ReadAsync(s);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(count.Body));
        Assert.Empty(leases.Active);

        cts.Cancel();
        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Leasing_to_a_connected_client_makes_the_lease_live_and_giving_it_back_releases_it()
    {
        var leases = new OpenRgbLeases(new MemoryStore());
        var released = new List<string>();
        leases.ActiveChanged += (_, rel) => released.AddRange(rel);
        leases.ClientConnected(1, "Aurora (RGB.NET)");

        leases.SetOwner("openrgb-s-mouse", "Aurora (RGB.NET)");
        Assert.Contains("openrgb-s-mouse", leases.Active);

        leases.SetOwner("openrgb-s-mouse", null);
        Assert.Empty(leases.Active);
        Assert.Equal(new[] { "openrgb-s-mouse" }, released);
        Assert.Null(leases.OwnerOf("openrgb-s-mouse"));
    }
}
