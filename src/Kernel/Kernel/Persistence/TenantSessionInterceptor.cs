using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Platform.Kernel.Persistence;

/// <summary>
/// Passes the current tenant to PostgreSQL, so row-level security policies can enforce isolation
/// underneath EF Core.
/// </summary>
/// <remarks>
/// HAND-WRITE: Nika. The guarantees it must meet and the concepts to study are in
/// <c>docs/hand-write/m0-tenancy.md</c>; choosing which EF Core interceptor interface(s) to implement
/// is part of the exercise. <see cref="IInterceptor"/> is only a placeholder marker. It is not
/// registered with the DbContext until it is written.
/// </remarks>
public sealed class TenantSessionInterceptor : IInterceptor
{
    // TODO(nika): implement per docs/hand-write/m0-tenancy.md, then register it in AddKernel.
}
