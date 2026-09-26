using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Platform.Architecture.Tests.Support;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchitectureModel = ArchUnitNET.Domain.Architecture;

namespace Platform.Architecture.Tests;

/// <summary>
/// Pack domain code (namespaces <c>Platform.Packs.&lt;Pack&gt;.Domain</c>) stays free of persistence and
/// web frameworks, so business rules can be tested and reasoned about without them. A pack may use
/// EF Core or ASP.NET Core elsewhere, for example in its persistence mapping.
///
/// This needs type-level analysis (which types reference which), so it uses ArchUnitNET rather than
/// assembly references: the pack assembly as a whole may legitimately reference EF Core.
/// </summary>
public sealed class PackDomainRuleTests
{
    private const string PackDomainNamespace = @"^Platform\.Packs\.[^.]+\.Domain(\..+)?$";
    private const string ForbiddenFrameworks = @"^Microsoft\.(EntityFrameworkCore|AspNetCore)(\..+)?$";

    private static readonly ArchitectureModel _packs = new ArchLoader()
        .LoadAssemblies([.. Solution.PackAssemblies])
        .Build();

    [Fact]
    public void PackDomainCode_DoesNotDependOnEfCoreOrAspNetCore()
    {
        var rule = Types(true).That().ResideInNamespaceMatching(PackDomainNamespace)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(ForbiddenFrameworks)
            // TODO(M1): remove once the first pack has domain types. M0 packs have none, and without
            // this flag ArchUnitNET fails a rule that matches no types.
            .WithoutRequiringPositiveResults();

        rule.Check(_packs);
    }
}
