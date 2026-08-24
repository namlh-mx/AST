using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AST.Meta.Tests;

// A resource key definition. IsGlobalScope answers "does this land in Application.Resources?" -
// which is NOT the same as "its parent element is a ResourceDictionary": FrameworkTemplate.Resources
// IS a ResourceDictionary and may be written with an explicit <ResourceDictionary> wrapper, so an
// element-shape test classifies a template-private resource as global and invents a collision.
internal sealed record KeyDefinition(string Key, string File, bool IsGlobalScope);

internal sealed class XamlResourceGraph
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // WPF-UI's own control templates consume these keys; no AST file references them, and that is by
    // design. Excluded as a WHOLE FILE by its documented role - never key-by-key, because a
    // hand-maintained list of WPF-UI key names would be a claim nothing checks.
    public const string WpfUiOverridesFile = "AST.UI/Resources/DesignSystem/WpfUiOverrides.xaml";

    // The (?!\{) is load-bearing: BasedOn="{StaticResource {x:Type ui:Button}}" is a nested markup
    // extension whose key is a TYPE, not an AST string key. Without it the captured token is the
    // literal "{x:Type". Verified against this tree: all 17 nested forms present are x:Type, no
    // ResourceKey= form exists, and every real key fits the token class.
    private static readonly Regex Reference = new(
        @"\{\s*(?:x:)?(?:Static|Dynamic)Resource\s+(?:ResourceKey\s*=\s*)?(?!\{)([^\s},]+)",
        RegexOptions.Compiled);

    // Any StaticResource/DynamicResource occurrence the pattern above does NOT capture. Unsupported
    // grammar FAILS CLOSED rather than being silently dropped: a form this model cannot read is a
    // reference it cannot see, which would turn into a false orphan report.
    private static readonly Regex AnyResourceUse = new(
        @"\{\s*(?:x:)?(?:Static|Dynamic)Resource\b",
        RegexOptions.Compiled);

    // The property-ELEMENT spelling of the two resource extensions, with and without the
    // "Extension" suffix XAML lets you drop. Kept as a named set rather than inlined so the
    // element path and the attribute path are visibly the same universe.
    private static readonly HashSet<string> ResourceElementNames = new(StringComparer.Ordinal)
    {
        "StaticResource",
        "DynamicResource",
        "StaticResourceExtension",
        "DynamicResourceExtension",
    };

    private XamlResourceGraph(
        IReadOnlyList<KeyDefinition> definitions,
        IReadOnlySet<string> referencedKeys,
        IReadOnlyList<string> mergedScopeFiles)
    {
        Definitions = definitions;
        ReferencedKeys = referencedKeys;
        MergedScopeFiles = mergedScopeFiles;
    }

    public IReadOnlyList<KeyDefinition> Definitions { get; }

    public IReadOnlySet<string> ReferencedKeys { get; }

    // The app-global merged scope, in App.xaml merge order. AST dictionaries only: the two WPF-UI
    // dictionaries App.xaml also merges are deliberately NOT inventoried, because WpfUiOverrides.xaml
    // exists precisely to collide with them. Cross-package collisions are therefore outside this
    // model's universe and outside every claim it makes.
    public IReadOnlyList<string> MergedScopeFiles { get; }

    public static XamlResourceGraph Load(string repoRoot)
    {
        var xamlFiles = SolutionFiles(repoRoot, "*.xaml")
            .Select(f => Rel(repoRoot, f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var definitions = new List<KeyDefinition>();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = new List<string>();

        foreach (var rel in xamlFiles)
        {
            var root = XDocument.Load(Path.Combine(repoRoot, rel)).Root!;
            foreach (var el in root.DescendantsAndSelf())
            {
                var key = el.Attribute(X + "Key")?.Value;
                if (key is not null)
                {
                    definitions.Add(new KeyDefinition(key, rel, IsGlobalScope(el, root)));
                }

                // A markup extension may also be written as a property ELEMENT -
                // <DynamicResource ResourceKey="AstFoo" /> - and R3 saw none of it: the model read
                // attribute and text VALUES only, so this form produced no edge AND no throw while
                // the plan claimed unsupported forms throw. A live key would be reported orphan.
                // (Whether WPF accepts this form at all is settled by the build in
                // Task 1 Step 5 mutation 9, not asserted here.)
                if (ResourceElementNames.Contains(el.Name.LocalName))
                {
                    var elementKey = el.Attribute("ResourceKey")?.Value;
                    if (elementKey is null)
                    {
                        unreadable.Add($"{rel}: <{el.Name.LocalName}> with no ResourceKey attribute");
                    }
                    else
                    {
                        referenced.Add(elementKey);
                    }
                }

                foreach (var attr in el.Attributes())
                {
                    Harvest(attr.Value, rel, referenced, unreadable);
                }
            }

            foreach (var text in root.DescendantNodes().OfType<XText>())
            {
                Harvest(text.Value, rel, referenced, unreadable);
            }
        }

        if (unreadable.Count > 0)
        {
            // Fail LOUDLY, never return a graph known to be incomplete: an unreadable reference is
            // an edge this model cannot see, and a missing edge turns a live key into a false orphan.
            // The message asks for the MODEL to be extended - the XAML is presumed correct.
            throw new InvalidOperationException(
                "XamlResourceGraph cannot parse these resource-reference forms; extend the model "
                + "rather than the baseline:\n  " + string.Join("\n  ", unreadable));
        }

        AddCSharpReferences(repoRoot, definitions, referenced);

        return new XamlResourceGraph(definitions, referenced, ReadMergedScope(repoRoot));
    }

    // A definition is in the app-global scope when, walking up from it, the first "<Type>.Resources"
    // property element encountered is Application.Resources - or there is none at all and the
    // document root is a ResourceDictionary (a standalone merged dictionary file). Any other
    // .Resources ancestor (ControlTemplate.Resources, UserControl.Resources, Style.Resources) means
    // the key is private to that scope. Plain <ResourceDictionary> wrappers are skipped on the way
    // up, which is exactly what makes an explicit wrapper inside a template classify correctly.
    private static bool IsGlobalScope(XElement el, XElement root)
    {
        for (var a = el.Parent; a is not null; a = a.Parent)
        {
            var name = a.Name.LocalName;
            if (name.EndsWith(".Resources", StringComparison.Ordinal))
            {
                return name.Equals("Application.Resources", StringComparison.Ordinal);
            }
        }

        return root.Name.LocalName.Equals("ResourceDictionary", StringComparison.Ordinal);
    }

    private static void Harvest(string value, string file, HashSet<string> into, List<string> unreadable)
    {
        // A value beginning with {} is the XAML escape: everything after it is LITERAL TEXT, not a
        // markup extension. R3 matched inside it, so "{}{StaticResource AstFoo}" - a caption that
        // displays those characters - minted a phantom edge and would have kept a genuinely unused
        // AstFoo GREEN forever. Not theoretical here: this tree already uses the escape twice, at
        // ConnectionDeclarationView.xaml:334 and :346 (StringFormat="{}{0} ({1})"). Skipping the
        // whole value is correct rather than conservative - the escape governs the entire value.
        //
        if (value.TrimStart().StartsWith("{}", StringComparison.Ordinal))
        {
            return;
        }

        var captured = Reference.Matches(value).Count;
        var present = AnyResourceUse.Matches(value).Count;
        if (present > captured + CountNestedTypeForms(value))
        {
            unreadable.Add($"{file}: {value.Trim()}");
        }

        foreach (Match m in Reference.Matches(value))
        {
            into.Add(m.Groups[1].Value);
        }
    }

    // {StaticResource {x:Type ...}} - a type-keyed lookup, deliberately not an AST string key, and
    // therefore the ONE nested form this model is allowed to absorb silently.
    //
    // R2 matched any nested markup extension here, which made the fail-closed claim above false: a
    // perfectly valid {DynamicResource {x:Static SystemColors.ControlBrushKey}} produced no edge and
    // no throw, so a permitted orphan that became referenced through x:Static would have stayed
    // falsely in the baseline. Narrowed to x:Type; every other nested form now reaches `unreadable`.
    //
    private static int CountNestedTypeForms(string value) =>
        Regex.Matches(value, @"\{\s*(?:x:)?(?:Static|Dynamic)Resource\s+\{\s*x:Type\b").Count;

    // A key is "referenced from C#" when its name appears as a quoted literal in NON-COMMENT C#,
    // anywhere in the solution except a TEST project. All three halves are load-bearing and each was
    // wrong at some point:
    //   - Scope: AST.Core owns the quoted values for three brushes. Scanning only the UI projects
    //     reported all three as orphans.
    //   - Comments: AST/MainWindow.xaml.cs names a key in a comment while hard-coding its value, and
    //     AST.UI/Controls/AstOrgUnitPicker.cs quotes x:Key="AstOrgUnitPicker" in a comment. Counting
    //     a comment as a reference lets a genuinely unused key stay GREEN forever.
    //   - Test projects: a key named in a test is named by something ASSERTING ABOUT the resource,
    //     never by something USING it, so it is not a reference edge. This project was excluded from
    //     the start for that reason - PermittedOrphans would otherwise mark every baseline key as
    //     referenced and the guard would silently certify itself - and the rule is a property of test
    //     projects, not of this one. Generalised 2026-08-20, BEFORE the
    //     runtime half's AST.AppScope.Tests exists: its baselines quote 30 keys, and under the old
    //     one-project exclusion each would have become a reference edge, keeping a resource GREEN
    //     because a test named it long after the app stopped using it.
    // Residue, declared: a quoted key in dead code or in an unrelated non-comment string still
    // counts, and so does one inside an interpolation hole in a raw literal, whose content is copied
    // verbatim by design. Closing that needs Roslyn, and Roslyn needs a ProjectReference this
    // project refuses.
    private static void AddCSharpReferences(
        string repoRoot, IReadOnlyList<KeyDefinition> definitions, HashSet<string> into)
    {
        var sources = SolutionFiles(repoRoot, "*.cs")
            .Select(f => Rel(repoRoot, f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !IsTestProjectFile(f))
            .Select(f => Path.Combine(repoRoot, f))
            .ToList();

        var candidates = definitions.Select(d => d.Key).Distinct(StringComparer.Ordinal).ToList();

        foreach (var file in sources)
        {
            var body = StripComments(File.ReadAllText(file));
            foreach (var key in candidates)
            {
                if (body.Contains('"' + key + '"', StringComparison.Ordinal))
                {
                    into.Add(key);
                }
            }
        }
    }

    // A repo-relative path whose FIRST segment is a test project. Matched on the segment, not with
    // Contains: a production file under a directory merely named "…Tests…" is not a test file, and a
    // production project would have to be renamed to opt out of being scanned.
    private static bool IsTestProjectFile(string relativePath)
    {
        var firstSeparator = relativePath.IndexOf('/');
        var project = firstSeparator < 0 ? relativePath : relativePath[..firstSeparator];
        return project.EndsWith(".Tests", StringComparison.Ordinal);
    }

    // Remove // and /* */ comments without touching string or char literals. A naive regex breaks on
    // "pack://application:,,,/..." - the // inside that literal is not a comment.
    //
    // internal, not private: the raw / verbatim / escaped-quote / char / pack-URI forms are pinned
    // by direct cases in XamlResourceGraphTests. A lexer nobody can call is a lexer nobody can
    // falsify, and this one shipped wrong once.
    internal static string StripComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        for (var i = 0; i < src.Length;)
        {
            var c = src[i];
            if (c == '"')
            {
                var verbatim = i > 0 && src[i - 1] == '@';

                // A run of THREE OR MORE quotes opens a raw string literal, whose content runs
                // verbatim to the next run of at least that length. Raw literals carry no escapes,
                // so nothing inside one can be mistaken for a delimiter - and until R3 this lexer
                // had no state for them at all (53 in 23 files here, 14 of them the $""" form).
                // The !verbatim guard is load-bearing: @"""" is a VERBATIM literal holding one
                // doubled quote, not a raw literal, and reading it as raw desynchronises the
                // lexer - which is the exact failure this branch exists to prevent.
                var run = QuoteRun(src, i);
                if (!verbatim && run >= 3)
                {
                    sb.Append(src, i, run);
                    i += run;
                    while (i < src.Length)
                    {
                        if (src[i] != '"')
                        {
                            sb.Append(src[i]);
                            i++;
                            continue;
                        }

                        var close = QuoteRun(src, i);
                        sb.Append(src, i, close);
                        i += close;
                        if (close >= run)
                        {
                            break;
                        }
                    }

                    continue;
                }

                sb.Append(c);
                i++;
                while (i < src.Length)
                {
                    if (verbatim)
                    {
                        if (src[i] == '"' && i + 1 < src.Length && src[i + 1] == '"')
                        {
                            sb.Append(src[i], 2);
                            i += 2;
                            continue;
                        }

                        sb.Append(src[i]);
                        if (src[i++] == '"')
                        {
                            break;
                        }
                    }
                    else
                    {
                        if (src[i] == '\\' && i + 1 < src.Length)
                        {
                            sb.Append(src[i]).Append(src[i + 1]);
                            i += 2;
                            continue;
                        }

                        sb.Append(src[i]);
                        if (src[i++] == '"')
                        {
                            break;
                        }
                    }
                }

                continue;
            }

            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < src.Length)
                {
                    if (src[i] == '\\' && i + 1 < src.Length)
                    {
                        sb.Append(src[i]).Append(src[i + 1]);
                        i += 2;
                        continue;
                    }

                    sb.Append(src[i]);
                    if (src[i++] == '\'')
                    {
                        break;
                    }
                }

                continue;
            }

            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                {
                    i++;
                }

                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int QuoteRun(string src, int i)
    {
        var n = 0;
        while (i + n < src.Length && src[i + n] == '"')
        {
            n++;
        }

        return n;
    }

    // App.xaml's AST MergedDictionaries, mapped from pack:// URIs back to repository paths, plus
    // App.xaml itself (its direct Application.Resources entries land in the same scope). Parsed
    // rather than hard-coded so the list cannot drift from the merge it describes.
    private static IReadOnlyList<string> ReadMergedScope(string repoRoot)
    {
        const string AppXaml = "AST/App.xaml";
        var root = XDocument.Load(Path.Combine(repoRoot, AppXaml)).Root!;

        var files = new List<string>();
        foreach (var el in root.Descendants())
        {
            var source = el.Attribute("Source")?.Value;
            if (source is null)
            {
                continue;
            }

            const string Marker = ";component/";
            var i = source.IndexOf(Marker, StringComparison.Ordinal);
            if (i < 0)
            {
                continue;
            }

            var assembly = source[..i];
            assembly = assembly[(assembly.LastIndexOf('/') + 1)..];
            files.Add(assembly + "/" + source[(i + Marker.Length)..]);
        }

        files.Add(AppXaml);
        return files;
    }

    // Enumeration is bound to the SOLUTION, not the repository. `templates/skeletons/` holds
    // SampleView.xaml, SampleView.xaml.cs and SampleViewModel.cs - repository files that no project
    // compiles (21 XAML repo-wide against 20 in AST.slnx). Repo-wide enumeration would let a quoted
    // key in a skeleton erase a real production orphan.
    //
    // The list is PARSED from AST.slnx rather than globbed, for the same reason ReadMergedScope
    // parses App.xaml: the R2 baseline came from a script globbing `AST*/**` while the R2 model read
    // the whole repository, so the measurement and the implementation never shared their inputs.
    // Today those two happen to coincide - every top-level AST* directory is a solution project.
    // Reading AST.slnx is what keeps them coinciding when that stops being true.
    //
    // What this still does NOT give you: a project DIRECTORY is not MSBuild's evaluated item set.
    // A .cs excluded from compilation, or a project nested inside another, would be scanned anyway
    // and its quoted key could erase a real orphan. Both assumptions hold today and neither is
    // checked by the compiler, so NoProjectOptsOutOfTheDefaultItemGlobs guards them instead of this
    // comment promising them. (the honest fix is a falsifiable guard, not a wider
    // claim and not an MSBuild dependency AST.Meta.Tests refuses.)
    private static IEnumerable<string> SolutionFiles(string repoRoot, string pattern) =>
        SolutionProjectDirectories(repoRoot)
            .SelectMany(d => Directory.EnumerateFiles(d, pattern, SearchOption.AllDirectories))
            .Where(f => !MetaTest.IsGenerated(repoRoot, f));

    private static IReadOnlyList<string> SolutionProjectDirectories(string repoRoot)
    {
        var root = XDocument.Load(Path.Combine(repoRoot, "AST.slnx")).Root!;

        return root.Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .Where(p => p is not null)
            .Select(p => Path.GetDirectoryName(p!)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(repoRoot, d))
            .ToList();
    }

    private static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
