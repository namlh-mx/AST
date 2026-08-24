using FluentAssertions;

namespace AST.Meta.Tests;

// META guard — an AST-owned XAML resource key that nothing references is dead weight that
// accumulates silently: the compiler never sees it and no runtime path fails.
//
// What this guard claims is deliberately narrow. It does NOT claim the tree is free of orphans:
// seven exist today and are permitted by name, because cleaning them up is its own task
// (rule-shared-components — they are design-system surfaces). It claims that no EIGHTH appears
// without someone deciding to allow it. It says nothing about WPF-UI's keys.
public class XamlResourceGraphTests
{
    // Measured 2026-08-19 with whole-solution C# edges and comments stripped. An earlier baseline
    // had the same COUNT and a different MEMBERSHIP — two errors that cancelled. That is why this
    // is a named set and both assertions below compare sets, never sizes.
    private static readonly string[] PermittedOrphans =
    {
        "AstConnectingBrush",
        "AstHeadlineLargeText",
        "AstLabelSmallText",
        "AstOnPrimaryBrush",
        "AstPrimaryDarkBrush",
        "AstRadiusLarge",
        "AstSectionGap",
    };

    [Fact]
    public void NoNewOrphanResourceKey()
    {
        var graph = XamlResourceGraph.Load(MetaTest.RepoRoot());

        var orphans = graph.Definitions
            .Where(d => d.File != XamlResourceGraph.WpfUiOverridesFile)
            .Select(d => d.Key)
            .Distinct(StringComparer.Ordinal)
            .Where(k => !graph.ReferencedKeys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var appeared = orphans.Except(PermittedOrphans, StringComparer.Ordinal).ToList();
        var resolved = PermittedOrphans.Except(orphans, StringComparer.Ordinal).ToList();

        appeared.Should().BeEmpty(
            "a NEW unused AST XAML resource key was added - reference it, delete it, or add it to "
            + "PermittedOrphans with a reason:\n  " + string.Join("\n  ", appeared));

        resolved.Should().BeEmpty(
            "these keys are no longer orphans - remove them from PermittedOrphans so the baseline "
            + "shrinks instead of rotting:\n  " + string.Join("\n  ", resolved));
    }

    // META guard — XamlResourceGraph's universe is "every file under a project directory named in
    // AST.slnx". That equals what MSBuild compiles only while two things hold: no project opts out
    // of the default item globs, and no project nests inside another. A violation is silent in
    // exactly the wrong direction - a file nobody compiles still gets scanned, and one quoted key
    // in it erases a real orphan. Measured 2026-08-19: 13 projects, 0 opt-outs, 0 nesting.
    //
    [Fact]
    public void NoProjectOptsOutOfTheDefaultItemGlobs()
    {
        var root = MetaTest.RepoRoot();
        var projects = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !MetaTest.IsGenerated(root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        string[] optOuts = { "EnableDefaultCompileItems", "EnableDefaultItems", "<Compile ", "<Page " };

        var handManaged = projects
            .Where(f => optOuts.Any(o => File.ReadAllText(f).Contains(o, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        handManaged.Should().BeEmpty(
            "this guard scans project DIRECTORIES; a project that hand-manages its items breaks "
            + "directory == compiled set, so the orphan guard starts reading files nobody builds:\n  "
            + string.Join("\n  ", handManaged));

        var nested = projects
            .Where(f => projects.Any(other => other != f
                && Path.GetDirectoryName(f)!.StartsWith(
                    Path.GetDirectoryName(other)! + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        nested.Should().BeEmpty(
            "a nested project makes the parent's directory scan swallow the child's files:\n  "
            + string.Join("\n  ", nested));
    }

    // META guard — App.xaml merges five AST dictionaries into ONE Application.Resources scope, so
    // two global-scope definitions of the same key silently resolve to whichever won the merge.
    // Measured 2026-08-19: 0 such collisions.
    //
    // The grouping is by KEY, not by file, and that is a correction made while executing the plan.
    // The plan excluded same-file duplicates on the premise that the XAML compiler rejects them,
    // and required that premise to be verified rather than assumed. It was verified and it is
    // FALSE: adding a second <Thickness x:Key="AstCardPadding"> to Spacing.xaml builds clean. So
    // nothing in the build catches a same-file collision either, and excluding it would have left
    // the easier half of the hazard unguarded.
    //
    // Two boundaries remain deliberate, and both are narrower than "any collision in the merged
    // scope":
    //   - AST-owned keys only. App.xaml also merges ui:ThemesDictionary and ui:ControlsDictionary,
    //     which are NOT inventoried - WpfUiOverrides.xaml exists precisely to override WPF-UI keys,
    //     so a cross-package collision is the intended design, not a defect.
    //   - Global scope only. A key inside ControlTemplate.Resources or a view's local Resources is a
    //     different scope, even when written with an explicit <ResourceDictionary> wrapper, which is
    //     a supported shape because FrameworkTemplate.Resources IS a ResourceDictionary.
    [Fact]
    public void NoDuplicateAstKeyInTheAppGlobalMergedScope()
    {
        var graph = XamlResourceGraph.Load(MetaTest.RepoRoot());
        var inScope = graph.MergedScopeFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var collisions = graph.Definitions
            .Where(d => d.IsGlobalScope && inScope.Contains(d.File))
            .GroupBy(d => d.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key + " <- " + string.Join(", ", g.Select(d => d.File)))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        collisions.Should().BeEmpty(
            "these AST keys are defined more than once at the global scope of the merged "
            + "dictionaries, so one silently shadows the other:\n  " + string.Join("\n  ", collisions));
    }

    // The C# edge of this graph is only as good as the lexer that decides what is code and what is a
    // comment, and R2 shipped one with NO state for raw string literals ("""), of which this tree
    // has 53 across 23 files. A quote the lexer mis-reads desynchronises it, and from that point a
    // // inside ordinary content reads as a comment start: quoted keys after it become invisible and
    // a LIVE key gets reported as an orphan. These cases pin the forms that can do it.
    //
    // Each source ends with a REAL comment naming a probe key, so every case proves both halves at
    // once - the comment goes, the literal survives verbatim. The last case is different in kind:
    // it exists to falsify one line of the lexer, and it is the only case that fails without it.
    //
    [Theory]
    [MemberData(nameof(CommentStrippingCases))]
    public void StripCommentsDropsCommentsAndKeepsLiterals(string label, string source, string mustSurvive)
    {
        var stripped = XamlResourceGraph.StripComments(source);

        stripped.Should().NotContain(
            "\"AstProbeKeyInComment\"", $"the trailing comment must be removed ({label})");
        stripped.Should().Contain(
            mustSurvive, $"the literal must survive the lexer intact ({label})");
    }

    public static TheoryData<string, string, string> CommentStrippingCases => new()
    {
        {
            "ordinary literal with an escaped quote",
            """var s = "a\"b"; var k = "AstCardPadding"; // "AstProbeKeyInComment" """,
            "\"AstCardPadding\""
        },
        {
            "verbatim literal with a doubled quote",
            """var s = @"a""b"; var k = "AstCardPadding"; // "AstProbeKeyInComment" """,
            "\"AstCardPadding\""
        },
        {
            "pack URI - the // inside it is not a comment",
            """var s = "pack://application:,,,/AST.UI;component/Palette.xaml"; // "AstProbeKeyInComment" """,
            "pack://application"
        },
        {
            "char literal holding a quote",
            """var c = '"'; var k = "AstCardPadding"; // "AstProbeKeyInComment" """,
            "\"AstCardPadding\""
        },
        {
            "raw literal containing quotes and a slash-slash",
            """"var s = """he said "hi" // still content"""; var k = "AstCardPadding"; // "AstProbeKeyInComment" """",
            "// still content"
        },
        {
            "raw INTERPOLATED literal - 14 of these in this tree",
            """"var s = $"""value: {x} // still content"""; var k = "AstCardPadding"; // "AstProbeKeyInComment" """",
            "value: {x} // still content"
        },
        {
            // The case that falsifies the !verbatim guard specifically. @"""" is a VERBATIM literal
            // holding one doubled quote; read as a raw literal it opens a four-quote delimiter that
            // never closes, so the lexer swallows the rest of the file and the trailing comment
            // survives. Without the guard THIS case fails and the six above still pass.
            "verbatim literal that looks like a raw one",
            """""var s = @""""; var k = "AstCardPadding"; // "AstProbeKeyInComment" """"",
            "\"AstCardPadding\""
        },
    };
}
