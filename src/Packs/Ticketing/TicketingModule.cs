using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Kernel.Contracts.Modules;

namespace Platform.Packs.Ticketing;

/// <summary>
/// Placeholder registration for the ticketing pack. It exists in M0 so host wiring and the
/// architecture tests have a real pack to check; ticketing features arrive in M1.
/// </summary>
public sealed class TicketingModule : IModule
{
    public string Name => "Ticketing";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Intentionally empty until M1.
    }
}
