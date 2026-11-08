using Microsoft.EntityFrameworkCore;

namespace SolPnl.Core.Data;

/// <summary>
/// EF Core context used for queries and writes. The schema itself is owned by the hand-written,
/// per-driver scripts in Data/Migrations (applied by <see cref="MigrationService"/>), not by EF migrations,
/// so the mappings here must match those scripts.
///
/// Portability rules for the schema: timestamps are UTC Unix milliseconds (bigint), no provider-specific types.
/// </summary>
public sealed class SolPnlDb(DbContextOptions<SolPnlDb> options) : DbContext(options)
{
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<MetricEntity> Metrics => Set<MetricEntity>();
    public DbSet<ReadingEntity> Readings => Set<ReadingEntity>();
    public DbSet<LatestReadingEntity> LatestReadings => Set<LatestReadingEntity>();
    public DbSet<RollupEntity> Rollups => Set<RollupEntity>();
    public DbSet<RollupStateEntity> RollupStates => Set<RollupStateEntity>();
    public DbSet<DeviceConfigEntity> DeviceConfigs => Set<DeviceConfigEntity>();
    public DbSet<PluginStateEntity> PluginStates => Set<PluginStateEntity>();
    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();
    public DbSet<DaySummaryEntity> DaySummaries => Set<DaySummaryEntity>();
    public DbSet<PluginSettingEntity> PluginSettings => Set<PluginSettingEntity>();
    public DbSet<NotificationLogEntity> NotificationLog => Set<NotificationLogEntity>();
    public DbSet<AlertHistoryEntity> AlertHistory => Set<AlertHistoryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceEntity>(e =>
        {
            e.ToTable("device");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DeviceKey).HasColumnName("device_key").HasMaxLength(200);
            e.Property(x => x.Vendor).HasColumnName("vendor").HasMaxLength(200);
            e.Property(x => x.Model).HasColumnName("model").HasMaxLength(200);
            e.Property(x => x.SerialNumber).HasColumnName("serial_number").HasMaxLength(200);
            e.Property(x => x.FirmwareVersion).HasColumnName("firmware_version").HasMaxLength(100);
            e.Property(x => x.CreatedAtMs).HasColumnName("created_at_ms");
            e.HasIndex(x => x.DeviceKey).IsUnique();
        });

        modelBuilder.Entity<MetricEntity>(e =>
        {
            e.ToTable("metric");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(x => x.Unit).HasColumnName("unit").HasMaxLength(20);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ReadingEntity>(e =>
        {
            e.ToTable("reading");
            e.HasKey(x => new { x.DeviceId, x.MetricId, x.TimestampMs });
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.MetricId).HasColumnName("metric_id");
            e.Property(x => x.TimestampMs).HasColumnName("ts_ms");
            e.Property(x => x.Value).HasColumnName("value");
        });

        modelBuilder.Entity<LatestReadingEntity>(e =>
        {
            e.ToTable("reading_latest");
            e.HasKey(x => new { x.DeviceId, x.MetricId });
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.MetricId).HasColumnName("metric_id");
            e.Property(x => x.TimestampMs).HasColumnName("ts_ms");
            e.Property(x => x.Value).HasColumnName("value");
        });

        modelBuilder.Entity<RollupEntity>(e =>
        {
            e.ToTable("reading_rollup");
            e.HasKey(x => new { x.DeviceId, x.MetricId, x.ResolutionMs, x.BucketMs });
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.MetricId).HasColumnName("metric_id");
            e.Property(x => x.ResolutionMs).HasColumnName("resolution_ms");
            e.Property(x => x.BucketMs).HasColumnName("bucket_ms");
            e.Property(x => x.ValueSum).HasColumnName("value_sum");
            e.Property(x => x.ValueMin).HasColumnName("value_min");
            e.Property(x => x.ValueMax).HasColumnName("value_max");
            e.Property(x => x.SampleCount).HasColumnName("sample_count");
        });

        modelBuilder.Entity<RollupStateEntity>(e =>
        {
            e.ToTable("rollup_state");
            e.HasKey(x => x.ResolutionMs);
            e.Property(x => x.ResolutionMs).HasColumnName("resolution_ms").ValueGeneratedNever();
            e.Property(x => x.WatermarkMs).HasColumnName("watermark_ms");
        });

        modelBuilder.Entity<DeviceConfigEntity>(e =>
        {
            e.ToTable("device_config");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.PluginId).HasColumnName("plugin_id").HasMaxLength(100);
            e.Property(x => x.SettingsJson).HasColumnName("settings");
            e.Property(x => x.DeviceKey).HasColumnName("device_key").HasMaxLength(200);
            e.Property(x => x.CreatedAtMs).HasColumnName("created_at_ms");
            e.HasIndex(x => x.DeviceKey).IsUnique();
        });

        modelBuilder.Entity<PluginStateEntity>(e =>
        {
            e.ToTable("plugin_state");
            e.HasKey(x => x.PluginId);
            e.Property(x => x.PluginId).HasColumnName("plugin_id").HasMaxLength(100).ValueGeneratedNever();
            e.Property(x => x.Enabled).HasColumnName("enabled");
        });

        modelBuilder.Entity<AppSettingEntity>(e =>
        {
            e.ToTable("app_setting");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("setting_key").HasMaxLength(100).ValueGeneratedNever();
            e.Property(x => x.Value).HasColumnName("value");
        });

        modelBuilder.Entity<DaySummaryEntity>(e =>
        {
            e.ToTable("day_summary");
            e.HasKey(x => x.Day);
            e.Property(x => x.Day).HasColumnName("day").HasMaxLength(10).ValueGeneratedNever();
            e.Property(x => x.SunriseMs).HasColumnName("sunrise_ms");
            e.Property(x => x.SunsetMs).HasColumnName("sunset_ms");
            e.Property(x => x.ForecastHigh).HasColumnName("forecast_high");
            e.Property(x => x.ForecastLow).HasColumnName("forecast_low");
            e.Property(x => x.ForecastIrradiation).HasColumnName("forecast_irradiation");
            e.Property(x => x.ClearSkyIrradiation).HasColumnName("clear_sky_irradiation");
            e.Property(x => x.ActualHigh).HasColumnName("actual_high");
            e.Property(x => x.ActualLow).HasColumnName("actual_low");
            e.Property(x => x.SolarWh).HasColumnName("solar_wh");
            e.Property(x => x.LoadWh).HasColumnName("load_wh");
            e.Property(x => x.GridWh).HasColumnName("grid_wh");
            e.Property(x => x.UpdatedMs).HasColumnName("updated_ms");
        });

        modelBuilder.Entity<PluginSettingEntity>(e =>
        {
            e.ToTable("plugin_setting");
            e.HasKey(x => new { x.PluginId, x.Key });
            e.Property(x => x.PluginId).HasColumnName("plugin_id").HasMaxLength(100);
            e.Property(x => x.Key).HasColumnName("setting_key").HasMaxLength(100);
            e.Property(x => x.Value).HasColumnName("value");
        });

        modelBuilder.Entity<NotificationLogEntity>(e =>
        {
            e.ToTable("notification_log");
            e.HasKey(x => x.EventKey);
            e.Property(x => x.EventKey).HasColumnName("event_key").HasMaxLength(255).ValueGeneratedNever();
            e.Property(x => x.RaisedMs).HasColumnName("raised_ms");
            e.Property(x => x.ClearedMs).HasColumnName("cleared_ms");
        });

        modelBuilder.Entity<AlertHistoryEntity>(e =>
        {
            e.ToTable("alert_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EventKey).HasColumnName("event_key").HasMaxLength(255);
            e.Property(x => x.Category).HasColumnName("category").HasMaxLength(20);
            e.Property(x => x.Severity).HasColumnName("severity").HasMaxLength(20);
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(300);
            e.Property(x => x.Message).HasColumnName("message");
            e.Property(x => x.DeviceId).HasColumnName("device_id").HasMaxLength(200);
            e.Property(x => x.RaisedMs).HasColumnName("raised_ms");
            e.Property(x => x.DeliveredMs).HasColumnName("delivered_ms");
            e.Property(x => x.Delivery).HasColumnName("delivery");
            e.Property(x => x.Attempts).HasColumnName("attempts");
        });
    }
}

/// <summary>A device the user configured: which plugin type, and its settings (JSON object of strings).</summary>
public sealed class DeviceConfigEntity
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string PluginId { get; set; }
    public required string SettingsJson { get; set; }

    /// <summary>The device key its readings are stored under (from the plugin), unique across configured devices.</summary>
    public required string DeviceKey { get; set; }
    public long CreatedAtMs { get; set; }
}

/// <summary>Enabled/disabled state of a plugin. No row means enabled. Int (0/1) rather than bool for portability.</summary>
public sealed class PluginStateEntity
{
    public required string PluginId { get; set; }
    public int Enabled { get; set; }
}

/// <summary>Small application flags and values that live with the data (e.g. one-time migrations done).</summary>
public sealed class AppSettingEntity
{
    public required string Key { get; set; }
    public string? Value { get; set; }
}

/// <summary>Bucket sizes used for summaries.</summary>
public static class Resolutions
{
    public const long MinuteMs = 60_000;
    public const long HourMs = 3_600_000;
}

/// <summary>
/// Summary of one metric over one bucket. Sum and count (rather than a mean) are stored so coarser buckets can be
/// built exactly from finer ones.
/// </summary>
public sealed class RollupEntity
{
    public int DeviceId { get; set; }
    public int MetricId { get; set; }
    public long ResolutionMs { get; set; }
    public long BucketMs { get; set; }
    public double ValueSum { get; set; }
    public double ValueMin { get; set; }
    public double ValueMax { get; set; }
    public int SampleCount { get; set; }
}

/// <summary>Rollup progress per resolution: every bucket starting before the watermark has been summarised.</summary>
public sealed class RollupStateEntity
{
    public long ResolutionMs { get; set; }
    public long WatermarkMs { get; set; }
}

public sealed class DeviceEntity
{
    public int Id { get; set; }
    public required string DeviceKey { get; set; }
    public required string Vendor { get; set; }
    public required string Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public long CreatedAtMs { get; set; }
}

public sealed class MetricEntity
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Unit { get; set; }
}

/// <summary>Raw time-series value. One row per device, metric and poll.</summary>
public sealed class ReadingEntity
{
    public int DeviceId { get; set; }
    public int MetricId { get; set; }
    public long TimestampMs { get; set; }
    public double Value { get; set; }
}

/// <summary>Most recent value per device and metric, kept up to date on every save so "current" queries stay cheap.</summary>
public sealed class LatestReadingEntity
{
    public int DeviceId { get; set; }
    public int MetricId { get; set; }
    public long TimestampMs { get; set; }
    public double Value { get; set; }
}

/// <summary>One local day (key "yyyy-MM-dd"). Temperatures °C, irradiation kWh/m², energy Wh.</summary>
public sealed class DaySummaryEntity
{
    public required string Day { get; set; }
    public long? SunriseMs { get; set; }
    public long? SunsetMs { get; set; }
    public double? ForecastHigh { get; set; }
    public double? ForecastLow { get; set; }
    public double? ForecastIrradiation { get; set; }
    public double? ClearSkyIrradiation { get; set; }
    public double? ActualHigh { get; set; }
    public double? ActualLow { get; set; }
    public double? SolarWh { get; set; }
    public double? LoadWh { get; set; }
    public double? GridWh { get; set; }
    public long UpdatedMs { get; set; }
}

/// <summary>One plugin-level setting value (e.g. the SMTP host of the email plugin).</summary>
public sealed class PluginSettingEntity
{
    public required string PluginId { get; set; }
    public required string Key { get; set; }
    public string? Value { get; set; }
}

/// <summary>An event a notification was sent for (and, for conditions like "offline", when it cleared).</summary>
public sealed class NotificationLogEntity
{
    public required string EventKey { get; set; }
    public long RaisedMs { get; set; }
    public long? ClearedMs { get; set; }
}

/// <summary>An alert SolPnl raised (and its delivery result), for the Alerts page.</summary>
public sealed class AlertHistoryEntity
{
    public long Id { get; set; }
    public required string EventKey { get; set; }
    public required string Category { get; set; }
    public required string Severity { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public string? DeviceId { get; set; }
    public long RaisedMs { get; set; }
    public long? DeliveredMs { get; set; }
    public string? Delivery { get; set; }
    public int Attempts { get; set; }
}
