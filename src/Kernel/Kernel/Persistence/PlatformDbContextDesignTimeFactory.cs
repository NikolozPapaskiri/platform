using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Platform.Kernel.Tenancy;

namespace Platform.Kernel.Persistence;

/// <summary>
/// Used only by the <c>dotnet ef</c> tools. <c>migrations add</c> never connects to a database, so
/// the connection string is a placeholder; <c>database update</c> is always given an explicit
/// <c>--connection</c> for the owner role (AGENTS.md section 2), never the application role.
/// </summary>
public sealed class PlatformDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=localhost")
            .Options;

        return new PlatformDbContext(options, new TenantContext());
    }
}
