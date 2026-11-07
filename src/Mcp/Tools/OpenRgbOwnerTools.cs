using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read tool: the OpenRGB SDK apps the broker knows and who owns each OpenRGB device.</summary>
public sealed class GetDeviceOwnersTool : IMcpTool
{
    private readonly OpenRgbLeases _leases;
    private readonly RgbBridge _bridge;
    private readonly IConfigStore _store;

    public GetDeviceOwnersTool(OpenRgbLeases leases, RgbBridge bridge, IConfigStore store)
    {
        _leases = leases;
        _bridge = bridge;
        _store = store;
    }

    public string Name => "get_device_owners";
    public string Title => "Get Device Owners";

    public string Description =>
        "Lists the apps that connect to Nexus over the OpenRGB SDK (Aurora, Artemis, SignalRGB, ...), whether each is " +
        "connected now, and every OpenRGB device with its owner: \"Nexus\" or the app it is leased to. An app sees and " +
        "drives only the devices leased to it, and only while it is connected; otherwise Nexus drives them. Call before set_device_owner.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => true;

    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct) =>
        Task.FromResult(McpToolExecutionResult.Ok(
            OpenRgbLeaseApi.Snapshot(_leases, _bridge, _store.Load().Devices.OpenRgbBroker).ToJsonString()));
}

/// <summary>Write tool: leases an OpenRGB device to an SDK app, or gives it back to Nexus.</summary>
public sealed class SetDeviceOwnerTool : IMcpTool
{
    private readonly OpenRgbLeases _leases;
    private readonly RgbBridge _bridge;
    private readonly MultiplexHub _hub;

    public SetDeviceOwnerTool(OpenRgbLeases leases, RgbBridge bridge, MultiplexHub hub)
    {
        _leases = leases;
        _bridge = bridge;
        _hub = hub;
    }

    public string Name => "set_device_owner";
    public string Title => "Set Device Owner";

    public string Description =>
        "Gives one OpenRGB device to an app connecting over the OpenRGB SDK, by the name get_device_owners lists " +
        "(e.g. \"Aurora (RGB.NET)\"), or back to Nexus with owner \"Nexus\". device is a device id from get_device_owners " +
        "or a lighting card id. The app sees the device at once if connected; while it is not, Nexus drives the device.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"device\":{\"type\":\"string\",\"description\":\"Device id from get_device_owners or a lighting card id.\"}," +
        "\"owner\":{\"type\":\"string\",\"description\":\"App name from get_device_owners, or \\\"Nexus\\\".\"}" +
        "},\"required\":[\"device\",\"owner\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        if (args is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("device", out var deviceEl) || deviceEl.GetString() is not { Length: > 0 } device
            || !obj.TryGetProperty("owner", out var ownerEl))
        {
            return Task.FromResult(McpToolExecutionResult.Error("device and owner are required"));
        }
        var owner = ownerEl.GetString();
        if (!OpenRgbLeaseApi.SetOwner(device, owner, _leases, _bridge))
        {
            return Task.FromResult(McpToolExecutionResult.Error($"no OpenRGB device with id '{device}'; call get_device_owners"));
        }
        PanelTopics.BroadcastLighting(_hub);
        var who = string.IsNullOrWhiteSpace(owner) ? OpenRgbLeaseApi.NexusOwner : owner;
        return Task.FromResult(McpToolExecutionResult.Ok($"{device} now belongs to {who}"));
    }
}
