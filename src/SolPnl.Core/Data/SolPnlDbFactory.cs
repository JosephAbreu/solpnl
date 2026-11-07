using Microsoft.EntityFrameworkCore;

namespace SolPnl.Core.Data;

/// <summary>Creates contexts for explicit settings (setup wizard, tests), outside dependency injection.</summary>
public sealed class SolPnlDbFactory(DbSettings settings) : IDbContextFactory<SolPnlDb>
{
    public SolPnlDb CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<SolPnlDb>();
        settings.Configure(builder);
        return new SolPnlDb(builder.Options);
    }
}
