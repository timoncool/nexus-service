using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read tool: every panel with its accent source and monitoring widget slots, plus the lighting cards an accent can follow.</summary>
public sealed class GetPanelsTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;
    private readonly ILightingDeviceProvider _lighting;

    public GetPanelsTool(PanelDeviceRegistry registry, ILightingDeviceProvider lighting)
    {
        _registry = registry;
        _lighting = lighting;
    }

    public string Name => "get_panels";
    public string Title => "Get Panels";

    public string Description =>
        "Lists every Nexus panel (fan LCDs, cooler screens, displays) with its id, name, accent settings " +
        "(accentLightingDevice = lighting card whose live LED colour is the accent) and the config of each " +
        "monitoring widget (slotN_design, slotN_device, slotN_sensor, slotN_valueScale/unitScale/labelScale/strokeScale, ...). " +
        "Also lists the lighting card ids an accent can follow. Call before set_panel_style.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => true;

    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var panels = new JsonArray();
        foreach (var record in _registry.List())
        {
            var widgets = new JsonArray();
            foreach (var page in record.Layout?.Pages ?? new List<PanelPageDto>())
            {
                foreach (var widget in page.Widgets)
                {
                    widgets.Add(new JsonObject
                    {
                        ["id"] = widget.Id,
                        ["type"] = widget.Type,
                        ["size"] = widget.Size,
                        ["config"] = ConfigNode(widget.Config),
                    });
                }
            }
            panels.Add(new JsonObject
            {
                ["id"] = record.Id,
                ["name"] = record.DisplayName,
                ["surface"] = record.Layout?.Surface,
                ["accentSyncWithDesktop"] = record.AccentSyncWithDesktop,
                ["accentColor"] = record.AccentColor,
                ["accentLightingDevice"] = record.AccentLightingDevice,
                ["widgets"] = widgets,
            });
        }

        var cards = new JsonArray();
        foreach (var device in _lighting.GetAll().Devices)
        {
            cards.Add(new JsonObject { ["id"] = device.Id, ["name"] = device.Name, ["ledCount"] = device.LedCount });
        }

        var result = new JsonObject { ["panels"] = panels, ["lightingCards"] = cards };
        return Task.FromResult(McpToolExecutionResult.Ok(result.ToJsonString()));
    }

    private static JsonObject ConfigNode(Dictionary<string, JsonElement>? config)
    {
        var node = new JsonObject();
        if (config is null) return node;
        foreach (var (key, value) in config)
        {
            node[key] = JsonNode.Parse(value.GetRawText());
        }
        return node;
    }
}

/// <summary>Write tool: a panel's accent source and its monitoring widget slot config, the same fields the panel editor writes.</summary>
public sealed class SetPanelStyleTool : IMcpTool
{
    private static readonly string[] ScaleKeys = { "valueScale", "unitScale", "labelScale", "strokeScale" };
    private const double MinScale = 0.5;
    private const double MaxScale = 2.5;

    private readonly PanelDeviceRegistry _registry;
    private readonly MultiplexHub _hub;

    public SetPanelStyleTool(PanelDeviceRegistry registry, MultiplexHub hub)
    {
        _registry = registry;
        _hub = hub;
    }

    public string Name => "set_panel_style";
    public string Title => "Set Panel Style";

    public string Description =>
        "Styles one panel, or every panel with panel=\"all\". accentLightingDevice takes a lighting card id from " +
        "get_panels so the panel accent follows that card's live LED colour (\"\" turns it off). The other fields edit " +
        "the panel's monitoring widgets at the given slot (default 0): design (e.g. caterpillar, arc270, text), device + " +
        "sensor (as get_panels shows them), and sizes valueScale / unitScale / labelScale / strokeScale from 0.5 to 2.5 " +
        "(1 resets). config sets any other widget config key directly; a null value removes the key.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"panel\":{\"type\":\"string\",\"description\":\"Panel id or name from get_panels, or \\\"all\\\".\"}," +
        "\"accentLightingDevice\":{\"type\":\"string\"}," +
        "\"slot\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":3}," +
        "\"design\":{\"type\":\"string\"}," +
        "\"device\":{\"type\":\"string\"}," +
        "\"sensor\":{\"type\":\"string\"}," +
        "\"valueScale\":{\"type\":\"number\",\"minimum\":0.5,\"maximum\":2.5}," +
        "\"unitScale\":{\"type\":\"number\",\"minimum\":0.5,\"maximum\":2.5}," +
        "\"labelScale\":{\"type\":\"number\",\"minimum\":0.5,\"maximum\":2.5}," +
        "\"strokeScale\":{\"type\":\"number\",\"minimum\":0.5,\"maximum\":2.5}," +
        "\"config\":{\"type\":\"object\"}" +
        "},\"required\":[\"panel\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var panelArg = McpArgs.StringArg(args, "panel");
        if (string.IsNullOrWhiteSpace(panelArg))
        {
            return Task.FromResult(McpToolExecutionResult.Error("'panel' is required: an id or name from get_panels, or \"all\"."));
        }

        var targets = new List<PanelDeviceRecord>();
        foreach (var record in _registry.List())
        {
            if (panelArg == "all"
                || string.Equals(record.Id, panelArg, StringComparison.Ordinal)
                || string.Equals(record.DisplayName, panelArg, StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(record);
            }
        }
        if (targets.Count == 0)
        {
            return Task.FromResult(McpToolExecutionResult.Error($"No panel matches '{panelArg}'. Call get_panels for ids and names."));
        }

        if (!TryBuildWidgetEdits(args, out var edits, out var error))
        {
            return Task.FromResult(McpToolExecutionResult.Error(error));
        }
        var accent = McpArgs.StringArg(args, "accentLightingDevice");

        var changed = new JsonArray();
        foreach (var record in targets)
        {
            var patch = new PanelDevicePatch { AccentLightingDevice = accent };
            var widgetsEdited = 0;
            if (edits.Count > 0 && record.Layout is not null)
            {
                widgetsEdited = ApplyToMonitoringWidgets(record.Layout, edits);
                if (widgetsEdited > 0) patch.Layout = record.Layout;
            }
            if (patch.AccentLightingDevice is null && patch.Layout is null) continue;
            if (_registry.Patch(record.Id, patch) is null) continue;
            PanelTopics.BroadcastPanelDevice(_hub, record.Id);
            changed.Add(new JsonObject { ["id"] = record.Id, ["name"] = record.DisplayName, ["monitoringWidgetsEdited"] = widgetsEdited });
        }

        return Task.FromResult(McpToolExecutionResult.Ok(new JsonObject { ["changed"] = changed }.ToJsonString()));
    }

    /// <summary>Config key -> new value; null removes the key.</summary>
    private static bool TryBuildWidgetEdits(JsonElement? args, out Dictionary<string, JsonElement?> edits, out string error)
    {
        edits = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        error = "";
        var slot = McpArgs.IntArg(args, "slot") ?? 0;
        if (slot is < 0 or > 3)
        {
            error = "'slot' must be 0 to 3.";
            return false;
        }
        foreach (var key in new[] { "design", "device", "sensor" })
        {
            var value = McpArgs.StringArg(args, key);
            if (value is not null) edits[$"slot{slot}_{key}"] = StringElement(value);
        }
        foreach (var key in ScaleKeys)
        {
            var value = McpArgs.NumberArg(args, key);
            if (value is null) continue;
            if (!double.IsFinite(value.Value) || value.Value < MinScale || value.Value > MaxScale)
            {
                error = $"'{key}' must be between {MinScale} and {MaxScale}.";
                return false;
            }
            edits[$"slot{slot}_{key}"] = Math.Abs(value.Value - 1) < 0.001 ? null : NumberElement(value.Value);
        }
        if (args is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("config", out var config))
        {
            if (config.ValueKind != JsonValueKind.Object)
            {
                error = "'config' must be an object of widget config keys.";
                return false;
            }
            foreach (var prop in config.EnumerateObject())
            {
                edits[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.Clone();
            }
        }
        return true;
    }

    private static int ApplyToMonitoringWidgets(PanelLayoutDto layout, Dictionary<string, JsonElement?> edits)
    {
        var count = 0;
        foreach (var page in layout.Pages)
        {
            foreach (var widget in page.Widgets)
            {
                if (!string.Equals(widget.Type, "monitoring", StringComparison.Ordinal)) continue;
                widget.Config = Apply(widget.Config, edits);
                count++;
            }
        }
        if (layout.SingleWidgetConfigs is { } single && single.TryGetValue("monitoring", out var monitoring))
        {
            single["monitoring"] = Apply(monitoring, edits);
        }
        return count;
    }

    private static Dictionary<string, JsonElement> Apply(Dictionary<string, JsonElement>? config, Dictionary<string, JsonElement?> edits)
    {
        var next = config is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) : new Dictionary<string, JsonElement>(config, StringComparer.Ordinal);
        foreach (var (key, value) in edits)
        {
            if (value is null) next.Remove(key);
            else next[key] = value.Value;
        }
        return next;
    }

    private static JsonElement StringElement(string value)
    {
        using var doc = JsonDocument.Parse(JsonValue.Create(value).ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonElement NumberElement(double value)
    {
        using var doc = JsonDocument.Parse(value.ToString("0.###", CultureInfo.InvariantCulture));
        return doc.RootElement.Clone();
    }
}
