using SolPnl.Contracts.Models;
using SolPnl.Core.Devices;

namespace SolPnl.Core.Tests;

public class DeviceStatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeDevice _device = new("fake", "fake-a", fail: false);

    private static Reading State(double value, DateTimeOffset at) => new("fake-a", "vendor.fake.state", value, "", at);

    [Fact]
    public void Current_DescribesTheLatestStateReading()
    {
        var state = DeviceStates.Current(_device, [State(4, Now.AddSeconds(-10))], Now);
        Assert.Equal(new DeviceOperatingState(4, "Mains powered"), state);
    }

    [Fact]
    public void Current_KeepsTheValue_WhenThePluginDoesNotKnowIt()
    {
        Assert.Equal(new DeviceOperatingState(7, null), DeviceStates.Current(_device, [State(7, Now)], Now));
    }

    [Fact]
    public void Current_IsNull_WithoutAFreshStateReading()
    {
        Assert.Null(DeviceStates.Current(_device, [], Now));
        Assert.Null(DeviceStates.Current(_device, [State(4, Now - DeviceFaults.MaxAge - TimeSpan.FromSeconds(1))], Now));
        Assert.Null(DeviceStates.Current(null, [State(4, Now)], Now));
    }
}
