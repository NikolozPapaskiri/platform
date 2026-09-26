namespace Platform.Kernel.Contracts.Tenancy;

/// <summary>
/// Identifies a tenant (an organizer). A dedicated type instead of a bare <see cref="Guid"/>, so a
/// tenant id cannot be passed where an order id or event id is expected, and vice versa.
/// </summary>
public readonly record struct TenantId
{
    public TenantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A tenant id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    /// <summary>
    /// A new id. Version 7 GUIDs start with a timestamp, so new rows land at the end of an index
    /// instead of at random pages, which keeps inserts and index size efficient.
    /// </summary>
    public static TenantId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
