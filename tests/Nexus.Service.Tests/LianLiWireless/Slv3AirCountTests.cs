using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

// The meter is process-wide: hub tests running alongside would add their payloads to the count.
[CollectionDefinition(nameof(Slv3AirCountTests), DisableParallelization = true)]
public sealed class Slv3AirCountCollection;

[Collection(nameof(Slv3AirCountTests))]
public class Slv3AirCountTests
{
    [Fact]
    public void Counts_payloads_per_minute_with_the_busiest_second_only_while_on()
    {
        var state = new Slv3State();
        long now = 10_000;
        using var count = new Slv3AirCount(state, () => now);

        Slv3AirMetrics.RfPayloads.Add(1);
        Assert.Null(state.AirPayloadsPerMinute);

        count.SetEnabled(true);
        Assert.True(state.AirCount);
        for (var s = 0; s < 60; s++)
        {
            var burst = s == 7 ? 40 : 2;
            for (var i = 0; i < burst; i++)
            {
                Slv3AirMetrics.RfPayloads.Add(1);
            }
            now += 1000;
        }
        Slv3AirMetrics.RfPayloads.Add(1);

        Assert.Equal(59 * 2 + 40 + 1, state.AirPayloadsPerMinute);
        Assert.Equal(40, state.AirPeakPerSecond);

        count.SetEnabled(false);
        Assert.False(state.AirCount);
        Assert.Null(state.AirPayloadsPerMinute);
        Assert.Null(state.AirPeakPerSecond);
    }
}
