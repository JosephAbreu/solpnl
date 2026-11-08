using Microsoft.EntityFrameworkCore;
using SolPnl.Contracts.Models;
using SolPnl.Core.Backup;
using SolPnl.Core.Data;

namespace SolPnl.Core.Tests;

public class BackupScheduleTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly BackupSettings At0230 = new(Time: "02:30");

    // 2026-11-10 is CST (UTC-6).
    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(-6));

    [Fact]
    public void BeforeTodaysTime_IsDueToday()
    {
        var next = BackupService.NextRun(At0230, Chicago, lastScheduledAt: Local(9, 2, 30), now: Local(10, 1));
        Assert.Equal(Local(10, 2, 30), next);
    }

    [Fact]
    public void AfterTodaysRun_IsDueTomorrow()
    {
        var next = BackupService.NextRun(At0230, Chicago, lastScheduledAt: Local(10, 2, 31), now: Local(10, 9));
        Assert.Equal(Local(11, 2, 30), next);
    }

    [Fact]
    public void MissedTime_IsDueNow()
    {
        // SolPnl was off at 02:30; at 09:00 the backup is overdue (due time already passed).
        var now = Local(10, 9);
        Assert.True(BackupService.NextRun(At0230, Chicago, lastScheduledAt: Local(9, 2, 30), now) <= now);
        Assert.True(BackupService.NextRun(At0230, Chicago, lastScheduledAt: null, now) <= now);
    }

    [Fact]
    public void SkippedDaylightSavingTime_RunsAnHourLater()
    {
        // 2027-03-14: clocks jump from 02:00 to 03:00 in Chicago, so 02:30 doesn't exist.
        var now = new DateTimeOffset(2027, 3, 14, 1, 0, 0, TimeSpan.FromHours(-6));
        var next = BackupService.NextRun(At0230, Chicago, lastScheduledAt: now.AddDays(-1), now);
        Assert.Equal(new DateTimeOffset(2027, 3, 14, 3, 30, 0, TimeSpan.FromHours(-5)), next);
    }

    [Fact]
    public void FileNames_RoundTrip_AndRejectAnythingElse()
    {
        var at = new DateTimeOffset(2026, 11, 8, 8, 30, 5, TimeSpan.Zero);
        var name = BackupService.FileName(at, BackupKinds.BeforeRestore);
        Assert.Equal("solpnl-20261108T083005Z-before-restore.zip", name);
        Assert.Equal((at, BackupKinds.BeforeRestore), BackupService.ParseName(name));

        Assert.Null(BackupService.ParseName("../solpnl-20261108T083005Z-manual.zip"));
        Assert.Null(BackupService.ParseName("solpnl-20261108T083005Z-manual.zip.partial"));
        Assert.Null(BackupService.ParseName("appsettings.Local.json"));
    }

    [Fact]
    public void EveryTable_IsBackedUp()
    {
        var options = new DbContextOptionsBuilder<SolPnlDb>().UseSqlite("Data Source=:memory:").Options;
        using var db = new SolPnlDb(options);
        var model = db.Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();
        var backedUp = BackupArchive.Tables.Select(t => t.EntityType).ToHashSet();
        Assert.Equal(model.OrderBy(t => t.Name), backedUp.OrderBy(t => t.Name));
    }
}
