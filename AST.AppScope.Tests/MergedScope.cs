using System.IO;
using System.Windows;
using System.Xml.Linq;
using FluentAssertions;
using Wpf.Ui;

namespace AST.AppScope.Tests;

// Who declares a dictionary. Decided by PROVENANCE, never by "not WPF-UI, therefore AST": the spec's
// "must merely not block" list names a Dark ThemesDictionary and a second control library, and a
// name-based partition classifies both as AST and then manufactures false collisions and false
// offenders.
//
// Provenance is a CHAIN, because Source alone is not the only evidence and is not always present
//: a dictionary can be built in code, and a nested Source can be relative rather than a
// pack URI. Each link is a fact about where the dictionary came from; only when all of them are silent
// does the classification refuse.
internal enum ScopeOwner
{
    Ast,
    WpfUi,
    Other,           // another vendor's: legitimate, and NOT a party to the collision contract.
    Unattributable,  // no evidence at all -- reported by route, never guessed into a side.
}

// One entry of the app-global scope.
//
// Route is the top-level entry it arrived through - the ROUTE into the scope, not the declaring file
// and not a promise about which declaration wins a lookup. Failure messages must use that word.
//
// A value is read through the DECLARING dictionary rather than through the Application, because
// ResourceDictionary's indexer and Contains both search MergedDictionaries: asked through a parent
// they answer for a child, which is how the first provenance walk on this feature produced a table
// naming the outermost dictionary for every single hit. That reason still governs Project below.
// (A `Value` convenience property used to live here and had no callers; deleted 2026-08-21 --
// Raised independently by two reviewers.)
//
// ⚠️ THIS TYPE HOLDS LIVE WPF OBJECTS and is reachable from the xUnit thread, and a cross-thread use
// of it WOULD NOT THROW: ResourceDictionary is not a DispatcherObject and Style's
// getters do not VerifyAccess, so the call would silently realize deferred values on the wrong thread
// and return a plausible answer. That is why every guard reads AppScope.Scope -- a snapshot of plain
// data captured on the owning STA thread -- and never Entries directly.
internal readonly record struct ScopeEntry(string Route, ScopeOwner Owner, ResourceDictionary Dictionary, object Key);

// Plain data, materialised on the owning STA thread. Nothing here holds a WPF object, and that is the
// point: every WPF object is thread-affine, so what a guard cannot reach, a guard cannot break. This
// is what replaced marshalling a guard body onto a live Application (2026-08-20).
//
// ⚠️ "Immutable" here names the INTENT and the thread-safety property this type was built for -- it is
// not a mechanism. Every collection is IReadOnlyList<T> backed by a List<T>, so a
// consumer inside this assembly can cast one back and mutate the single Lazy-cached instance every
// guard shares. Nothing does, and nothing should. Narrowed rather than defended with AsReadOnly():
// the exposure is same-assembly only, the caller does not exist, and this project's rule is that a
// guarantee is worded to what holds.
internal sealed record ScopeSnapshot(
    string AppTypeFullName,
    bool AppIsTheProcessApplication,
    IReadOnlyList<string> MergedLabels,
    IReadOnlyList<string> PrimaryStringKeysBeforeWarmup,
    IReadOnlyList<string> PrimaryNonStringKeysBeforeWarmup,
    IReadOnlyList<string> PrimaryStringKeysAfterWarmup,
    IReadOnlyList<string> PrimaryNonStringKeysAfterWarmup,
    IReadOnlyList<ScopeEntrySnapshot> Entries,
    IReadOnlyList<string> UnreadableValues);

// What kind of thing the key WAS, before it was flattened to a string. `Other` is deliberately not a
// residual for `Type`: R2's F-16 caught `KeyIsString == false` conflating a Type key with a
// ComponentResourceKey, and Task 3's whole subject is Type keys.
internal enum ScopeKeyKind
{
    String,
    Type,
    Other,
}

// One entry of the recursive walk, with every WPF object replaced by what a guard actually asserts on.
// Key is described rather than held for the same reason a Type key cannot travel: it is an object
// reference into the owning thread's world.
//
// ⚠️ DictionaryId is IDENTITY, and it is not decoration. Task 2's
// RequireUnattributableDictionariesArePermitted counts **distinct dictionary instances** - its own
// comment says so - and a label cannot count them: two `Source`-less
// dictionaries merged under one route are both labelled "ResourceDictionary" and would collapse into
// one. The id is assigned per distinct instance within a single capture, by reference, in walk order.
// It is meaningless ACROSS captures and no guard may persist it.
internal sealed record ScopeEntrySnapshot(
    string Route,
    ScopeOwner Owner,
    int DictionaryId,
    string DictionaryLabel,
    string Key,
    ScopeKeyKind Kind,
    string? KeyTypeFullName,
    string? StyleTargetTypeFullName,
    bool? StyleHasBasedOn);

// The ONE walk. Both guards read this; neither writes its own.
internal static class MergedScope
{
    // App.xaml's own ResourceDictionary. Its ten converter entries sit AFTER the merge list inside the
    // same dictionary, and anything declared there OUTRANKS every merged dictionary in a lookup. R1
    // and R2 started the walk at MergedDictionaries and never saw it: an implicit style
    // added here changed what a keyed style displaces with both guards green.
    internal const string PrimaryRoute = "App.xaml";

    // ── The primary dictionary has TWO authors, and this is the ONLY place in the scope where that
    // is true. ────────────────────────────────────────────────────────────────────────────────────
    //
    // Every MERGED dictionary carries its own provenance - a Source naming an assembly, or a type
    // declared by one - so OwnerOf can read it. The primary dictionary carries none: it is the
    // Application's own dictionary, it has no Source, and WPF-UI writes into it through the indexer
    // (measured 2026-08-20). Two authors, one dictionary, and NOTHING at runtime distinguishes their
    // entries. Not the value's type: a value's type is no more provenance than a key's name (the same
    // locked rule from a third direction), and the discriminator that looks available today - all ten
    // AST entries here are converters - dies the moment App.xaml declares a brush, which is exactly
    // the F-A1 scenario. Not insertion order either: ResourceDictionary's key enumeration is
    // hash-backed, so it is not even available. Two agents with different toolsets went looking and
    // neither found a mechanism; the second tried to BREAK the premise rather than confirm it, and
    // reported that ResourceDictionaryDiagnostics is dictionary-level only and does not cover
    // Application.Resources at all.
    //
    // So this is NOT a provenance decision, and it must never be read as one. It is a PARTITION:
    // one side DERIVED from source, one side named in full, checked for coverage AND overlap in both
    // directions. Nothing is ever "the rest" - a residual definition silently absorbs whatever the
    // framework adds next, which is the exact defect that stopped execution here.
    //
    // WHY NOT infer: "a key WPF-UI also defines is not AST's" was considered and REJECTED. It
    // reclassifies the one thing the collision guard exists to catch - an AST key accidentally
    // colliding with a WPF-UI one - and hands a WPF-UI upgrade the power to flip an unchanged AST
    // key's owner. A name never decides an owner in this file; that is the same rule as F-17.

    // AST's own, DERIVED from App.xaml's source rather than listed here.
    //
    // ⚠️ It was a hand-written list until 2026-08-20, and that was a silent hole (wpf-architect
    // F-A1). ResourceDictionary keys are UNIQUE: an AST entry declared in App.xaml under a name
    // WPF-UI also injects occupies the SAME slot, so the dictionary still holds 31 keys and the union
    // assertion still matches - GREEN, while two authors fight over one slot in an order-dependent
    // way. Nothing reddened, because a hand-written list only moves when a HUMAN moves it, and the
    // human adding that converter has no reason to touch either list.
    //
    // Derived, the same edit puts the key in BOTH sets, and the disjointness check below fails by
    // name. The catcher was already in the design; hand-writing this side is what disabled it.
    //
    // This reads ONE file with XDocument and consumes nothing from AST.Meta.Tests. That distinction
    // is the whole reason it is allowed: review rejected reusing XamlResourceGraph across
    // projects (internal, calls MetaTest.IsGenerated, and the cross-project link is an explicitly
    // PARKED decision this plan refuses to settle sideways). It never rejected deriving the set. An
    // earlier revision collapsed the requirement into that one implementation and dropped both.
    //
    // Cost, stated rather than argued away: a second, much smaller XAML reader now exists in the
    // repo. It answers a different question at a different altitude (the direct x:Key children of one
    // known file, vs. a whole-repository key graph), and it is retired by the extraction task if that
    // is ever scheduled.
    internal static IReadOnlyList<string> AstPrimaryKeys(string repoRoot)
    {
        var appXaml = Path.Combine(repoRoot, "AST", "App.xaml");

        // NAMED failures rather than two bare Single() calls. AstPrimaryKeys runs inside
        // the capture and Lazy caches the failure, so the implicit-dictionary form -- which WPF accepts
        // and which is common -- used to fail ALL FOUR tests with "Sequence contains no matching
        // element", naming neither the file, the expected shape, nor which Single() gave up. RepoRoot()
        // in this same file already does this properly for a strictly less likely failure.
        // ⚠️ Counted, not SingleOrDefault: that overload returns null only for ZERO matches and THROWS
        // the same unnamed "Sequence contains more than one matching element" for MORE than one -- which
        // is precisely the multi-dictionary form these messages claim to cover. Measured 2026-08-21
        // while proving this branch: the first draft of this fix used SingleOrDefault and left that case
        // exactly as unnamed as the bare Single() it replaced.
        var resources = XDocument.Load(appXaml).Root!
            .Elements().Where(e => e.Name.LocalName == "Application.Resources").ToList();
        if (resources.Count != 1)
        {
            throw new InvalidOperationException(
                $"{appXaml} has {resources.Count} <Application.Resources> elements; this reader needs "
                + "exactly one. It derives the AST side of the primary-dictionary partition from that "
                + "element's explicit <ResourceDictionary> child, so the shape is load-bearing, not "
                + "incidental.");
        }

        var dictionaries = resources[0]
            .Elements().Where(e => e.Name.LocalName == "ResourceDictionary").ToList();
        if (dictionaries.Count != 1)
        {
            throw new InvalidOperationException(
                $"{appXaml}'s <Application.Resources> contains {dictionaries.Count} explicit "
                + "<ResourceDictionary> children; this reader needs exactly one. ⚠️ MEASURED 2026-08-21 "
                + "while proving this branch, and the first wording of this message was wrong about it: "
                + "WPF REJECTS two direct <ResourceDictionary> children at load, before this reader ever "
                + "runs, so the real host cannot reach this branch that way -- only a fabricated App.xaml "
                + "passed straight to AstPrimaryKeys can. What the host CAN reach is the implicit form "
                + "(resources written directly under Application.Resources), which this reader also does "
                + "not support, and where silently reading the wrong element would make the derived AST "
                + "side WRONG rather than absent.");
        }

        var dictionary = dictionaries[0];

        // DIRECT children only. A key nested deeper is inside MergedDictionaries or inside another
        // resource's own content, and neither lands in the primary dictionary.
        return dictionary.Elements()
            .Select(e => e.Attribute(Xaml + "Key")?.Value)
            .OfType<string>()
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // WPF-UI's, written into Application.Resources by the PRIVATE method
    // Wpf.Ui.Appearance.ApplicationAccentColorManager.UpdateColorResources, which the first get of
    // UiApplication.Resources triggers via ApplySystemAccent().
    //
    // ⚠️ CORRECTED 2026-08-20: this comment used to say "Prism never starts, so this is the library
    // acting on its own." Both halves were wrong. WPF's Application constructor queues the callback
    // that starts Prism, and Prism DID run here until the host stopped pumping. What is true is
    // narrower: AST's own brand Apply (AST/App.xaml.cs:183) lives in OnInitialized, which Prism reaches
    // only after InitializeModules, and that step throws first - so these 21 keys are not written by
    // AST's call. Since 2026-08-20 the host pulls the trigger itself, by name, in CaptureSnapshot.
    //
    // ⚠️ THIS LIST IS A RECURRING COST, ON PURPOSE. WPF-UI publishes no enumerable form of it - the
    // names are string literals inside a private method, the public ThemeResource enum omits 9 of
    // them, and no public API accepts a write target, so it cannot be derived by machine (verified
    // twice: read out of the 4.3.0 source, and by inspecting the compiled 4.3.0 assembly). 4.2.1 and
    // 4.3.0 agree; 4.0.3 -> 4.1.0 ADDED three of these names. A WPF-UI upgrade must therefore redden
    // this list rather than slide past it, which is why the assertion runs in both directions.
    //
    // The 21 names below were NOT copied from the vendor's source. They are what the running app
    // reports, and they match the vendor's source as SETS in both directions - two independent
    // methods, compared as sets and never as counts.
    internal static readonly string[] WpfUiInjectedPrimaryKeys =
    [
        "AccentFillColorDefault", "AccentFillColorDefaultBrush", "AccentFillColorSecondary",
        "AccentFillColorSecondaryBrush", "AccentFillColorSelectedTextBackgroundBrush",
        "AccentFillColorTertiary", "AccentFillColorTertiaryBrush", "AccentTextFillColorDisabled",
        "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush", "SystemAccentBrush", "SystemAccentColor",
        "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorTertiary",
        "SystemFillColorAttentionBrush", "TextOnAccentFillColorDisabled",
        "TextOnAccentFillColorPrimary", "TextOnAccentFillColorSecondary",
        "TextOnAccentFillColorSelectedText",
    ];

    internal static string Label(ResourceDictionary dictionary) =>
        dictionary.Source is null
            ? dictionary.GetType().Name
            : Path.GetFileNameWithoutExtension(dictionary.Source.OriginalString);

    // The provenance chain, strongest evidence first. `parent` is the owner of the dictionary this one
    // is merged into, and is Ast for a top-level merge, because App.xaml is AST's own file.
    //
    // ⚠️ The ordering is not cosmetic. R4 gated link 1 on Uri.IsAbsoluteUri, which is FALSE for
    // "/Vendor;component/File.xaml" - a perfectly ordinary relative pack URI that names an assembly
    // outright. That dictionary fell through to containment and was classified as its parent
    //. Assembly evidence in the Source now decides FIRST, absolute or relative, and
    // containment is reserved for a source with no assembly in it at all.
    internal static ScopeOwner OwnerOf(ResourceDictionary dictionary, ScopeOwner parent)
    {
        var source = dictionary.Source?.OriginalString;

        // 1. Any Source that names an assembly - "pack://application:,,,/AST.UI;component/..." or the
        //    relative "/AST.UI;component/...". The assembly is the segment before ";component".
        if (source is not null && AssemblyInPackUri(source) is { } named)
        {
            return AssemblyOwner(named);
        }

        // 2. A Source with NO assembly in it is a genuinely local path, resolved inside the package
        //    that declares it - so containment is the evidence, and the parent's owner is the answer.
        if (source is not null)
        {
            return parent;
        }

        // 3. No Source at all: a TYPED dictionary still declares its vendor by its own type. This is
        //    what keeps a programmatically-supplied third-party dictionary out of the AST set instead
        //    of blocking the whole guard on it. It sits BELOW the Source links deliberately: a
        //    subclass wrapper carrying an explicit Source must be classified by that Source, not by the
        //    assembly that declared the wrapper type.
        var declaring = dictionary.GetType();
        if (declaring != typeof(ResourceDictionary))
        {
            return AssemblyOwner(declaring.Assembly.GetName().Name);
        }

        // 4. A plain, Source-less dictionary. Nothing here says who wrote it, and guessing is the F-17
        //    defect, so it is reported by identity rather than assigned a side.
        return ScopeOwner.Unattributable;
    }

    // "…/Name;component/…" or "…/Name;v1.2.3.4;component/…" -> "Name". Null when the URI carries no
    // ";component" segment at all, which is exactly what separates links 1 and 2 above.
    private static string? AssemblyInPackUri(string source)
    {
        var marker = source.IndexOf(";component", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var start = source.LastIndexOf('/', marker) + 1;
        var segment = source[start..marker];
        var version = segment.IndexOf(';');
        return version < 0 ? segment : segment[..version];
    }

    private static ScopeOwner AssemblyOwner(string? assembly) => assembly switch
    {
        "Wpf.Ui" => ScopeOwner.WpfUi,
        "AST" or "AST.UI" => ScopeOwner.Ast,
        _ => ScopeOwner.Other,
    };

    internal static List<ScopeEntry> Entries(Application app)
    {
        var entries = new List<ScopeEntry>();

        // The primary dictionary first, and WITHOUT descending into its merges: those are walked below
        // under their own routes, and visiting them twice would double every key. It gets its own walk
        // because it is the one dictionary whose owner cannot be read off the dictionary - see
        // WalkPrimary.
        WalkPrimary(app.Resources, entries);

        foreach (var top in app.Resources.MergedDictionaries)
        {
            Walk(top, Label(top), OwnerOf(top, ScopeOwner.Ast), entries);
        }

        return entries;
    }

    // The primary dictionary's own entries, classified by the named partition rather than by
    // provenance - because it has none (see the two lists above).
    //
    // A key in NEITHER list is Unattributable, not a guess. It is then excluded from the AST side of
    // both guards exactly like a Source-less dictionary's keys, so an unclassified key can never
    // manufacture a collision or an offender - and RequirePrimaryDictionaryIsPartitioned is what makes
    // sure it is reported instead of quietly ignored.
    //
    // Non-string keys are deliberately left Unattributable rather than being partitioned: an implicit
    // style declared here is a decision that must be noticed, and AppScopeTests already asserts there
    // are none. The two assertions together account for every key in this dictionary.
    private static void WalkPrimary(ResourceDictionary primary, List<ScopeEntry> into)
    {
        var ast = AstPrimaryKeys(RepoRoot());

        foreach (var key in primary.Keys.Cast<object>().ToList())
        {
            var owner = key switch
            {
                // AST FIRST, deliberately. If a key is somehow in both sets the partition is already
                // broken, and RequirePrimaryDictionaryIsPartitioned is what reports it; reaching this
                // switch in that state must not quietly pick a side, so the order here is the one that
                // keeps the AST-declared key VISIBLE to the collision guard rather than hiding it on
                // the WPF-UI side (wpf-architect F-A1).
                string s when ast.Contains(s, StringComparer.Ordinal) => ScopeOwner.Ast,
                string s when WpfUiInjectedPrimaryKeys.Contains(s, StringComparer.Ordinal) => ScopeOwner.WpfUi,
                _ => ScopeOwner.Unattributable,
            };

            into.Add(new ScopeEntry(PrimaryRoute, owner, primary, key));
        }
    }

    // Repo root = the directory holding AST.slnx, walked up from the test binary. Same shape as
    // AST.Meta.Tests/MetaTest.cs:9 and deliberately a separate copy: linking that file is the parked
    // cross-project decision, and eight lines is not worth settling it. It is a
    // FIXTURE-location helper, not a model.
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AST.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Repo root (the directory containing AST.slnx) not found above {AppContext.BaseDirectory}. "
                + "This guard reads App.xaml's SOURCE, so it cannot run from a binary published away "
                + "from the repository - a clear failure here beats a silently empty AST key set.");
    }

    // Both guards call this FIRST, BEFORE RequireUnattributableDictionariesArePermitted.
    //
    // The order matters and was wrong until 2026-08-20 (wpf-architect F-A5): a primary key in neither
    // set becomes Unattributable under route "App.xaml", so running the dictionary guard first
    // reported a missing-provenance DICTIONARY that does not exist, and its message invited a baseline
    // entry that would then have absorbed any number of unnamed primary keys.
    //
    // What this does and does not establish. It establishes COVERAGE - every string key in the
    // dictionary is accounted for by exactly one side - and coverage is what makes the classification
    // in WalkPrimary safe to act on. It does NOT establish ATTRIBUTION: a world in which all 21 accent
    // keys were AST's own would satisfy it identically. Attribution rests on the 2026-08-20
    // measurement and the WPF-UI 4.3.0 source read recorded above - historical evidence in a comment,
    // not something re-proved per run. An earlier draft called this "licensing" and claimed the second
    // thing; that sentence was wider than its mechanism and contradicted this plan's own "does not
    // prove" list.
    internal static void RequirePrimaryDictionaryIsPartitioned(ScopeSnapshot scope)
    {
        var ast = AstPrimaryKeys(RepoRoot());

        // ⚠️ THE F-A1 CATCHER, and the reason the AST side is derived rather than listed. An AST entry
        // declared in App.xaml under a name WPF-UI also injects shares ONE slot with it, so the
        // dictionary's key COUNT and key SET are both unchanged and the union assertion below stays
        // green. This is the only assertion that sees it.
        ast.Intersect(WpfUiInjectedPrimaryKeys, StringComparer.Ordinal).Should().BeEmpty(
            "a key App.xaml declares directly AND WPF-UI injects is two authors writing one slot, and "
            + "which value survives depends on load order. It is invisible to every other assertion "
            + "here, because ResourceDictionary keys are unique - the dictionary looks unchanged. If "
            + "this fails, do not rename the AST key to dodge it: decide whether App.xaml should be "
            + "declaring that key at all");

        // AFTER the warm-up, deliberately -- and this asserts the injected-key CONTRACT, not production
        // state (125 F-15). The host triggers ApplySystemAccent(); the shipping app instead calls
        // ApplicationAccentColorManager.Apply(#AE1C3E) in OnInitialized (AST/App.xaml.cs:183), which this
        // host never reaches. Both routes write the SAME 21 key names, which is what is asserted here.
        // ⚠️ THE VALUES BEHIND THOSE KEYS ARE NOT PRODUCTION'S, and no guard may assert on them until the
        // two routes are measured for value equivalence. This dictionary carries the system accent.
        var actual = scope.PrimaryStringKeysAfterWarmup;

        var named = ast.Concat(WpfUiInjectedPrimaryKeys)
            .OrderBy(k => k, StringComparer.Ordinal);

        actual.Should().Equal(named,
            "the primary dictionary has two authors and no per-entry provenance, so both sides are "
            + "accounted for and their union must equal every string key in it - nothing is 'the rest'. "
            + "A key reported here belongs to one of two cases: App.xaml's declarations no longer match "
            + "what loads, which is a bug in this reader or in the file; or a WPF-UI release changed "
            + "what ApplicationAccentColorManager.UpdateColorResources writes, which is a decision about "
            + "the upgrade and not a list to top up");
    }

    // Recursive on purpose: a dictionary merged under Controls.xaml is as much a part of the scope as
    // Controls.xaml itself, and a guard that stops at the top level stays green while the defect ships.
    // A child keeps its parent's ROUTE (that is the granularity App.xaml controls) but is classified on
    // its OWN provenance - a WPF-UI dictionary merged under an AST one contributes WPF-UI keys, which
    // Task 3 Step 5b is what actually proves.
    private static void Walk(
        ResourceDictionary dictionary,
        string route,
        ScopeOwner owner,
        List<ScopeEntry> into)
    {
        foreach (var key in dictionary.Keys.Cast<object>().ToList())
        {
            into.Add(new ScopeEntry(route, owner, dictionary, key));
        }

        // A `bool recurse = true` parameter and its early return used to sit here. No call site ever
        // passed false, so the early return was unreachable; deleted 2026-08-21.
        foreach (var child in dictionary.MergedDictionaries)
        {
            Walk(child, route, OwnerOf(child, owner), into);
        }
    }

    // Called SECOND, AFTER RequirePrimaryDictionaryIsPartitioned -- this comment said FIRST until
    // 2026-08-21, which is the reverse of the wpf-architect F-A5 contract stated correctly in
    // three other places. The partition runs first so that a primary key belonging to neither side
    // cannot surface here as a phantom missing-provenance DICTIONARY.
    //
    // It is a SET comparison against a baseline, not an
    // unconditional stop. The difference matters: a stop would make one legitimate arrangement -- a
    // dictionary built in code and merged in App.xaml.cs -- impossible to ship without redesigning the
    // guard, which is the "must merely not block" line the spec draws. A baseline costs one entry and
    // a written reason, exactly like the 15 permitted collisions.
    //
    // Keyed by (route, COUNT), not by route alone: once a route were permitted, a SECOND
    // unattributable dictionary arriving under it would change no member of a route-only set, and its
    // keys are excluded from the AST side of both guards -- so it would ship invisible to all three
    // assertions. The count is what makes a second one a diff.
    //
    // The baseline is EMPTY today, measured. Entries from an unattributable dictionary are excluded
    // from the AST side of both guards, so an unclassified dictionary can never manufacture a
    // collision or an offender while it waits to be classified.
    internal static readonly (string Route, int Count)[] PermittedUnattributableDictionaries = [];

    // Reads the SNAPSHOT, not live entries: by the time a guard runs, the thread that owned every
    // ResourceDictionary in this walk has exited (125 F-12). DictionaryId is what makes that possible -
    // it is per-instance identity assigned during the capture, which is the only thing that can still
    // count instances once the instances themselves are gone (126 F-16).
    internal static void RequireUnattributableDictionariesArePermitted(IReadOnlyList<ScopeEntrySnapshot> entries)
    {
        var unattributable = entries
            // The primary dictionary is NOT in this guard's universe (wpf-architect F-A5). Its
            // Unattributable entries are unnamed KEYS, not a dictionary with no provenance, and this
            // baseline counts distinct DICTIONARY instances - so one entry here would absorb any
            // number of unnamed primary keys and F-27's count protection would not apply.
            // RequirePrimaryDictionaryIsPartitioned owns that question and runs first.
            .Where(e => e.Owner == ScopeOwner.Unattributable && e.Route != PrimaryRoute)
            .GroupBy(e => e.Route, StringComparer.Ordinal)
            .Select(g => (Route: g.Key, Count: g.Select(e => e.DictionaryId).Distinct().Count()))
            .OrderBy(x => x.Route, StringComparer.Ordinal);

        unattributable.Should().Equal(
            PermittedUnattributableDictionaries.OrderBy(x => x.Route, StringComparer.Ordinal),
            "a Source-less plain dictionary carries no evidence of who wrote it; the guards refuse to "
            + "guess it into a side, so each one must be named here with a reason -- and the count is "
            + "part of the name, because a second one under a permitted route is a second decision");
    }

    // ⚠️ THE ORDER OF THE STATEMENTS BELOW IS THE CONTRACT, not an implementation detail. MEASURED
    // 2026-08-20: Application.Resources holds App.xaml's 10 declarations until something forces WPF-UI's
    // accent injection, and 31 afterwards. Reading a value out of Wpf.Ui's CalendarDatePicker.xaml does it
    // by accident; Prism's startup did it by accident too, which is the only reason the old host ever saw
    // 31. Both states are captured here, and the trigger is pulled BY NAME in between, so neither number
    // depends on what else ran first in this process.
    internal static ScopeSnapshot CaptureSnapshot(Application app)
    {
        var beforeStrings = PrimaryStringKeys(app);
        var beforeOthers = PrimaryNonStringKeys(app);

        // THE NAMED WARM-UP. UiApplication.Resources is a getter with a side effect: it calls
        // ApplicationAccentColorManager.ApplySystemAccent(), whose private UpdateColorResources writes the
        // 21 accent keys into Application.Resources - and it returns that same dictionary instance
        // (measured: ReferenceEquals true). Merely touching UiApplication.Current does NOT trigger it.
        // MEASURED 2026-08-21 (`127d` W-08, which reported this clause as unreachable and said so as
        // REASONING, not measurement): in this host UiApplication.Current is NOT null -- WPF-UI's static
        // Current lazily constructs its wrapper, so the branch below did not fire. Kept rather than
        // deleted: "did not fire on this build of WPF-UI" is not "cannot fire", and a null here would
        // still be a real failure that must not degrade into a NullReferenceException.
        var ui = UiApplication.Current
            ?? throw new InvalidOperationException(
                "Wpf.Ui.UiApplication.Current is null, so the accent warm-up cannot run and the 31-key "
                + "state cannot be produced. Do not skip the warm-up to make this pass: the guards below "
                + "would then be measuring a scope this app never has.");
        _ = ui.Resources;


        var afterStrings = PrimaryStringKeys(app);

        // POST-CONDITION on the warm-up.
        // The warm-up is a named side effect of a property GETTER, so its disappearance is SILENT, and the
        // partition guard's own failure text then offers two causes that would BOTH be wrong. A loud
        // misdiagnosis in a heavily commented file will be believed, so this says which failure it is at
        // the point it happens.
        //
        // ⚠️ THREE states fail here and they are NOT the same failure. The first version compared COUNTS
        // and asserted one cause flatly. MEASURED 2026-08-21, both arms: killing the trigger ALONE was
        // caught correctly, but killing it AND adding ONE unrelated string key sailed past -- 10 vs 11 --
        // and the failure then surfaced from the T1/T2 seam instead, reporting 21 accent keys "added
        // during the walk". True, and the wrong thing to go looking for: the reader hunts a writer in the
        // walk rather than a dead trigger. One key was enough to defeat the count.
        //
        // The other direction is reachable too -- Prism's
        // startup used to perform this injection by accident, which is failure (b) above -- the FIRST branch
        // below is what now names it instead of blaming the trigger. Asserted as SETS
        // against the NAMED 21, because a count cannot tell an accent key from any other string.
        var alreadyPresent = WpfUiInjectedPrimaryKeys
            .Intersect(beforeStrings, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (alreadyPresent.Count > 0)
        {
            throw new InvalidOperationException(
                "The accent keys were ALREADY in Application.Resources before the named warm-up ran: "
                + string.Join(", ", alreadyPresent)
                + ". This is NOT the trigger failing and NOT the key list changing -- something injected them "
                + "first, so PrimaryStringKeysBeforeWarmup is not App.xaml's declaration contract and the two "
                + "captured states are no longer two different contracts. Find the earlier writer first.");
        }

        var missingAccentKeys = WpfUiInjectedPrimaryKeys
            .Except(afterStrings, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (missingAccentKeys.Count > 0)
        {
            throw new InvalidOperationException(
                "Reading UiApplication.Current.Resources did not inject " + missingAccentKeys.Count
                + " of the 21 named accent keys into Application.Resources; missing: "
                + string.Join(", ", missingAccentKeys)
                + ". This is the TRIGGER failing, not the injected key list changing: WPF-UI moved "
                + "ApplySystemAccent() off the Resources getter, or the accent manager no longer writes to the "
                + "Application scope. Asserted against the NAMED list, so an unrelated key arriving in the same "
                + "moment cannot mask it. Re-measure before touching the 21-key list.");
        }
        var afterOthers = PrimaryNonStringKeys(app);

        // The graph walk comes LAST, deliberately: reading a value out of Wpf.Ui's CalendarDatePicker.xaml
        // is a second, accidental trigger for the same injection (measured node-by-node), so walking before
        // the two primary-dictionary captures would make WHICH of them saw 31 depend on a vendor's file
        // layout. Walking after means both captures are decided by the named warm-up alone.
        var unreadable = new List<string>();

        // Reference identity, so two dictionaries that describe themselves identically are still two.
        // IEqualityComparer<T> is contravariant, so the object comparer binds here.
        var ids = new Dictionary<ResourceDictionary, int>(ReferenceEqualityComparer.Instance);
        var entries = Entries(app).Select(entry => Project(entry, ids, unreadable)).ToList();

        // THE T1/T2 SEAM. The partition
        // is asserted over the key set read BEFORE the walk, but classification happens DURING it -- and
        // this file records, a few lines up, that realizing a value can itself write to
        // Application.Resources (the accidental accent trigger, measured node-by-node 2026-08-20).
        //
        // ⚠️ That window is TWO windows, and the first version of this comment described only the one
        // with no known writer. Entries(app) materialises a List, so every key is enumerated before any
        // value is realized:
        //   - Window A, between the captures above and WalkPrimary's enumeration: a key arriving here DOES
        //     become a ScopeEntry, classified Unattributable under the primary route, which every guard
        //     then filters out -- excluded, silently.
        //   - Window B, between that enumeration and this line, which is where value realization and
        //     therefore the only known writer actually live: a key arriving here never becomes a
        //     ScopeEntry at all. It is invisible by ABSENCE, not by exclusion.
        // Different mechanisms, same consequence -- a snapshot whose partition describes a dictionary that
        // has since moved -- so one check covers both, over both KEY UNIVERSES, because both are asserted on.
        //
        // ⚠️ BOTH universes, and the string-only version was MEASURED GREEN 2026-08-21 under an injected
        // Type-keyed Style: afterOthers is captured before the walk, so nothing saw it at all.
        var afterWalkStrings = PrimaryStringKeys(app);
        var afterWalkOthers = PrimaryNonStringKeys(app);
        if (!afterWalkStrings.SequenceEqual(afterStrings, StringComparer.Ordinal)
            || !afterWalkOthers.SequenceEqual(afterOthers, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The primary dictionary changed between the partition capture and the graph walk. String keys added: "
                + string.Join(", ", afterWalkStrings.Except(afterStrings, StringComparer.Ordinal))
                + "; string keys removed: "
                + string.Join(", ", afterStrings.Except(afterWalkStrings, StringComparer.Ordinal))
                + "; non-string keys added: "
                + string.Join(", ", afterWalkOthers.Except(afterOthers, StringComparer.Ordinal))
                + "; non-string keys removed: "
                + string.Join(", ", afterOthers.Except(afterWalkOthers, StringComparer.Ordinal))
                + ". BOTH universes are read because both are asserted on: the string keys by the partition "
                + "guard, the non-string keys by AppScopeTests' BeEmpty. Covering only one leaves an implicit "
                + "style able to arrive under the walk and still be asserted as absent.");
        }

        return new ScopeSnapshot(
            app.GetType().FullName!,
            ReferenceEquals(app, Application.Current),
            app.Resources.MergedDictionaries.Select(Label).ToList(),
            beforeStrings,
            beforeOthers,
            afterStrings,
            afterOthers,
            entries,
            // A SORTED SEQUENCE, and the sort is the point: this list is asserted
            // with Equal -- a sequence comparison -- but is built in walk order, so a SECOND unreadable
            // resource would otherwise land wherever the merge happened to reach it. Ordinal, like
            // every other collection in this snapshot.
            unreadable.OrderBy(value => value, StringComparer.Ordinal).ToList());
    }

    // A value read can THROW: measured 2026-08-20, Light/BadgeBackground raises XamlParseException. The walk
    // records that and carries on -- aborting would lose the other 1224 entries (MEASURED 2026-08-20) over one unrelated resource,
    // and swallowing it silently would hide a real defect. UnreadableValues is asserted on like any other
    // field, so a NEW unreadable resource reddens rather than passing quietly.
    private static ScopeEntrySnapshot Project(
        ScopeEntry entry,
        Dictionary<ResourceDictionary, int> ids,
        List<string> unreadable)
    {
        var label = Label(entry.Dictionary);
        var key = entry.Key as string ?? DescribeKey(entry.Key);

        if (!ids.TryGetValue(entry.Dictionary, out var id))
        {
            id = ids.Count;
            ids[entry.Dictionary] = id;
        }

        Style? style = null;
        string? targetType = null;
        try
        {
            style = entry.Dictionary[entry.Key] as Style;

            // Read INSIDE the try. It used to sit in the constructor call
            // below, outside every guard, where any failure would take down the whole capture -- and
            // because Lazy uses ExecutionAndPublication that means ALL FOUR tests report one cause
            // instead of their own question.
            //
            // ⚠️ The reason is NOT the NullReferenceException the two reviewers reasoned about.
            // MEASURED 2026-08-21 in this host, with a keyed <Style /> carrying no TargetType:
            // Style.TargetType comes back as System.Windows.IFrameworkInputElement, never null. That
            // failure mode is UNREACHABLE, and the backlog entry describing it was deleted.
            // What the move buys is narrower and still worth two lines: whatever this projection may
            // fail on in future is recorded in UnreadableValues like every other unreadable value.
            targetType = style?.TargetType.FullName;
        }
        catch (Exception ex)
        {
            // ⚠️ THE VENDOR'S PROSE IS DELIBERATELY NOT RECORDED (design U3, spec
            // `### Candidates U1-U3`). What is asserted is AST's own statement -- WHOSE resource, WHERE
            // it came from, WHICH key, and WHAT SHAPE the failure has. The exception's free text is
            // Microsoft's, may be reworded by any .NET servicing release, and is localizable; asserting
            // it gave a vendor a veto over an AST contract and made a reword indistinguishable from a
            // real regression.
            //
            // The mechanism has a name that four standards share -- PIN THE STABLE IDENTIFIER, DO NOT
            // COMPARE THE RENDERING (spec `### B6''`): Clang's diagnostics carry a unique ID and typed
            // arguments while the English string is a rendering template; RFC 9457 says consumers MUST
            // key on the type URI and SHOULD NOT parse `detail`; Roslyn diagnostic IDs are deliberately
            // not English text; SARIF prefers a symbolic rule id over a descriptive one.
            //
            // ⚠️ NARROWED 2026-08-21 after review opened the three sites this comment used to
            // cite. The in-repo precedent is ONE clean instance, not three: ScopeEntrySnapshot's
            // KeyTypeFullName and StyleTargetTypeFullName, both Type.FullName. DescribeKey is HALF an
            // instance -- it pins FullName for a Type key and falls back to ToString(), the rendering,
            // for every other key. Label is NOT an instance and points the other way: it discards the
            // pack URI (the stable identifier that OwnerOf depends on) for a file-name stem, and its
            // other branch is GetType().Name -- the very simple name the next paragraph calls unsafe.
            // The principle stated above ScopeEntrySnapshot is a DIFFERENT one: a live WPF object must
            // not travel out of its owning thread. The wider sentence was this feature's own signature
            // defect committed once more, in the comment written to name that defect.
            //
            // ⚠️ FullName, NOT Name. A simple name collides: a type named
            // XamlParseException in another namespace would produce a byte-identical record and pass.
            //
            // ⚠️ OWNER comes from entry.Owner and NEVER from the route string. `route/label/key` does not
            // carry provenance -- a child dictionary keeps its parent's ROUTE but is classified on its
            // OWN (see Walk) -- so a WPF-UI dictionary merged under an AST route would otherwise be
            // reported as AST's. That distinction is the whole of "regression, or vendor upgrade?"
            //.
            //
            // ⚠️ ACCEPTED AND OPEN, recorded rather than mitigated: the same key failing for a DIFFERENT
            // reason within the same exception shape stays GREEN. That is U3's stated price; the
            // requester chose it over U1 with this named. The inner MESSAGE that diagnosed the one known
            // entry is not lost -- it lives in the backlog, which is where a conclusion
            // belongs rather than in a per-run observation.
            //
            // ⚠️ COUPLING, and it is silent: when a read throws,
            // `style` stays null, so this entry becomes indistinguishable from "the value is not a
            // Style". It therefore silently leaves the implicitly-styled universe -- WEAKENING what
            // KeyedStylesDisplacingAnImplicitStyle has to displace -- and silently leaves the offender
            // set, EXCUSING an AST style that failed to realize. The only thing that catches either is
            // the exact one-entry UnreadableValues baseline, which lives in a DIFFERENT test class
            // from the guard whose universe shrank.
            var innerType = ex.InnerException is null ? "none" : ex.InnerException.GetType().FullName;
            unreadable.Add(
                $"{entry.Owner}/{entry.Route}/{label}/{key}: {ex.GetType().FullName} [inner={innerType}]");
        }

        var kind = entry.Key switch
        {
            string => ScopeKeyKind.String,
            Type => ScopeKeyKind.Type,
            _ => ScopeKeyKind.Other,
        };

        return new ScopeEntrySnapshot(
            entry.Route,
            entry.Owner,
            id,
            label,
            key,
            kind,
            (entry.Key as Type)?.FullName,
            targetType,
            style is null ? null : style.BasedOn is not null);
    }

    private static string DescribeKey(object key) =>
        key is Type type ? type.FullName ?? type.Name : key.ToString() ?? "<null>";

    private static IReadOnlyList<string> PrimaryStringKeys(Application app) =>
        app.Resources.Keys.Cast<object>().OfType<string>().OrderBy(k => k, StringComparer.Ordinal).ToList();

    // Described rather than held: a Type key is a WPF object reference, and this record leaves the thread.
    private static IReadOnlyList<string> PrimaryNonStringKeys(Application app) =>
        app.Resources.Keys.Cast<object>().Where(k => k is not string)
            .Select(DescribeKey)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

}
