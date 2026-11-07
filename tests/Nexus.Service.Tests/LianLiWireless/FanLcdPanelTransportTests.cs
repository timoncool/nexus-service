using System.Collections.Generic;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class FanLcdPanelTransportTests
{
    private sealed class FakeScreens : IFanLcdScreens
    {
        public int Pushes;
        public string HandlerId => "fake";
        public string LogTag => "fake";
        public IReadOnlyList<string> AttachedSerials() => new[] { "S1" };
        public Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices) => new();
        public void PrepareFrames() { }
        public void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept) { }
        public bool SetRotation(string serial, byte rotation) => true;
        public bool PushImage(string serial, byte[] jpeg) => true;
        public void Release(string serial) { }
        public void SetPanelOwned(string serial, bool owned) { }
        public bool PushPanelFrame(string serial, byte[] jpeg) { Pushes++; return true; }
        public string PanelFamily => "fake";
        public int PanelFps => 12;
        public string PanelName(string serial) => serial;
    }

    [Fact]
    public void An_unchanged_frame_is_pushed_again_only_after_the_keepalive()
    {
        var screens = new FakeScreens();
        long now = 0;
        using var transport = new FanLcdPanelTransport(screens, "S1", "fake:S1", () => now);
        var frame = new byte[FanLcdContent.Size * FanLcdContent.Size * 4];

        transport.Write(frame);
        Assert.Equal(1, screens.Pushes);

        for (var i = 0; i < 10; i++)
        {
            now += 80;
            transport.Write(frame);
        }
        Assert.Equal(1, screens.Pushes);

        now += FanLcdPanelTransport.KeepAliveMs;
        transport.Write(frame);
        Assert.Equal(2, screens.Pushes);

        frame[0] = 0xFF;
        now += 80;
        transport.Write(frame);
        Assert.Equal(3, screens.Pushes);
    }

    [Fact]
    public void A_screen_the_overlay_stops_feeding_still_gets_the_last_frame_every_starvation_interval()
    {
        var screens = new FakeScreens();
        long now = 0;
        using var transport = new FanLcdPanelTransport(screens, "S1", "fake:S1", () => now);
        transport.KeepAwakeTick();
        Assert.Equal(0, screens.Pushes);

        transport.Write(new byte[FanLcdContent.Size * FanLcdContent.Size * 4]);
        Assert.Equal(1, screens.Pushes);

        now += FanLcdPanelTransport.StarvationMs - 1;
        transport.KeepAwakeTick();
        Assert.Equal(1, screens.Pushes);

        now += 1;
        transport.KeepAwakeTick();
        Assert.Equal(2, screens.Pushes);
        transport.KeepAwakeTick();
        Assert.Equal(2, screens.Pushes);
    }
}
