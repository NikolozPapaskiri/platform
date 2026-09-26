using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Packs.Ticketing;

namespace Platform.Ticketing.Tests;

public sealed class TicketingModuleTests
{
    [Fact]
    public void Register_Succeeds_WithNoPackConfiguration()
    {
        // The host registers every module at startup; a pack that demands configuration it does not
        // yet use would stop the whole platform from booting.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        new TicketingModule().Register(services, configuration);

        Assert.Equal("Ticketing", new TicketingModule().Name);
    }
}
