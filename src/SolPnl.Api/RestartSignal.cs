namespace SolPnl.Api;

/// <summary>Lets an endpoint ask Program to stop the host and build a fresh one (e.g. after setup saves new settings).</summary>
public sealed class RestartSignal
{
    private volatile bool _requested;

    public bool Requested => _requested;

    public void Reset() => _requested = false;

    /// <summary>Stops the current host shortly after the current response has been sent.</summary>
    public void Request(IHostApplicationLifetime lifetime, TimeSpan? delay = null)
    {
        _requested = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay ?? TimeSpan.FromMilliseconds(500));
            lifetime.StopApplication();
        });
    }
}

/// <summary>Where machine-specific data lives (settings file, default SQLite database).</summary>
public sealed record SetupPaths(string DataDirectory)
{
    public string DefaultSqlitePath => Path.Combine(DataDirectory, "solpnl.db");
}
