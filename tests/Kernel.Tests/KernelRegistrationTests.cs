using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Kernel.Tests;

public sealed class KernelRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddKernel_FailsAtStartup_WhenConnectionStringIsMissing(string? connectionString)
    {
        // A missing connection string must stop startup with a clear message, not surface later as
        // a confusing failure on the first request or a permanently unready instance.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = connectionString,
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddKernel(configuration));

        Assert.Contains("'Platform'", error.Message, StringComparison.Ordinal);
    }
}
