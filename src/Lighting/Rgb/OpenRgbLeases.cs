using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>An SDK client connected to the broker.</summary>
public sealed record OpenRgbBrokerClient(string Name, DateTime ConnectedAtUtc);

/// <summary>
/// Who drives each OpenRGB device: Nexus, or the SDK client it is leased to through the broker
/// (DRM-lease style: the lessee sees only what it leases, the rest does not exist for it). A lease
/// is live only while its client is connected; when the client goes, the device is Nexus's again.
/// </summary>
public sealed class OpenRgbLeases
{
    private const int MaxKnownClients = 32;

    private readonly IConfigStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<long, OpenRgbBrokerClient> _sessions = new();
    private volatile HashSet<string> _active = new(StringComparer.Ordinal);

    public OpenRgbLeases(IConfigStore store)
    {
        _store = store;
    }

    /// <summary>StableIds whose lease went live (a client took them) or ended (they are Nexus's again).</summary>
    public event Action<IReadOnlyCollection<string> /*taken*/, IReadOnlyCollection<string> /*released*/>? ActiveChanged;

    /// <summary>Client names whose visible device set changed, so a connected one re-enumerates.</summary>
    public event Action<IReadOnlyCollection<string>>? ViewChanged;

    /// <summary>StableIds leased to a connected client right now; Nexus writes nothing to them.</summary>
    public IReadOnlySet<string> Active => _active;

    public IReadOnlyDictionary<string, string> Leases => _store.Load().Devices.OpenRgbLeases;

    public IReadOnlyList<string> KnownClients => _store.Load().Devices.OpenRgbKnownClients;

    public IReadOnlyList<OpenRgbBrokerClient> Connected
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values.ToList();
            }
        }
    }

    public string? OwnerOf(string stableId) =>
        Leases.TryGetValue(stableId, out var client) ? client : null;

    /// <summary>The devices a client sees: the given daemon list filtered to those leased to it, in daemon order.</summary>
    public IReadOnlyList<RgbDevice> ViewFor(string clientName, IReadOnlyList<RgbDevice> devices)
    {
        var leases = Leases;
        var view = new List<RgbDevice>();
        foreach (var d in devices.OrderBy(d => d.Index))
        {
            if (leases.TryGetValue(d.StableId, out var owner) && string.Equals(owner, clientName, StringComparison.Ordinal))
            {
                view.Add(d);
            }
        }
        return view;
    }

    public void ClientConnected(long session, string name)
    {
        lock (_gate)
        {
            _sessions[session] = new OpenRgbBrokerClient(name, DateTime.UtcNow);
        }
        if (!KnownClients.Contains(name, StringComparer.Ordinal))
        {
            _store.Update(s =>
            {
                var known = s.Devices.OpenRgbKnownClients.Where(c => c != name).Append(name).ToList();
                s.Devices.OpenRgbKnownClients = known.Skip(Math.Max(0, known.Count - MaxKnownClients)).ToList();
            });
        }
        Recompute();
    }

    public void ClientDisconnected(long session)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(session))
            {
                return;
            }
        }
        Recompute();
    }

    /// <summary>Leases a device to a client, or gives it back to Nexus with null.</summary>
    public void SetOwner(string stableId, string? clientName)
    {
        var before = OwnerOf(stableId);
        _store.Update(s =>
        {
            var leases = new Dictionary<string, string>(s.Devices.OpenRgbLeases, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(clientName))
            {
                leases.Remove(stableId);
            }
            else
            {
                leases[stableId] = clientName;
            }
            s.Devices.OpenRgbLeases = leases;
        });
        Recompute();
        if (before == clientName)
        {
            return;
        }
        var changed = new List<string>();
        if (!string.IsNullOrEmpty(before)) changed.Add(before);
        if (!string.IsNullOrEmpty(clientName)) changed.Add(clientName);
        ViewChanged?.Invoke(changed);
    }

    private void Recompute()
    {
        HashSet<string> connected;
        lock (_gate)
        {
            connected = _sessions.Values.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        }
        var next = Leases.Where(kv => connected.Contains(kv.Value)).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var prev = _active;
        _active = next;
        var taken = next.Where(id => !prev.Contains(id)).ToList();
        var released = prev.Where(id => !next.Contains(id)).ToList();
        if (taken.Count > 0 || released.Count > 0)
        {
            ActiveChanged?.Invoke(taken, released);
        }
    }
}
