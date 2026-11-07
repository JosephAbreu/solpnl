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
    }
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
