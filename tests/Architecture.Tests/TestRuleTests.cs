using System.Text.RegularExpressions;
using Platform.Architecture.Tests.Support;

namespace Platform.Architecture.Tests;

/// <summary>
/// AGENTS.md section 8: never skip or disable tests to make CI pass. The only allowed skip is a
/// hand-write contract test marked exactly <c>HAND-WRITE: Nika</c>. Checked on the source text, so
/// every test project is covered without referencing it.
/// </summary>
public sealed partial class TestRuleTests
{
    private const string AllowedSkipReason = "\"HAND-WRITE: Nika\"";

    [Fact]
    public void TestsAreOnlySkippedAsHandWriteContracts()
    {
        var violations = new List<string>();
        foreach (var file in TestSources())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (KeepsATestFromRunning(lines[i]))
                {
                    violations.Add($"{Path.GetRelativePath(Solution.Root.FullName, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("[Fact(Skip = \"flaky\")]")]
    [InlineData("[Fact(Skip = Reasons.Later)]")]
    [InlineData("[Theory(Skip=\"HAND-WRITE: Someone\")]")]
    [InlineData("[Fact(Explicit = true)]")]
    [InlineData("[Fact(SkipUnless = nameof(Enabled))]")]
    [InlineData("Assert.Skip(\"later\");")]
    [InlineData("Assert.SkipWhen(true, \"later\");")]
    [InlineData("[Fact(SkipExceptions = [typeof(NotImplementedException)])]")]
    [InlineData("[Fact(Skip = \"HAND-WRITE: Nika\" + Later)]")]
    [InlineData("throw new InvalidOperationException(DynamicSkipToken.Value + \"later\");")]
    [InlineData("throw new InvalidOperationException(\"$XunitDynamicSkip$later\");")]
    [InlineData("throw SkipException.ForSkip(\"later\");")]
    public void TheRuleCatches(string line)
    {
        // Guards the patterns themselves: a regex that matches nothing would pass every file.
        Assert.True(KeepsATestFromRunning(line), line);
    }

    [Fact]
    public void TheRuleAllowsHandWriteContracts()
    {
        Assert.False(KeepsATestFromRunning("[Fact(Skip = \"HAND-WRITE: Nika\")]"));
    }

    private static bool KeepsATestFromRunning(string line) =>
        SkipProperty().Matches(line).Any(skip => skip.Groups["reason"].Value != AllowedSkipReason) ||
        OtherWayToNotRun().IsMatch(line);

    private static IEnumerable<string> TestSources()
    {
        var separator = Path.DirectorySeparatorChar;

        // This file is left out (its samples in TheRuleCatches are violations on purpose). Matched by
        // its path in the repository, not [CallerFilePath], which deterministic builds rewrite.
        var thisFile = Path.Combine(Solution.Root.FullName, "tests", "Architecture.Tests", "TestRuleTests.cs");
        return Directory.GetFiles(Path.Combine(Solution.Root.FullName, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal) &&
                           !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal) &&
                           !string.Equals(path, thisFile, StringComparison.OrdinalIgnoreCase));
    }

    // The reason group captures a string literal that ends the argument; anything else (a constant,
    // an expression, a concatenation) captures nothing and so never equals the allowed reason.
    [GeneratedRegex("""\bSkip\s*=\s*(?<reason>"[^"]*"(?=\s*[,)]))?""")]
    private static partial Regex SkipProperty();

    // xUnit v3's other ways to keep a test from running: conditional skips, skips on exception types,
    // explicit-only tests, and skipping from inside the test body (Assert.Skip, SkipException, or an
    // exception message carrying the dynamic-skip token).
    [GeneratedRegex("""\b(SkipUnless|SkipWhen|SkipExceptions)\s*=|\bExplicit\s*=\s*true\b|\bAssert\.Skip(Unless|When)?\s*\(|\bSkipException\b|\bDynamicSkipToken\b|\$XunitDynamicSkip\$""")]
    private static partial Regex OtherWayToNotRun();
}
