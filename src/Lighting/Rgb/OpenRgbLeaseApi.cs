using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>What the routes and the MCP tools say about device owners, and how they change one.</summary>
public static class OpenRgbLeaseApi
{
    public const string NexusOwner = "Nexus";

    /// <summary>The OpenRGB device a lighting card id or StableId names: a card id is the StableId, or the StableId plus "-zone".</summary>
    public static RgbDevice? Resolve(string id, RgbBridge bridge) =>
        bridge.Devices
            .Where(d => id == d.StableId || id.StartsWith(d.StableId + "-", StringComparison.Ordinal))
            .OrderByDescending(d => d.StableId.Length)
            .FirstOrDefault();

    public static JsonObject Snapshot(OpenRgbLeases leases, RgbBridge bridge, bool brokerOn)
    {
        var connected = leases.Connected;
        var clients = new JsonArray();
        foreach (var name in leases.KnownClients.Union(connected.Select(c => c.Name)))
        {
            var live = connected.FirstOrDefault(c => c.Name == name);
            clients.Add(new JsonObject
            {
                ["name"] = name,
                ["connected"] = live is not null,
                ["connectedAt"] = live?.ConnectedAtUtc.ToString("O"),
            });
        }

        var active = leases.Active;
        var devices = new JsonArray();
        foreach (var d in bridge.Devices.OrderBy(d => d.Index))
        {
            var owner = leases.OwnerOf(d.StableId);
            devices.Add(new JsonObject
            {
                ["id"] = d.StableId,
                ["name"] = d.Name,
                ["owner"] = owner ?? NexusOwner,
                ["leaseLive"] = owner is not null && active.Contains(d.StableId),
            });
        }

        return new JsonObject { ["broker"] = brokerOn, ["clients"] = clients, ["devices"] = devices };
    }

    /// <summary>Gives a device to a client by name, or back to Nexus for null, "" or "Nexus". False when no OpenRGB device has that id.</summary>
    public static bool SetOwner(string id, string? owner, OpenRgbLeases leases, RgbBridge bridge)
    {
        if (Resolve(id, bridge) is not { } device)
        {
            return false;
        }
        var client = string.IsNullOrWhiteSpace(owner) || owner == NexusOwner ? null : owner;
        leases.SetOwner(device.StableId, client);
        return true;
    }
}
