using Platform.Architecture.Tests.Support;
using Platform.Kernel;
using Platform.Kernel.Contracts.Modules;

namespace Platform.Architecture.Tests;

/// <summary>
/// The dependency rules from AGENTS.md section 4, checked twice: on project references (catches a
/// reference before any code uses it) and on compiled assemblies (catches what the code really uses).
/// </summary>
public sealed class DependencyRuleTests
{
    public static TheoryData<string> PackProjectPaths =>
        [.. Solution.PackProjects.Select(project => Path.GetRelativePath(Solution.Root.FullName, project.FullName))];

    public static TheoryData<string> PackAssemblyNames =>
        [.. Solution.PackAssemblies.Select(assembly => assembly.GetName().Name!)];

    [Fact]
    public void TheSolutionHasAtLeastOnePack()
    {
        // Every pack rule below iterates over what it finds. Finding nothing would make them all pass
        // vacuously, so an empty result is itself a failure.
        Assert.NotEmpty(Solution.PackProjects);
        Assert.NotEmpty(Solution.PackAssemblies);
    }

    [Theory]
    [MemberData(nameof(PackProjectPaths))]
    public void PackProject_IsNamedAsAPack(string packProject)
    {
        // The Kernel and assembly rules recognise packs by the "Packs." prefix. A pack project named
        // anything else would slip past them.
        Assert.StartsWith(
            Solution.PackProjectPrefix,
            Path.GetFileNameWithoutExtension(packProject),
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PackProjectPaths))]
    public void PackProject_IsWiredIntoTheHostAndCompiled(string packProject)
    {
        // Rule 5: the host is the composition root for every pack. It is also what puts the pack's
        // assembly next to these tests, so an unwired pack would escape every assembly-level rule.
        var packName = Path.GetFileNameWithoutExtension(packProject);
        var hostReferences = Solution.ProjectReferences(
            Solution.Project("src", "Host", "Platform.Api", "Platform.Api.csproj"));

        Assert.Contains(packName, hostReferences);
        Assert.Contains(Solution.PackAssemblies, assembly => assembly.GetName().Name == "Platform." + packName);
    }

    [Theory]
    [MemberData(nameof(PackProjectPaths))]
    public void PackProject_ReferencesOnlyKernelContracts(string packProject)
    {
        var references = Solution.ProjectReferences(Solution.Project(packProject));

        Assert.All(references, reference => Assert.Equal("Kernel.Contracts", reference));
    }

    [Theory]
    [MemberData(nameof(PackAssemblyNames))]
    public void PackAssembly_UsesNoSolutionAssemblyButKernelContracts(string packAssembly)
    {
        var assembly = Solution.PackAssemblies.Single(a => a.GetName().Name == packAssembly);

        var forbidden = Solution.SolutionReferences(assembly)
            .Where(name => name != Solution.ContractsAssembly)
            .ToList();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void KernelProject_ReferencesNoPack()
    {
        var references = Solution.ProjectReferences(Solution.Project("src", "Kernel", "Kernel", "Kernel.csproj"));

        Assert.DoesNotContain(
            references,
            reference => reference.StartsWith(Solution.PackProjectPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void KernelAssembly_UsesNoPack()
    {
        var kernel = typeof(KernelServiceCollectionExtensions).Assembly;

        Assert.DoesNotContain(
            Solution.SolutionReferences(kernel),
            name => name.StartsWith(Solution.PackAssemblyPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void KernelContractsProject_ReferencesNothingInTheSolution()
    {
        var references = Solution.ProjectReferences(
            Solution.Project("src", "Kernel", "Kernel.Contracts", "Kernel.Contracts.csproj"));

        Assert.Empty(references);
    }

    [Fact]
    public void KernelContractsAssembly_UsesNothingInTheSolution()
    {
        Assert.Empty(Solution.SolutionReferences(typeof(IModule).Assembly));
    }

    [Fact]
    public void AssemblyNames_MatchWhatTheRulesExpect()
    {
        // The rules match assemblies by name, so a rename must fail loudly here first.
        Assert.Equal(Solution.ContractsAssembly, typeof(IModule).Assembly.GetName().Name);
        Assert.Equal(Solution.KernelAssembly, typeof(KernelServiceCollectionExtensions).Assembly.GetName().Name);
        Assert.All(
            Solution.PackAssemblies,
            pack => Assert.StartsWith(Solution.PackAssemblyPrefix, pack.GetName().Name, StringComparison.Ordinal));
    }
}
