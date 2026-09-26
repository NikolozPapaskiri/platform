using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Kernel.Contracts.Modules;

/// <summary>
/// A pack's entry point. The host lists every module explicitly and calls <see cref="Register"/>
/// once at startup, so a pack never needs a reference to the Kernel implementation or the host.
/// </summary>
public interface IModule
{
    /// <summary>Stable module name, used in logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>Adds the module's services. Runs once, before the application is built.</summary>
    void Register(IServiceCollection services, IConfiguration configuration);
}
