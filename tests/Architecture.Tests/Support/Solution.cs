using System.Reflection;
using System.Xml.Linq;

namespace Platform.Architecture.Tests.Support;

/// <summary>
/// Finds the solution's projects and compiled assemblies. Rules iterate over what is actually on
/// disk, so a new pack is covered the moment it exists instead of when someone remembers to list it.
/// </summary>
internal static class Solution
{
    public const string ContractsAssembly = "Platform.Kernel.Contracts";
    public const string KernelAssembly = "Platform.Kernel";
    public const string PackProjectPrefix = "Packs.";
    public const string PackAssemblyPrefix = "Platform." + PackProjectPrefix;

    public static DirectoryInfo Root { get; } = FindRoot();

    /// <summary>Every project file under <c>src/Packs</c>, whatever it is named.</summary>
    public static IReadOnlyList<FileInfo> PackProjects { get; } =
        Directory.GetFiles(Path.Combine(Root.FullName, "src", "Packs"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToList();

    /// <summary>
    /// Every pack assembly copied next to the tests. The test project references the host, and the
    /// host references every pack (a rule checks that), so all packs are here.
    /// </summary>
    public static IReadOnlyList<Assembly> PackAssemblies { get; } =
        Directory.GetFiles(AppContext.BaseDirectory, PackAssemblyPrefix + "*.dll")
            .Select(Assembly.LoadFrom)
            .ToList();

    public static FileInfo Project(params string[] relativePath) =>
        new(Path.Combine([Root.FullName, .. relativePath]));

    /// <summary>File names (without extension) of the projects a csproj references directly.</summary>
    public static IReadOnlyList<string> ProjectReferences(FileInfo project)
    {
        var document = XDocument.Load(project.FullName);

        // A csproj with the legacy MSBuild XML namespace would make every lookup below match nothing,
        // so the rules would pass without checking anything. Refuse to read it instead.
        if (document.Root?.Name.NamespaceName is { Length: > 0 } ns)
        {
            throw new InvalidOperationException($"{project.Name} declares XML namespace '{ns}'; the rules cannot read it.");
        }

        return document
            .Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .OfType<string>()
            .SelectMany(include => include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')))
            .ToList();
    }

    /// <summary>Solution assemblies (<c>Platform.*</c>) an assembly's compiled code references.</summary>
    public static IReadOnlyList<string> SolutionReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .Where(name => name.StartsWith("Platform.", StringComparison.Ordinal))
            .ToList();

    private static DirectoryInfo FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("Platform.slnx not found above " + AppContext.BaseDirectory);
    }
}
