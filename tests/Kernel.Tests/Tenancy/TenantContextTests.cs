using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Tenancy;

namespace Platform.Kernel.Tests.Tenancy;

public sealed class TenantContextTests
{
    [Fact]
    public void Unresolved_ReadingTheTenant_Throws()
    {
        var context = new TenantContext();

        Assert.False(context.IsResolved);
        Assert.Throws<InvalidOperationException>(() => context.TenantId);
    }

    [Fact]
    public void Set_ResolvesTheTenant()
    {
        var context = new TenantContext();
        var tenant = TenantId.New();

        context.Set(tenant);

        Assert.True(context.IsResolved);
        Assert.Equal(tenant, context.TenantId);
    }

    [Fact]
    public void Set_SameTenantTwice_IsAllowed()
    {
        var context = new TenantContext();
        var tenant = TenantId.New();

        context.Set(tenant);
        context.Set(tenant);

        Assert.Equal(tenant, context.TenantId);
    }

    [Fact]
    public void Set_DifferentTenant_ThrowsAndKeepsTheFirst()
    {
        // A scope that silently switched tenant halfway would mix two tenants' data in one unit of work.
        var context = new TenantContext();
        var first = TenantId.New();

        context.Set(first);

        Assert.Throws<InvalidOperationException>(() => context.Set(TenantId.New()));
        Assert.Equal(first, context.TenantId);
    }

    [Fact]
    public void Set_DefaultTenantId_Throws()
    {
        // default(TenantId) skips the constructor's check and equals the filter's "no tenant" value.
        var context = new TenantContext();

        Assert.Throws<ArgumentException>(() => context.Set(default));
        Assert.False(context.IsResolved);
    }

    [Fact]
    public void TenantId_CannotBeEmpty()
    {
        Assert.Throws<ArgumentException>(() => new TenantId(Guid.Empty));
    }

    [Fact]
    public void TenantId_New_IsTimeOrderedVersion7()
    {
        Assert.Equal(7, TenantId.New().Value.Version);
    }
}
