using System.Text.RegularExpressions;

namespace AST.Meta.Tests;

// META guard — AST.UI is a View-side leaf shared library (a UI SharedKernel). rule-module-boundary §1: it must
// reference only the shared kernel AST.Core, never AST.Shell / a business module / the exe — otherwise a module
// UI referencing AST.UI would transitively pull in the Shell and every module would couple to it. Advisory in a
// convention regresses silently; this makes a stray reference a build-red failure for every contributor alike.
public class SharedUiBoundaryTests
{
    private static readonly Regex ProjectRef =
        new(@"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase);

    [Fact]
    public void AstUiReferencesOnlyAstCore()
    {
        var csproj = Path.Combine(MetaTest.RepoRoot(), "AST.UI", "AST.UI.csproj");
        Assert.True(File.Exists(csproj), $"AST.UI.csproj not found at {csproj}.");

        var violations = new List<string>();
        foreach (Match m in ProjectRef.Matches(File.ReadAllText(csproj)))
        {
            var referenced = Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/'));
            if (!referenced.Equals("AST.Core", StringComparison.Ordinal))
            {
                violations.Add(referenced);
            }
        }

        Assert.True(
            violations.Count == 0,
            "rule-module-boundary §1: AST.UI (a View-side leaf shared library) must reference only AST.Core, "
                + "never AST.Shell, a business module, or the exe:\n  " + string.Join("\n  ", violations));
    }
}
