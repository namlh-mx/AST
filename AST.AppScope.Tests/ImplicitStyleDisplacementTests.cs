using FluentAssertions;

namespace AST.AppScope.Tests;

// GUARD - a keyed style with no BasedOn REPLACES the implicit style for its TargetType rather than
// extending it, so a WPF-UI-styled control loses its template and renders as bare framework chrome
// (the shape recorded in memory feedback-wpfui-style-basedon).
//
// The rule is narrower than "every keyed style must carry BasedOn", and the narrowing is mechanical
// rather than a matter of taste: if the merged scope holds NO implicit style for the TargetType,
// there is nothing to displace and BasedOn is meaningless. Measured 2026-08-20, that alone excuses
// AstActionBar (StackPanel) and AstCard (Border) with no list entry needed - neither type appears
// among the 94 distinctly styled types in this scope (94: MEASURED 2026-08-20, not asserted - no
// assertion holds this number, so it is evidence for a decision and never a guarantee).
//
// What remains needs a named, approved set, because each member is a DELIBERATE replacement with a
// reason recorded next to it. Held as a set and compared both ways: a new offender fails, and an
// exception that gains a BasedOn must leave this list instead of rotting in it.
//
// TWO different universes, and conflating them is what R4 and R5 both caught:
//   DEFAULTS come from ANY owner - AST's, WPF-UI's or a third vendor's - because what matters is
//     whether something exists to displace, not who wrote it.
//   OFFENDER DECLARATIONS are AST-owned only. A third library's keyed no-BasedOn style displaces
//     just as effectively, but nobody in this repo can fix it, and the spec requires that another
//     control library must not block this build. It is excluded on purpose, and "does not prove"
//     says so rather than the comment implying otherwise.
// It deliberately does NOT mean the framework theme default: Application.TryFindResource falls
// through past Application.Resources into theme/system resources, so asking it "is there an implicit
// style for this type" answers a wider question than this guard is making a claim about.
// Reading the captured dictionaries directly is what makes the two distinguishable at all.
//
// ⚠️ Nothing here touches a WPF object. AppScope.Scope is an immutable snapshot -- narrowed on
// ScopeSnapshot: the INTENT, not a mechanism -- captured on a thread
// that has since exited, so a Style is read through the two fields the capture projected from it -
// StyleTargetTypeFullName and StyleHasBasedOn - and a Type key through KeyTypeFullName (125 F-12).
// Both carry a value ONLY when the entry's value really is a Style, which is what keeps this rule
// from degrading into the weaker "key presence" rule R3 deleted: a Type key whose value
// is a ControlTemplate or a DataTemplate is not an implicit style and must not count as one.
//
// ⚠️ Types are matched by FULL NAME, not by identity, because a Type object cannot leave the capture
// thread. Two same-named types from different assemblies would be conflated; MEASURED 2026-08-20 (not
// asserted), the 94 styled types in this scope are all distinct by full name (Wpf.Ui.Controls.TextBox
// and System.Windows.Controls.TextBox are two different strings, which is the case that would
// matter). Nothing re-checks that distinctness on a later run, so a conflation arriving with a new
// dependency is invisible here - see the uncovered-case list in the design notes.
//
// ⚠️ AN UNREADABLE VALUE SILENTLY SHRINKS BOTH SIDES OF THIS GUARD.
// When MergedScope.Project cannot realize a value, `style` stays null and the entry becomes
// indistinguishable from "not a Style": it leaves the DEFAULTS universe (so there is less to displace)
// and it leaves the OFFENDER set (so an AST style that failed to realize is excused). Neither shows up
// here. The only thing that catches it is AppScopeTests' exact one-entry UnreadableValues baseline --
// which lives in a different test class from this guard, and that separation is the risk.
//
// What a GREEN run does NOT prove: that a style with BasedOn inherits the RIGHT default, that any
// control renders, that a keyed style is safe when its type is styled only by the framework theme
// (deliberately out of scope, see above), or anything about Dark theme.
//
// ⚠️ AND FOUR MORE, added 2026-08-21 because the plan claimed "exactly two gaps" and there are six:
//   - A KEYED STYLE INSIDE A ControlTemplate.Resources IS NEVER WALKED, and there is a LIVE instance:
//     Controls.xaml's AstEffectivePeriodUndeterminedCheck (TargetType="CheckBox", no BasedOn) sits in
//     the AstEffectivePeriod template's own Resources. Walk reads dictionary.Keys and recurses into
//     MergedDictionaries; a template's Resources is neither. So on this guard's OWN rule the offenders
//     are 5 or 6 -- 6 iff an app-scope implicit style for System.Windows.Controls.CheckBox exists for it
//     to displace, which is REASONED (WPF-UI's ControlsDictionary ships one) and NOT measured
//. The GAP ITSELF is bounded and the bound is MEASURED 2026-08-21, RE-MEASURED
//     the same day after the first figure was wrong: template scope repo-wide is 3 ControlTemplate.Resources
//     BLOCKS, ALL in Controls.xaml, holding 4 keyed resources -- 3 converters and this one Style. There is
//     NO DataTemplate.Resources block anywhere in this repo. (The first version said "7 ... blocks": that
//     was the raw grep LINE count -- 3 opening tags + 3 closing tags + 1 prose mention -- read as a block
//     count, and it named a form that has never existed here. A number is not measured because a command
//     produced it.) So the walk's blind spot is +1 style
//     today, not an open set; it grows with each new screen, which is why the number is dated. The
//     permitted list below is not exhaustive. Widening the
//     walk is a NEW MECHANISM and the static half deliberately decided the opposite way, so it is a
//     design question and is NOT SCHEDULED. Recording the gap is not closing it.
//   - ScopeKeyKind.Other falls through BOTH guards and nothing asserts on it; and the defaults
//     universe is described as "any owner" while omitting Unattributable, which has no owner filter
//     in the code.
//   - This rule asks about the STYLE's TargetType, but a control's real default comes from its
//     DefaultStyleKey, which may name a DIFFERENT type. AstPasswordBox does exactly that: it overrides
//     DefaultStyleKey to typeof(PasswordBox) so it inherits the base Fluent template. Latent today
//.
//   - Type matching by full name (see above) conflates two same-named types from different
//     assemblies, which can produce a false offender or preserve a stale permitted entry.
public class ImplicitStyleDisplacementTests
{
    // Measured 2026-08-19 over the real App.xaml merged scope, re-measured 2026-08-20 through the
    // captured snapshot. Reason per member, because a list without reasons becomes a place to hide the
    // next mistake:
    //   AstDisplayText / AstBodyLargeText / AstLabelMediumText - the three ROOTS of the AST type
    //     scale, one per Segoe UI Variable optical axis (Display / Text / Small), documented at the
    //     top of Typography.xaml. Each defines the full property set deliberately; inheriting WPF-UI's
    //     implicit TextBlock style would pull its font and foreground into an AST-defined scale.
    //     The five derived steps DO carry BasedOn, onto these roots.
    //   AstSelectableValueText - LOCKED in docs/shared-components.md. A read-only TextBox is the only
    //     WPF primitive with built-in selection; dropping WPF-UI's implicit style is precisely how it
    //     reads as a label instead of an input.
    //   AstOrgUnitPickerComboBox - re-templated from scratch, so there is no default left to extend.
    //
    // Route-labelled like Task 2, and for the same reason: moving one of these into another dictionary
    // changes which declaration wins, and a key-only set would call that no change at all. Line
    // numbers are deliberately absent - a style is found by its key, and a line anchor rots
    // (rule-recording).
    private static readonly (string Route, string Key)[] PermittedReplacements =
    [
        ("Controls", "AstOrgUnitPickerComboBox"),
        ("Controls", "AstSelectableValueText"),
        ("Typography", "AstBodyLargeText"),
        ("Typography", "AstDisplayText"),
        ("Typography", "AstLabelMediumText"),
    ];

    [Fact]
    public void KeyedStylesDisplacingAnImplicitStyle()
    {
        var scope = AppScope.Scope;

        // First, and in both guards: a primary dictionary the partition no longer covers stops the
        // run, and so does an unattributable dictionary. Answering this question over a scope whose
        // ownership is guessed is worse than not answering.
        //
        // PARTITION FIRST (wpf-architect F-A5).
        MergedScope.RequirePrimaryDictionaryIsPartitioned(scope);
        MergedScope.RequireUnattributableDictionariesArePermitted(scope.Entries);

        // The scope's implicit styles: a Type key anywhere in the app scope - the primary dictionary
        // included (a style declared beside App.xaml's converters outranks every merged
        // one and was invisible until R3), and every vendor included, because an implicit style
        // displaces regardless of who shipped it. Read from the captured dictionaries themselves,
        // never through TryFindResource, which would also answer for the framework theme.
        var implicitlyStyled = scope.Entries
            .Where(e => e.Kind == ScopeKeyKind.Type && e.StyleTargetTypeFullName is not null)
            .Select(e => e.KeyTypeFullName!)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = scope.Entries
            .Where(e => e.Kind == ScopeKeyKind.String && e.Owner == ScopeOwner.Ast)
            // StyleHasBasedOn is null unless the value IS a Style, so this one comparison carries both
            // "it is a style" and "it does not extend anything".
            .Where(e => e.StyleHasBasedOn == false && e.StyleTargetTypeFullName is not null)
            // The whole rule, and it is a rule rather than a list: is there an app-scope implicit
            // style for this type to displace? If not, BasedOn would have nothing to inherit and its
            // absence is not a defect. That alone excuses AstCard (Border) and AstActionBar
            // (StackPanel) with no entry in the list below.
            .Where(e => implicitlyStyled.Contains(e.StyleTargetTypeFullName!))
            .Select(e => (e.Route, e.Key))
            // .Distinct() DIVERGES from the reviewed draft, deliberately: two
            // offender declarations sharing a (Route, Key) collapse to one. Kept, because the
            // assertion below compares against a SET of permitted pairs and a duplicated pair would
            // make that comparison unreadable rather than stricter.
            .Distinct()
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ThenBy(e => e.Key, StringComparer.Ordinal);

        offenders.Should().Equal(
            PermittedReplacements.OrderBy(e => e.Route, StringComparer.Ordinal)
                .ThenBy(e => e.Key, StringComparer.Ordinal),
            "each permitted member is a deliberate replacement with a recorded reason; a new "
            + "member is a style that lost its template without anyone deciding to");
    }
}
