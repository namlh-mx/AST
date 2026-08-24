using System.Text.RegularExpressions;

namespace AST.Meta.Tests;

// Guards docs/shared-components.md against the code both ways: nothing shared is unregistered (UI structural
// signal + the backend marker attribute), and no registry entry dangles. Source-scan only — references no
// product assembly. The marker match is anchored to the declaration directly under the attribute — tolerant of
// intervening doc/line comments and stacked attributes, of any modifier order, and of `record struct` /
// `record class` — so it catches every real declaration form and cannot be fooled by comments between.
public class SharedComponentRegistryTests
{
    // [SharedComponent] then (skipping blank lines, // or /// comments, and stacked [Attributes]) the type
    // declaration. Modifier set is exhaustive; `record struct`/`record class` are matched before bare `record`
    // so the type NAME is captured, not the "struct"/"class" keyword.
    private static readonly Regex Marker = new(
        @"\[SharedComponent\][^\S\r\n]*\r?\n(?:[^\S\r\n]*(?://[^\r\n]*|\[[^\]]*\])?\r?\n)*\s*(?:(?:public|internal|private|protected|sealed|abstract|static|readonly|partial)\s+)*(?:record\s+struct|record\s+class|interface|class|struct|record|enum)\s+(\w+)",
        RegexOptions.Compiled);

    // Structural type-declaration match (no marker gate) — used ONLY for AST.Shell/Presentation, where every
    // file is a single curated contract. If a Presentation file ever gains a nested/private helper type, this
    // would demand it be registered; keep those files one-type-each, or the guard is telling you to split it.
    private static readonly Regex TypeDecl = new(
        @"(?:(?:public|internal|private|protected|sealed|abstract|static|readonly|partial)\s+)*(?:record\s+struct|record\s+class|interface|class|struct|record|enum)\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex XamlKey = new("x:Key=\"(Ast[A-Za-z0-9]+)\"", RegexOptions.Compiled);

    private static string Registry(string root) =>
        File.ReadAllText(Path.Combine(root, "docs", "shared-components.md"));

    // The four DesignSystem files whose Ast* x:Keys ARE shared components (styles in Controls.xaml + the three
    // token files). Both the completeness check and the known-symbol set use exactly this list, so the two
    // directions stay symmetric — e.g. WpfUiOverrides.xaml (theme overrides, not shared components) is in
    // neither, so it can never be a one-directional hole.
    private static IEnumerable<string> DesignSystemKeyFiles(string root) =>
        new[] { "Controls.xaml", "Palette.xaml", "Typography.xaml", "Spacing.xaml" }
            .Select(f => Path.Combine(root, "AST.UI", "Resources", "DesignSystem", f));

    [Fact]
    public void EveryUiSharedComponentIsRegistered()
    {
        var root = MetaTest.RepoRoot();
        var reg = Registry(root);
        var missing = new List<string>();

        // (1) controls: the type name of each AST.UI/Controls/*.cs
        foreach (var cs in Directory.EnumerateFiles(Path.Combine(root, "AST.UI", "Controls"), "*.cs"))
        {
            var name = ControlName(cs);
            if (!reg.Contains($"`{name}`", StringComparison.Ordinal))
            {
                missing.Add($"control {name}");
            }
        }

        // (2) shared styles + (3) live tokens: every Ast* x:Key in the four DesignSystem key files
        foreach (var xaml in DesignSystemKeyFiles(root))
        {
            var kind = xaml.EndsWith("Controls.xaml", StringComparison.Ordinal) ? "style" : "token";
            foreach (Match m in XamlKey.Matches(File.ReadAllText(xaml)))
            {
                var key = m.Groups[1].Value;
                if (!reg.Contains($"`{key}`", StringComparison.Ordinal))
                {
                    missing.Add($"{kind} {key}");
                }
            }
        }

        // (4) converters: the type name of each AST.UI/Converters/*.cs
        foreach (var cs in Directory.EnumerateFiles(Path.Combine(root, "AST.UI", "Converters"), "*.cs"))
        {
            var name = Path.GetFileNameWithoutExtension(cs);
            if (!reg.Contains($"`{name}`", StringComparison.Ordinal))
            {
                missing.Add($"converter {name}");
            }
        }

        // (5) presentation contracts: every type declared under AST.Shell/Presentation
        foreach (var cs in Directory.EnumerateFiles(Path.Combine(root, "AST.Shell", "Presentation"), "*.cs"))
        {
            foreach (var t in TypeNames(File.ReadAllText(cs)))
            {
                if (!reg.Contains($"`{t}`", StringComparison.Ordinal))
                {
                    missing.Add($"presentation {t}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "docs/shared-components.md is missing UI shared components — add a row for each:\n  "
                + string.Join("\n  ", missing));
    }

    [Fact]
    public void EverySharedComponentMarkedTypeIsRegistered()
    {
        var root = MetaTest.RepoRoot();
        var reg = Registry(root);
        var missing = new List<string>();

        foreach (var (name, file) in MarkedTypes(root))
        {
            if (!reg.Contains($"`{name}`", StringComparison.Ordinal))
            {
                missing.Add($"{name} ({file})");
            }
        }

        Assert.True(
            missing.Count == 0,
            "These [SharedComponent] types are not in docs/shared-components.md — register each:\n  "
                + string.Join("\n  ", missing));
    }

    [Fact]
    public void NoRegistryEntryDangles()
    {
        var root = MetaTest.RepoRoot();
        var reg = Registry(root);
        var known = KnownSymbols(root);
        var dangling = new List<string>();

        // Each registry row leads with its component in backticks (| `Name` | ...). Every such symbol must
        // resolve to a real x:Key, control, converter, presentation type, or [SharedComponent]-marked type.
        foreach (Match m in Regex.Matches(reg, @"^\|\s*`([^`]+)`", RegexOptions.Multiline))
        {
            var token = m.Groups[1].Value;
            if (!known.Contains(token))
            {
                dangling.Add(token);
            }
        }

        Assert.True(
            dangling.Count == 0,
            "docs/shared-components.md cites components that no longer resolve — fix/remove each:\n  "
                + string.Join("\n  ", dangling));
    }

    private static string ControlName(string csPath) =>
        Path.GetFileNameWithoutExtension(csPath).Replace(".xaml", "", StringComparison.Ordinal);

    private static IEnumerable<(string Name, string File)> MarkedTypes(string root)
    {
        foreach (var cs in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcluded(root, cs))
            {
                continue;
            }

            foreach (Match m in Marker.Matches(File.ReadAllText(cs)))
            {
                yield return (m.Groups[1].Value, Path.GetRelativePath(root, cs));
            }
        }
    }

    private static IEnumerable<string> TypeNames(string csText) =>
        TypeDecl.Matches(csText).Select(m => m.Groups[1].Value);

    private static HashSet<string> KnownSymbols(string root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var xaml in DesignSystemKeyFiles(root))
        {
            foreach (Match m in XamlKey.Matches(File.ReadAllText(xaml)))
            {
                set.Add(m.Groups[1].Value);
            }
        }

        foreach (var cs in Directory.EnumerateFiles(Path.Combine(root, "AST.UI", "Controls"), "*.cs"))
        {
            set.Add(ControlName(cs));
        }

        foreach (var cs in Directory.EnumerateFiles(Path.Combine(root, "AST.UI", "Converters"), "*.cs"))
        {
            set.Add(Path.GetFileNameWithoutExtension(cs));
        }

        foreach (var cs in Directory.EnumerateFiles(Path.Combine(root, "AST.Shell", "Presentation"), "*.cs"))
        {
            foreach (var t in TypeNames(File.ReadAllText(cs)))
            {
                set.Add(t);
            }
        }

        foreach (var (name, _) in MarkedTypes(root))
        {
            set.Add(name);
        }

        return set;
    }

    // Skip build output and this test project itself — the guard's own source mentions the marker as text,
    // and must never scan itself as if it declared a shared component.
    private static bool IsExcluded(string root, string path)
    {
        if (MetaTest.IsGenerated(root, path))
        {
            return true;
        }

        var rel = Path.GetRelativePath(root, path);
        return rel.StartsWith($"AST.Meta.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
