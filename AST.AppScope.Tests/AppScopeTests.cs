using FluentAssertions;

namespace AST.AppScope.Tests;

// META guard on the instrument itself. Every other guard in this project reads the scope this test
// describes, so a silent change to App.xaml would change what those guards MEAN while they stayed
// green. A count would let one dictionary replace another, so nothing here counts.
//
// The merge list is asserted as an ORDERED SEQUENCE, not a set, and deliberately:
// App.xaml's own comment records that this order is load-bearing - tokens before the styles that
// consume them, WpfUiOverrides last so its retints win - so reordering it IS a change worth reddening.
// The primary dictionary's keys are a set: both sides are sorted, so their order carries nothing.
//
// What this test does NOT claim, after the 2026-08-20 measurement: that the keys in the primary
// dictionary are AST's. Twenty-one of the thirty-one are WPF-UI's, written there by the library
// itself, and no runtime evidence separates them from AST's ten. What separates them is
// App.xaml's own SOURCE on one side and a named list on the other, checked for coverage AND overlap
// - see MergedScope.
//
// ⚠️ Nothing here marshals anything. AppScope.Scope is an immutable record -- narrowed on
// ScopeSnapshot: the INTENT, not a mechanism -- captured on the owning STA
// thread, which has since exited, so a failing assertion in this file cannot reach the host by any
// path at all. The marshalling test that used to live here (RunOnRethrowsBodyFailureOnTheCallingThread)
// was deleted with AppScope.RunOn on 2026-08-20: with no marshalling path left, it had no subject.
//
// What a GREEN run does NOT prove (added 2026-08-21, Q-17 - this file is the fourth guard file and
// was the one without this paragraph):
//   - THAT THE VALUES BEHIND THE POST-WARM-UP KEYS ARE PRODUCTION'S. They are not. This host triggers
//     the SYSTEM accent by reading UiApplication.Current.Resources; the app applies its BRANDED accent
//     in OnInitialized, which this host never reaches. The 21 injected NAMES are the contract asserted
//     here; their colours are not.
//   - ANYTHING ABOUT DARK THEME. Everything in this project is measured under
//     ThemesDictionary Theme="Light".
public class AppScopeTests
{
    [Fact]
    public void RealAppXamlLoadsWithoutPrism()
    {
        var scope = AppScope.Scope;

        scope.AppTypeFullName.Should().Be(
            typeof(global::AST.App).FullName,
            "the point of this project is that the scope under test is the REAL App.xaml, not a replica "
            + "kept in step by inspection - that replica already exists in AST.App.Tests/OffscreenHost.cs "
            + "and is a different harness for a different question");
        scope.AppIsTheProcessApplication.Should().BeTrue("AppScope must own the process Application");

        scope.MergedLabels.Should().Equal(
            "Light", "Wpf.Ui", "Palette", "Typography", "Spacing", "Controls", "WpfUiOverrides");

        // NEW, and only possible now: the state BEFORE anything forces WPF-UI's injection is exactly what
        // App.xaml declares. The old host could never observe this - Prism's crash path had already
        // injected by the time any guard body ran, which is precisely why the count looked like a constant.
        // The two states are two DIFFERENT contracts, and neither stands in for production (125's
        // MISSING OPTION, adopted): before the warm-up this dictionary is App.xaml's DECLARATION contract;
        // after it, the WPF-UI INJECTED-KEY contract. Nothing here is a production-state surrogate.
        scope.PrimaryStringKeysBeforeWarmup.Should().Equal(
            MergedScope.AstPrimaryKeys(MergedScope.RepoRoot()),
            "before the accent warm-up the primary dictionary is App.xaml's own declarations and nothing "
            + "else; if this fails, either App.xaml changed or something read a WPF-UI resource first");

        // A NAMED baseline of WHICH resources cannot be instantiated, WHOSE they are, and WHAT SHAPE the
        // failure has -- and deliberately NOT of the exception prose that says why. Measured 2026-08-20:
        // exactly one. Pinning is not excusing; the diagnosis lives in the backlog.
        //
        // ⚠️ HOW TO READ A FAILURE HERE, which the old single-string baseline could not answer. The diff
        // names the entry and its OWNER. A `WpfUi`-owned entry appearing after an upgrade is a decision to
        // record here with a reason; an `Ast`-owned one is a defect to fix.
        //
        // ⚠️ AN ENTRY DISAPPEARING IS **TWO** THINGS, AND A SET COMPARISON CANNOT TELL THEM APART
        //. Either the value now realizes -- good news, delete the
        // row -- or the key or its dictionary LEFT THE WALK, in which case deleting the row silently
        // retires the guard for it. Nothing else here would notice: MergedLabels catches a whole
        // dictionary vanishing, never one key vanishing from inside one. Establish WHICH before deleting.
        //
        // What this still cannot tell you: whether a known entry began failing for a DIFFERENT reason
        // with the same exception shape. That gap is accepted, not overlooked (spec
        // `### Candidates U1-U3`).
        scope.UnreadableValues.Should().Equal(
            [
                // Identity MEASURED 2026-08-20; this recorded shape OBSERVED 2026-08-21 from the run, not
                // composed. Format: owner/route/label/key: ExceptionFullName [inner=InnerExceptionFullName].
                "WpfUi/Light/Light/BadgeBackground: System.Windows.Markup.XamlParseException [inner=System.Exception]"
            ],
            "a resource whose value cannot be instantiated is a defect somewhere in the merged scope. One is "
            + "known and pinned; a new entry here is a decision to make, not a list to top up");

        MergedScope.RequirePrimaryDictionaryIsPartitioned(scope);

        scope.PrimaryNonStringKeysAfterWarmup.Should().BeEmpty(
            "App.xaml declares only string-keyed converters today; a non-string key here is an implicit "
            + "style or template in the strongest scope in the app, and it must be a decision rather than "
            + "a discovery");
    }
}
