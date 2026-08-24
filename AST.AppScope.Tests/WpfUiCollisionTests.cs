using FluentAssertions;

namespace AST.AppScope.Tests;

// GUARD - WpfUiOverrides.xaml exists to collide with WPF-UI on purpose: overriding these keys is how
// stock WPF-UI templates adopt the AST palette. That makes the collision SET the contract, and it is
// checked in both directions for two different defects:
//   - a member DISAPPEARS: WPF-UI renamed or dropped the key, so an AST retint now paints nothing and
//     the screen silently reverts to Fluent's colour. Nothing else in the build notices.
//   - a member APPEARS: an AST key accidentally shadows a WPF-UI one, changing stock controls the
//     author never looked at.
// A COUNT cannot do this. Losing one intended collision while gaining one accidental collision leaves
// the count identical and two behaviours regressed - the same count-vs-set failure that put a wrong
// membership behind a right number in this feature's own baseline.
//
// The set is of (route, key) PAIRS, not keys. WpfUiOverrides.xaml is merged LAST precisely so its
// retints win; the same key declared from Palette.xaml collides just as much but loses to any later
// dictionary that also defines it. A key-only set calls those two states identical.
// "Route" is the top-level App.xaml entry a key arrived through - not the file that declares it, and
// not a claim about which declaration wins the lookup.
//
// AST vs WPF-UI is decided by MergedScope.OwnerOf. A Dark ThemesDictionary or a second control
// library therefore cannot be mistaken for AST's and cannot manufacture a collision, which the spec
// requires this guard "must merely not block".
//
// Ownership is read DIFFERENTLY by the two guards, and that is deliberate. Here only two
// sides exist: a collision is a fact about AST overriding WPF-UI, so Other and Unattributable are not
// parties to it. Task 3 asks a different question - what displaces an implicit style - and there
// EVERY owner counts, because a control does not care which vendor's style it lost.
//
// ⚠️ Nothing here touches a WPF object. AppScope.Scope is an immutable snapshot -- narrowed on
// ScopeSnapshot: the INTENT, not a mechanism -- captured on a thread
// that has since exited, so this guard reads ScopeEntrySnapshot records where the key is already a
// string and its original kind is carried separately (125 F-12).
//
// What a GREEN run does NOT prove: that the retint LOOKS right, that WPF-UI still USES the key it
// defines, that the merge ORDER is right (the guard reads the merged result, where order is
// invisible), that a THIRD vendor's dictionary does not collide with either side (Other is outside
// this contract), or anything about the Dark theme - the merge here is Theme="Light", as App.xaml
// ships it.
public class WpfUiCollisionTests
{
    // Measured 2026-08-19 against WPF-UI 4.3 through the real App.xaml merge, and RE-MEASURED
    // 2026-08-20 after the primary dictionary was partitioned - the partition put AST's 10 converters
    // and WPF-UI's 21 accent keys on opposite sides of this same question for the first time, and it
    // changed nothing here. Every member is a top-level x:Key of WpfUiOverrides.xaml - measured - and is
    // also defined by a WPF-UI-owned dictionary; WHICH ONE is not recorded, and this guard does not
    // depend on it. (Narrowed 2026-08-21, Q-05: the comment used to name ui:ThemesDictionary
    // specifically. OwnerOf classifies both the Light and the Wpf.Ui dictionaries as WpfUi and the
    // snapshot never records which one defined a key, so that half was never measured.)
    // The route is part of the contract: a retint that moves out of
    // WpfUiOverrides is a different arrangement, not the same one spelled differently.
    private static readonly (string Route, string Key)[] PermittedCollisions =
    [
        ("WpfUiOverrides", "ApplicationBackgroundBrush"),
        ("WpfUiOverrides", "ApplicationBackgroundColor"),
        ("WpfUiOverrides", "NavigationViewItemBackgroundPointerOver"),
        ("WpfUiOverrides", "NavigationViewItemBackgroundPressed"),
        ("WpfUiOverrides", "NavigationViewItemBackgroundSelected"),
        ("WpfUiOverrides", "NavigationViewItemForeground"),
        ("WpfUiOverrides", "NavigationViewItemForegroundPointerOver"),
        ("WpfUiOverrides", "NavigationViewItemForegroundPressed"),
        ("WpfUiOverrides", "SystemFillColorCriticalBrush"),
        ("WpfUiOverrides", "SystemFillColorSuccessBrush"),
        ("WpfUiOverrides", "TabViewItemForegroundSelected"),
        ("WpfUiOverrides", "TabViewItemHeaderBackgroundSelected"),
        ("WpfUiOverrides", "TabViewSelectedItemBorderBrush"),
        ("WpfUiOverrides", "TextFillColorPrimaryBrush"),
        ("WpfUiOverrides", "TextFillColorSecondaryBrush"),
    ];

    [Fact]
    public void AstKeysCollidingWithWpfUi()
    {
        var scope = AppScope.Scope;

        // First, and in both guards: a primary dictionary the partition no longer covers stops the
        // run, and so does an unattributable dictionary. Answering the collision question over a
        // scope whose ownership is guessed is worse than not answering.
        //
        // PARTITION FIRST (wpf-architect F-A5). An unnamed primary key becomes Unattributable
        // under route "App.xaml", so the other order reported a phantom missing-provenance
        // dictionary and buried the real cause.
        MergedScope.RequirePrimaryDictionaryIsPartitioned(scope);
        MergedScope.RequireUnattributableDictionariesArePermitted(scope.Entries);

        // String keys only: a Type key is an implicit style, which is Task 3's subject, not a
        // collision. Kind is read rather than the flattened Key, because in the snapshot every key is
        // already a string (126 F-16 - KeyIsString could not tell a Type from a ComponentResourceKey).
        //
        // No value is realized here either, so this guard cannot be broken by a resource that fails to
        // instantiate. That question belongs to a rendered visual tree, which this host does not have.
        var entries = scope.Entries.Where(e => e.Kind == ScopeKeyKind.String).ToList();

        var wpfUi = entries
            .Where(e => e.Owner == ScopeOwner.WpfUi)
            .Select(e => e.Key)
            .ToHashSet(StringComparer.Ordinal);

        var collisions = entries
            .Where(e => e.Owner == ScopeOwner.Ast && wpfUi.Contains(e.Key))
            .Select(e => (e.Route, e.Key))
            .Distinct()
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ThenBy(e => e.Key, StringComparer.Ordinal);

        collisions.Should().Equal(
            PermittedCollisions.OrderBy(e => e.Route, StringComparer.Ordinal)
                .ThenBy(e => e.Key, StringComparer.Ordinal),
            "the intended retints are the contract, route included; a member leaving means a "
            + "retint now paints nothing, a member arriving means an accidental shadow of a "
            + "stock control, and a member changing route means the retint may no longer win "
            + "the merge it was written to win");
    }
}
