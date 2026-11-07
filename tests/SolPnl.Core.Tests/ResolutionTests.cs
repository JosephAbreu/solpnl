using SolPnl.Contracts.Models;
using SolPnl.Core.Storage;

namespace SolPnl.Core.Tests;

public class ResolutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);
    private static readonly RetentionOptions Defaults = new(); // raw 7 d, minute 90 d, hour forever

    [Theory]
    [InlineData(0, 1, HistoryResolution.Raw)]      // last hour
    [InlineData(0, 2, HistoryResolution.Raw)]      // last 2 h: boundary
    [InlineData(0, 3, HistoryResolution.Minute)]   // last 3 h
    [InlineData(0, 24, HistoryResolution.Minute)]  // last day
    [InlineData(0, 72, HistoryResolution.Minute)]  // last 3 days: boundary
    [InlineData(0, 24 * 7, HistoryResolution.Hour)] // last week
    public void ChoosesByRange(int endHoursAgo, int spanHours, HistoryResolution expected)
    {
        var to = Now.AddHours(-endHoursAgo);
        Assert.Equal(expected, EfReadingStore.ChooseResolution(to.AddHours(-spanHours), to, Now, Defaults));
    }

    [Fact]
    public void ShortRange_BeyondRawRetention_UsesMinutes()
    {
        var from = Now.AddDays(-10);
        Assert.Equal(HistoryResolution.Minute, EfReadingStore.ChooseResolution(from, from.AddHours(1), Now, Defaults));
    }

    [Fact]
    public void ShortRange_BeyondMinuteRetention_UsesHours()
    {
        var from = Now.AddDays(-100);
        Assert.Equal(HistoryResolution.Hour, EfReadingStore.ChooseResolution(from, from.AddHours(1), Now, Defaults));
    }

    [Fact]
    public void ZeroRetention_MeansForever()
    {
        var keepAll = new RetentionOptions { RawDays = 0 };
        var from = Now.AddDays(-365);
        Assert.Equal(HistoryResolution.Raw, EfReadingStore.ChooseResolution(from, from.AddHours(1), Now, keepAll));
    }
}
