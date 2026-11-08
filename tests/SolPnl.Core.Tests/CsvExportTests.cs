using SolPnl.Contracts.Models;
using SolPnl.Core.Export;

namespace SolPnl.Core.Tests;

public class CsvExportTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]   // not run as a formula when opened in a spreadsheet
    [InlineData("+1", "+1")]                // a number stays a number
    [InlineData("-5.5", "-5.5")]
    public void Escape_QuotesAndDefusesFormulas(string field, string expected) =>
        Assert.Equal(expected, CsvExport.Escape(field));

    [Fact]
    public void Number_IsInvariant_WithoutTrailingZeros()
    {
        Assert.Equal("1234.5", CsvExport.Number(1234.5));
        Assert.Equal("0.000001", CsvExport.Number(0.000001));
        Assert.Equal("", CsvExport.Number(null));
        Assert.Equal("", CsvExport.Number(double.NaN));
    }

    [Fact]
    public async Task Days_HaveAHeaderAndOneRowPerDay_InLocalTime()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var day = new DaySummary(new DateOnly(2026, 11, 8),
            new DateTimeOffset(2026, 11, 8, 12, 15, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 8, 22, 50, 0, TimeSpan.Zero),
            20.5, 8, 3.1, 4.2, 21, 7.5, 30.25, 45, 14.75, 4.54);
        var writer = new StringWriter();
        await CsvExport.WriteDaysAsync(writer, [day, new DaySummary(new DateOnly(2026, 11, 9), null, null, null, null, null, null, null, null, null, null, null)], zone, "$");
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("date,sunrise,sunset,", lines[0]);
        Assert.EndsWith(",saved ($)", lines[0]);
        Assert.Equal("2026-11-08,06:15,16:50,20.5,8,3.1,4.2,21,7.5,30.25,45,14.75,4.54", lines[1]);
        Assert.Equal("2026-11-09,,,,,,,,,,,,", lines[2]);
    }

    [Theory]
    [InlineData(1, HistoryResolution.Raw)]
    [InlineData(24, HistoryResolution.Minute)]
    [InlineData(24 * 7, HistoryResolution.Hour)]
    public void AutoResolution_MatchesTheCharts(int hours, HistoryResolution expected) =>
        Assert.Equal(expected, CsvExport.AutoResolution(TimeSpan.FromHours(hours)));
}
