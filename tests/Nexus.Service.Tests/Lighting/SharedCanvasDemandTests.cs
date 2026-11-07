using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// The shared canvas renders only while something reads it: a driven device without a look of its own, or a
/// client watching the canvas. ShaderEffect.TestRender stands in for the GL draw and counts the renders.
/// </summary>
public class SharedCanvasDemandTests
{
    private sealed class Fill : IEffect
    {
        public Fill(string name) { Name = name; }
        public string Name { get; }
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (var y = 0; y < canvas.Height; y++)
                for (var x = 0; x < canvas.Width; x++)
                    canvas.SetPixel(x, y, 255, 0, 0);
        }
        public void Dispose() { }
    }

    private static GpuContext Context()
    {
        var gpu = new GpuContext(16, 9, null, () => { });
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(5)));
        return gpu;
    }

    private static ShaderEffect CountingShader(GpuContext gpu, Action onRender) =>
        new("kaleidoscope", gpu, "unused")
        {
            TestRender = (canvas, _) =>
            {
                onRender();
                for (var y = 0; y < canvas.Height; y++)
                    for (var x = 0; x < canvas.Width; x++)
                        canvas.SetPixel(x, y, 0, 255, 0);
                return true;
            },
        };

    private static DeviceFrame Device(int index, string id) =>
        new(index, id, 5, x: 300, y: 100, w: 40, h: 250, rotation: 90);

    private static async Task Frames(LightingEngine engine, int count)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;
        void Handler()
        {
            if (Interlocked.Increment(ref seen) >= count) done.TrySetResult();
        }
        engine.FramePublished += Handler;
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { engine.FramePublished -= Handler; }
    }

    private static (LightingEngine Engine, Func<int> Renders) Rig(GpuContext gpu, DevicePinTracker pins, params DeviceFrame[] devices)
    {
        var renders = 0;
        var engine = new LightingEngine
        {
            Pins = pins,
            PinEffectFactory = (_, _) => new Fill("red"),
            PinLookTag = _ => "t",
            FrameIntervalMs = 10,
            CanvasWatched = () => false,
        };
        engine.UpdateDevices(devices);
        engine.SetEffect(CountingShader(gpu, () => Interlocked.Increment(ref renders)));
        return (engine, () => Volatile.Read(ref renders));
    }

    private static async Task<int> RendersOver(LightingEngine engine, Func<int> renders, int frames)
    {
        await Frames(engine, 3);
        var before = renders();
        await Frames(engine, frames);
        return renders() - before;
    }

    [Fact]
    public async Task Every_device_on_its_own_look_and_nobody_watching_skips_the_shared_render()
    {
        using var gpu = Context();
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        var dev = Device(0, "dev-a");
        var (engine, renders) = Rig(gpu, pins, dev);
        using (engine)
        {
            Assert.Equal(0, await RendersOver(engine, renders, 10));
            Assert.Equal(255, dev.LedBytes.ToArray()[0]);
        }
    }

    [Fact]
    public async Task A_watcher_brings_the_shared_render_back()
    {
        using var gpu = Context();
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        var (engine, renders) = Rig(gpu, pins, Device(0, "dev-a"));
        using (engine)
        {
            var watched = false;
            engine.CanvasWatched = () => Volatile.Read(ref watched);
            Assert.Equal(0, await RendersOver(engine, renders, 5));

            Volatile.Write(ref watched, true);
            Assert.True(await RendersOver(engine, renders, 5) >= 3);
        }
    }

    [Fact]
    public async Task An_unpinned_device_renders_the_shared_canvas_unless_it_is_undriven()
    {
        using var gpu = Context();
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "red" });
        var leased = Device(1, "dev-b");
        var (engine, renders) = Rig(gpu, pins, Device(0, "dev-a"), leased);
        using (engine)
        {
            Assert.True(await RendersOver(engine, renders, 5) >= 3);

            engine.UndrivenFrameIds = new HashSet<string>(StringComparer.Ordinal) { "dev-b" };
            Assert.Equal(0, await RendersOver(engine, renders, 5));

            engine.UndrivenFrameIds = new HashSet<string>(StringComparer.Ordinal);
            Assert.True(await RendersOver(engine, renders, 5) >= 3);
            Assert.Equal(255, leased.LedBytes.ToArray()[1]);
        }
    }
}
