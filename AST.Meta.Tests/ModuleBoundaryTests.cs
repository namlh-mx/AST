using System.Text.RegularExpressions;

namespace AST.Meta.Tests;

// META guard — the mechanical half of the modular hard-lock. rule-module-boundary §1 is otherwise advisory
// (a convention plus a reviewer reading), so a stray reference regresses silently. This makes it a build-red failure,
// which is the ONLY enforcement layer that covers every contributor uniformly: a violation fails the
// acceptance gate regardless of who wrote it or whether they read the rule.
//
// Phase 1 (2026-07-18): no business module references another module, AST.Shell, or the exe.
// Phase 2 (Task X, plug-in wiring): the exe AST must not COMPILE against a business module (no `using
// AST.Modules.*`, no hardcoded `AddModule<T>()`) — DirectoryModuleCatalog discovers modules from Modules/ at
// runtime instead (see `AST/App.xaml.cs` CreateModuleCatalog()). The exe's AST.csproj MAY still list a module
// as a build-only ProjectReference (ReferenceOutputAssembly="false" — needed so the module builds and its DLL
// can be staged into Modules/), which this guard allows but requires to be marked build-only.
public class ModuleBoundaryTests
{
    private static readonly Regex ProjectRef =
        new(@"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase);

    private static readonly Regex ProjectRefTag =
        new(@"<ProjectReference\b[^>]*/>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Real `using AST.Modules.X;` directives only — not commented-out lines, so a stray "// using ..." (or a
    // prose mention of "AST.Modules.IAM" in a comment) does not false-positive this guard.
    private static readonly Regex UsingModuleDirective =
        new(@"(?m)^(?!\s*//)\s*using\s+AST\.Modules\.", RegexOptions.None);

    // A real `moduleCatalog.AddModule<T>()` call only — not a comment line explaining its absence (e.g. this
    // guard's own doc comments say "no hardcoded `AddModule<T>()`", which would otherwise false-positive itself).
    private static readonly Regex AddModuleCall =
        new(@"(?m)^(?!\s*//).*\bAddModule\s*<", RegexOptions.None);

    [Fact]
    public void NoBusinessModuleReferencesAnotherModuleTheShellOrTheExe()
    {
        var root = MetaTest.RepoRoot();
        var violations = new List<string>();

        foreach (var (moduleName, csproj) in BusinessModuleProjects(root))
        {
            foreach (Match m in ProjectRef.Matches(File.ReadAllText(csproj)))
            {
                // ProjectReference paths use '\'; normalise to '/' so the last segment resolves on any OS.
                var referenced = Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/'));
                if (IsForbiddenModuleDependency(moduleName, referenced))
                {
                    violations.Add($"{moduleName} -> {referenced}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "rule-module-boundary §1: a business module must reference only downward/shared layers "
                + "(AST.Core / AST.Infrastructure), never another module, AST.Shell, or the exe AST. Communicate "
                + "via a SharedKernel contract / IRegionManager / IEventAggregator instead:\n  "
                + string.Join("\n  ", violations));
    }

    [Fact]
    public void ExeDoesNotCompileAgainstAnyBusinessModule()
    {
        var root = MetaTest.RepoRoot();
        var exeDir = Path.Combine(root, "AST");
        var moduleNames = BusinessModuleProjects(root).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        // (a) AST.csproj may keep a module as a ProjectReference (so it builds + its DLL can be staged into
        // Modules/), but ONLY build-only -- never linked into this assembly's compilation.
        var csproj = Path.Combine(exeDir, "AST.csproj");
        foreach (Match tag in ProjectRefTag.Matches(File.ReadAllText(csproj)))
        {
            var include = ProjectRef.Match(tag.Value);
            if (!include.Success)
            {
                continue;
            }

            var referenced = Path.GetFileNameWithoutExtension(include.Groups[1].Value.Replace('\\', '/'));
            if (!moduleNames.Contains(referenced))
            {
                continue;
            }

            if (!Regex.IsMatch(tag.Value, @"ReferenceOutputAssembly\s*=\s*""false""", RegexOptions.IgnoreCase))
            {
                violations.Add($"AST.csproj -> {referenced} (ProjectReference is missing ReferenceOutputAssembly=\"false\")");
            }
        }

        // (b) no compile-time `using AST.Modules.*;` anywhere in the exe's own source, and no hardcoded
        // moduleCatalog.AddModule<T>() -- the module list must come from DirectoryModuleCatalog at runtime.
        foreach (var cs in Directory.EnumerateFiles(exeDir, "*.cs", SearchOption.AllDirectories))
        {
            if (MetaTest.IsGenerated(root, cs))
            {
                continue;
            }

            var text = File.ReadAllText(cs);
            var relative = Path.GetRelativePath(root, cs);

            if (UsingModuleDirective.IsMatch(text))
            {
                violations.Add($"{relative}: `using AST.Modules.*` (compile-time reference to a business module)");
            }

            if (AddModuleCall.IsMatch(text))
            {
                violations.Add($"{relative}: hardcoded AddModule<>() (use DirectoryModuleCatalog instead)");
            }
        }

        Assert.True(
            violations.Count == 0,
            "rule-module-boundary §1b (Phase 2): the exe must not compile against a business module or hardcode "
                + "its module list -- DirectoryModuleCatalog discovers modules from Modules/ at runtime instead:\n  "
                + string.Join("\n  ", violations));
    }

    // Production business-module projects = AST.Modules.*, excluding their .Tests companions (test projects may
    // reference the Shell and their own module for fixtures — the boundary rule governs shipped modules).
    private static IEnumerable<(string Name, string Csproj)> BusinessModuleProjects(string root)
    {
        foreach (var csproj in Directory.EnumerateFiles(root, "AST.Modules.*.csproj", SearchOption.AllDirectories))
        {
            if (MetaTest.IsGenerated(root, csproj))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(csproj);
            if (!name.EndsWith(".Tests", StringComparison.Ordinal))
            {
                yield return (name, csproj);
            }
        }
    }

    private static bool IsForbiddenModuleDependency(string moduleName, string referenced) =>
        referenced.Equals("AST", StringComparison.Ordinal)                  // the WPF exe / composition root
        || referenced.Equals("AST.Shell", StringComparison.Ordinal)         // the shell / composition host
        || (referenced.StartsWith("AST.Modules.", StringComparison.Ordinal) // another business module
            && !referenced.Equals(moduleName, StringComparison.Ordinal));
}
