using Platform.Kernel.Contracts.Modules;

namespace Platform.Architecture.Tests;

/// <summary>
/// First dependency rule; the full rule set arrives with M0 deliverable 4 (architecture tests).
/// </summary>
public sealed class ContractsIsolationTests
{
    [Fact]
    public void KernelContracts_ReferencesNoOtherAssemblyInTheSolution()
    {
        var contracts = typeof(IModule).Assembly;

        var solutionReferences = contracts.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("Platform.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(solutionReferences);
    }

    [Fact]
    public void KernelContracts_IsNamedAsTheBoundaryRulesExpect()
    {
        // The other rules match assemblies by name, so a rename must fail loudly here first.
        Assert.Equal("Platform.Kernel.Contracts", typeof(IModule).Assembly.GetName().Name);
    }
}
