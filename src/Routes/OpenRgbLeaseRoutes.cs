using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class OpenRgbOwnerRequest
{
    public string? DeviceId { get; set; }
    public string? Owner { get; set; }
}

public static class OpenRgbLeaseRoutes
{
    public static void MapOpenRgbLeaseEndpoints(this WebApplication app)
    {
        // GET /devices/openrgb-owners - SDK clients the broker knows (connected or not) and who owns each OpenRGB device.
        app.MapGet("/devices/openrgb-owners", (OpenRgbLeases leases, RgbBridge bridge, IConfigStore store) =>
            Results.Content(OpenRgbLeaseApi.Snapshot(leases, bridge, store.Load().Devices.OpenRgbBroker).ToJsonString(), "application/json"));

        // PUT /devices/openrgb-owners - lease a device (card id or StableId) to an SDK client by name, or give it back to Nexus.
        app.MapPut("/devices/openrgb-owners", (OpenRgbOwnerRequest body, OpenRgbLeases leases, RgbBridge bridge,
            Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            if (string.IsNullOrEmpty(body.DeviceId))
            {
                return Results.BadRequest(ApiResponse.Fail("deviceId is required"));
            }
            if (!OpenRgbLeaseApi.SetOwner(body.DeviceId, body.Owner, leases, bridge))
            {
                return Results.NotFound(ApiResponse.Fail("no OpenRGB device with that id"));
            }
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}
