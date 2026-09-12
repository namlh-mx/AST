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
    private static readonly Regex RegistryRow = new(
        @"^\|\s*`(?<name>[^`]+)`\s*\|\s*(?:`(?<path>[^`]+)`|(?<plain>[^|]+?))\s*\|(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex TableSeparatorRow = new(
        @"^\|(?:\s*:?-+:?\s*\|)+\s*$",
        RegexOptions.Compiled);
    private static readonly string[] PublicSectionPrefixes =
    {
        "## ①", "## ②", "## ③", "## ④", "## ⑤", "## ⑥", "## ⑦", "## ⑧", "## ⑨"
    };

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

    [Fact]
    public void RegistryRowsAreUniqueByNameAndLocationAndCarryLocked()
    {
        var rows = RegistryRows(MetaTest.RepoRoot());
        Assert.True(rows.Count > 0, "the registry must parse at least one data row");

        var invalidLocked = rows
            .Where(row => row.Locked is not ("yes" or "no"))
            .Select(row => $"`{row.Name}` Locked={row.Locked}")
            .ToList();
        Assert.True(
            invalidLocked.Count == 0,
            "every registry row with a customization boundary must carry Locked yes or no:\n  "
                + string.Join("\n  ", invalidLocked));

        var duplicates = rows
            .GroupBy(row => (row.Name, row.Location))
            .Where(group => group.Count() > 1)
            .Select(group => $"`{group.Key.Name}` | `{group.Key.Location}` x{group.Count()}")
            .ToList();
        Assert.True(
            duplicates.Count == 0,
            "registry rows must be unique by name plus location:\n  "
                + string.Join("\n  ", duplicates));
    }

    [Fact]
    public void AstOverlayHostHasExactlyOneLockedControlRowAndOneLockedStyleRow()
    {
        var rows = RegistryRows(MetaTest.RepoRoot());
        Assert.Equal(
            1,
            CountRows(rows, "AstOverlayHost", "AST.UI/Controls/AstOverlayHost.cs", "yes"));
        Assert.Equal(
            1,
            CountRows(rows, "AstOverlayHost", "Controls.xaml", "yes"));
    }

    [Fact]
    public void RegistrySectionTablesKeepPublicHeaders()
    {
        var headers = SectionTableHeaders(Registry(MetaTest.RepoRoot()));
        Assert.Equal(9, headers.Count);
        var fourColumn = new[] { "Component", "Home", "Purpose", "Customization boundary" };
        var tokenColumn = new[] { "Token", "Kind", "Note" };
        for (var i = 0; i < headers.Count; i++)
        {
            var expected = i == 2 ? tokenColumn : fourColumn;
            Assert.True(
                headers[i].SequenceEqual(expected),
                $"section {i + 1} header is [{string.Join(" | ", headers[i])}], expected [{string.Join(" | ", expected)}]; a renamed column is a structural change");
        }
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

    private static IReadOnlyList<(string Name, string Location, string Locked)> RegistryRows(string root)
    {
        var registry = File.ReadAllText(Path.Combine(root, "docs", "shared-components.md"));
        return RegistryRow.Matches(registry)
            .Select(match =>
            {
                var location = (match.Groups["path"].Success
                    ? match.Groups["path"].Value
                    : match.Groups["plain"].Value).Trim();
                var rest = match.Groups["rest"].Value;
                var locked =
                    rest.Contains("Unlocked", StringComparison.Ordinal)
                    || rest.Contains("NOT LOCKED", StringComparison.Ordinal)
                        ? "no"
                        : rest.Contains("LOCKED:", StringComparison.Ordinal)
                            ? "yes"
                            : "";
                return (match.Groups["name"].Value, location, locked);
            })
            .Where(row => row.Item3.Length > 0)
            .ToList();
    }

    private static int CountRows(
        IEnumerable<(string Name, string Location, string Locked)> rows,
        string name,
        string location,
        string locked) =>
        rows.Count(row => row.Name == name && row.Location == location && row.Locked == locked);

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

    private static IReadOnlyList<IReadOnlyList<string>> SectionTableHeaders(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var headers = new List<IReadOnlyList<string>>();
        var expected = 0;
        for (var i = 0; i < lines.Length && expected < PublicSectionPrefixes.Length; i++)
        {
            if (!lines[i].StartsWith(PublicSectionPrefixes[expected], StringComparison.Ordinal))
                continue;

            for (var j = i + 1; j < lines.Length; j++)
            {
                if (expected + 1 < PublicSectionPrefixes.Length
                    && lines[j].StartsWith(PublicSectionPrefixes[expected + 1], StringComparison.Ordinal))
                    break;
                if (!TableSeparatorRow.IsMatch(lines[j]) || j == 0 || !lines[j - 1].StartsWith('|'))
                    continue;
                headers.Add(HeaderCells(lines[j - 1]));
                break;
            }

            expected++;
        }

        return headers;
    }

    private static IReadOnlyList<string> HeaderCells(string headerLine) =>
        headerLine.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
