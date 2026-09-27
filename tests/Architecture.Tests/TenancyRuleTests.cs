using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Platform.Architecture.Tests.Support;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Persistence;
using Platform.Kernel.Tenancy;

namespace Platform.Architecture.Tests;

/// <summary>
/// Tenancy rules from AGENTS.md section 5 that can be checked without a database: the EF Core model
/// is built offline (building a model never connects).
/// </summary>
public sealed class TenancyRuleTests
{
    [Fact]
    public void EveryTenantAwareDbContext_IsCoveredByTheseRules()
    {
        // The model rules below inspect the contexts listed in CreateContexts. A new context that is
        // not listed there would escape them, so it must fail here until it is added.
        var contexts = CodeTypes()
            .Where(type => type is { IsAbstract: false } && type.IsSubclassOf(typeof(TenantAwareDbContext)))
            .ToList();

        Assert.Equal([typeof(PlatformDbContext)], contexts);
    }

    [Fact]
    public void EveryDbContext_IsTenantAware()
    {
        // A plain DbContext gets no tenant filter and no write checks, and the model rules below never
        // see it. It is also the obvious workaround for packs until the M1 persistence ADR lands.
        var plain = CodeTypes()
            .Where(type => type.IsSubclassOf(typeof(DbContext)) &&
                           type != typeof(TenantAwareDbContext) &&
                           !type.IsSubclassOf(typeof(TenantAwareDbContext)))
            .Select(type => type.FullName)
            .ToList();

        Assert.Empty(plain);
    }

    [Fact]
    public void EveryTenantOwnedEntity_HasTheTenantQueryFilter()
    {
        foreach (var context in CreateContexts())
        {
            using var _ = context;
            var tenantOwned = context.Model.GetEntityTypes()
                .Where(type => typeof(ITenantOwned).IsAssignableFrom(type.ClrType))
                .ToList();

            // Query filters live on the root of an inheritance hierarchy and apply to derived types.
            Assert.NotEmpty(tenantOwned);
            Assert.All(tenantOwned, type => Assert.Contains(
                type.GetRootType().GetDeclaredQueryFilters(),
                filter => filter.Key == TenantAwareDbContext.TenantFilterName));
        }
    }

    [Fact]
    public void EveryEntityWithATenantId_IsTenantOwnedOrMarkedAsCatalog()
    {
        // The dangerous mistake is an entity that has a TenantId column but forgot ITenantOwned: it
        // looks tenant-scoped and gets no filter and no write checks. Catalog entities must say so.
        foreach (var context in CreateContexts())
        {
            using var _ = context;
            var unmarked = context.Model.GetEntityTypes()
                .Where(type => type.FindProperty(nameof(ITenantOwned.TenantId)) is not null)
                .Where(type => !typeof(ITenantOwned).IsAssignableFrom(type.ClrType))
                .Where(type => type.ClrType.GetCustomAttribute<TenantCatalogAttribute>() is null)
                .Select(type => type.ClrType.FullName)
                .ToList();

            Assert.Empty(unmarked);
        }
    }

    [Fact]
    public void IgnoreQueryFilters_IsOnlyUsedInKernelTenancyCode_WithAReason()
    {
        // AGENTS.md section 5: bypassing the tenant filter is allowed only in Kernel tenancy code, and
        // every use carries a comment on the line above explaining why.
        var allowed = Path.Combine(Solution.Root.FullName, "src", "Kernel", "Kernel", "Tenancy");
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(Solution.Root.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("IgnoreQueryFilters(", StringComparison.Ordinal))
                {
                    continue;
                }

                var inAllowedFolder = file.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                var hasReason = i > 0 && lines[i - 1].TrimStart().StartsWith("//", StringComparison.Ordinal);
                if (!inAllowedFolder || !hasReason)
                {
                    violations.Add($"{Path.GetRelativePath(Solution.Root.FullName, file)}:{i + 1}");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>Every type in the application's own code: the Kernel, the host, and every pack.</summary>
    private static IEnumerable<Type> CodeTypes() =>
        new[] { typeof(PlatformDbContext).Assembly, Assembly.Load(new AssemblyName(Solution.HostAssembly)) }
            .Concat(Solution.PackAssemblies)
            .SelectMany(assembly => assembly.GetTypes());

    private static IEnumerable<TenantAwareDbContext> CreateContexts()
    {
        yield return new PlatformDbContext(
            new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql("Host=localhost").Options,
            new TenantContext());
    }
}
