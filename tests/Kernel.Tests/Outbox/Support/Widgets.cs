using Microsoft.EntityFrameworkCore;
using Platform.Kernel.Contracts.Events;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Persistence;

namespace Platform.Kernel.Tests.Outbox.Support;

/// <summary>A test-only tenant-owned entity that raises a domain event when created or renamed.</summary>
public sealed class Widget : ITenantOwned, IHasDomainEvents
{
    private readonly List<IDomainEvent> _events = [];

    private Widget()
    {
        Name = string.Empty;
    }

    public Widget(Guid id, string name)
    {
        Id = id;
        Name = name;
        _events.Add(new WidgetRenamed(id, name));
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public string Name { get; private set; }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _events;

    public void Rename(string name)
    {
        Name = name;
        _events.Add(new WidgetRenamed(Id, name));
    }

    public void ClearDomainEvents() => _events.Clear();
}

public sealed record WidgetRenamed(Guid WidgetId, string Name) : IDomainEvent
{
    public const string EventName = "test.widget-renamed";
}

/// <summary>
/// A second tenant-aware context that maps the outbox tables (so its events commit with its changes)
/// without owning their schema. How real pack contexts get this is an open M1 decision (AGENTS.md).
/// </summary>
public sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options, ITenantContext tenantContext)
    : TenantAwareDbContext(options, tenantContext)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>(widget =>
        {
            widget.ToTable("test_widgets");
            widget.HasKey(w => w.Id);
            widget.Property(w => w.Id).ValueGeneratedNever();
        });
    }
}
