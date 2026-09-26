namespace Platform.Kernel;

/// <summary>Tags that decide which health checks each probe endpoint runs.</summary>
public static class HealthTags
{
    /// <summary>Dependencies an instance needs before it should receive traffic.</summary>
    public const string Ready = "ready";
}
