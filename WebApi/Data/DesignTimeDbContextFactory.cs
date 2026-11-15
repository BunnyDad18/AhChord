using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WebApi.Data;

/// <summary>
/// ST-106 tooling: <c>dotnet ef migrations add</c> needs a context without booting
/// Program.cs (which dials the dev Postgres, runs the backfills, and starts Hangfire).
/// The connection string is never contacted for <c>migrations add/remove</c> — schema
/// diffing is purely local. Used only by the EF tools; nothing references it at runtime.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=ahchord_design;Username=postgres;Password=postgres")
            .Options;

        return new ApplicationDbContext(options);
    }
}
