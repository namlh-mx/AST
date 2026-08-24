using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace AST.App.Tests;

// Hosts a test body inside a REAL, shown-but-invisible Window under a real Application carrying AST/App.xaml's
// resource merge, so a control can be exercised through its REAL keyed style and template instead of a
// hand-built FrameworkElementFactory stand-in (see AstDateBoxTests.BuildTemplate, which deliberately avoids the
// real dictionaries).
//
// Why a real shown Window: a keyed style only produces a live visual tree once the element is in a rendered
// tree and the dispatcher has reached ApplicationIdle (measure -> ApplyTemplate -> OnApplyTemplate).
// Off-screen + ShowActivated=false + never Activate() keeps it from stealing focus on the requester's desktop.
//
// Why an Application AND a window-level copy of the same merge -- measured 2026-08-07 in this test host, not
// assumed. The AstDateBox ControlTemplate in Controls.xaml contains a DEFERRED {StaticResource AstRadiusCard}
// (a Spacing.xaml token) that is only resolved when the template is instantiated. Measured matrix, one config
// per dotnet test run, everything else equal:
//   merge on Application.Resources only ............................ FAILS "Cannot find resource 'AstRadiusCard'"
//     (still fails when that dictionary is XamlReader-parsed exactly like App.xaml, and when the host thread
//      runs a real Application.Run message loop -- so it is not a "programmatic vs BAML merge" difference;
//      Application.Current.Resources["AstRadiusCard"] resolves fine at that same moment)
//   merge on Window.Resources only, no Application ................. FAILS the same way
//   merge on Window.Resources, Application with empty Resources .... FAILS the same way
//   full merge on Application + only Spacing.xaml on the Window .... FAILS the same way
//   window.Resources set to the SAME instance as Application's ..... FAILS the same way
//   full merge on Application.Resources AND on Window.Resources .... PASSES
// 2026-08-15, different control / different token (not a restatement of the AstDateBox row): AstOrgUnitPicker
// hosted in AstField, deferred {StaticResource AstLabelMediumText} (Typography.xaml) inside the real
// ControlTemplate, via AstOrgUnitPickerLayoutTests. Same host, one config per run:
//   full 7-entry merge on Application.Resources only, Window has no copy
//     .............................................................. FAILS "Cannot find resource named
//                                                                        'AstLabelMediumText'"
//   full 7-entry merge on Window.Resources, Application with empty Resources
//     .............................................................. FAILS "Cannot find resource named
//                                                                        'AstLabelMediumText'" (P5, 2026-08-15)
//   full 7-entry merge on Window.Resources (Application also created via EnsureApplication with the same replica)
//     .............................................................. PASSES
// That second row is why BuildApplicationResources is internal: AstOrgUnitPickerLayoutTests takes a Window-
// level copy of THIS replica rather than appending dictionaries onto Application.Resources. The quirk is
// therefore not AstDateBox-specific, and is still not root-caused. The shipping app merges at Application
// level only (AST/App.xaml) and renders both controls fine. Consequence to keep in mind: this harness can
// prove a style resolves through the app's dictionary LIST, but it cannot prove that app-level-only
// PLACEMENT suffices. Do not "simplify" either copy away -- every removal in the matrix above was measured
// red.
//
// Application is a per-process, thread-affine singleton, which is why everything here runs on the one shared
// STA thread (Sta.RunOnSharedStaThread) rather than on a fresh Sta.Run thread per test.
internal static class OffscreenHost
{
    /// <summary>Runs <paramref name="body"/> against a shown, off-screen window carrying the app's resources.</summary>
    public static void Run(Action<Window> body) => Run(_ => new UIElement(), (window, _) => body(window));

    /// <summary>
    /// Builds content with <paramref name="createContent"/> (the window is passed in so a keyed style can be
    /// resolved off the real merged dictionaries), hosts it in the off-screen window, pumps the dispatcher to
    /// idle so the real style and template are actually applied, then runs <paramref name="body"/> against the
    /// window and that content.
    /// </summary>
    public static void Run<TContent>(Func<Window, TContent> createContent, Action<Window, TContent> body)
        where TContent : UIElement
        => Sta.RunOnSharedStaThread(() =>
        {
            EnsureApplication();
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                // Far off every plausible virtual-screen extent: shown (so templates apply) but never seen.
                Left = -32000,
                Top = -32000,
                Width = 1,
                Height = 1,
                Resources = BuildApplicationResources(),
            };
            try
            {
                var content = createContent(window);
                window.Content = content;
                window.Show(); // never Activate() -- the requester's foreground window must not change
                Sta.PumpToIdle();
                body(window, content);
            }
            finally
            {
                window.Close();
                Sta.PumpToIdle();
            }
        });

    // Sole owner of the process-wide Application singleton. Must run on the shared STA thread — enforced
    // below, including the adopt path (a non-null Application.Current created on a different dispatcher is
    // a defect, not a skip). Application.Current is process-wide, so construction happens at most once per
    // test-host process; OnExplicitShutdown keeps closing the last host window from tearing the dispatcher
    // down. Any test that needs Application (OffscreenHost.Run, layout tests) goes through this method
    // rather than constructing or mutating Application itself.
    internal static void EnsureApplication()
    {
        var shared = Sta.SharedStaUiDispatcher;
        var verdict = DecideEnsureApplication(
            Dispatcher.CurrentDispatcher,
            shared,
            Application.Current?.Dispatcher);

        if (verdict.FailureMessage is not null)
        {
            throw new InvalidOperationException(verdict.FailureMessage);
        }

        if (verdict.Kind is EnsureApplicationKind.Construct)
        {
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
                Resources = BuildApplicationResources(),
            };
        }
    }

    internal enum EnsureApplicationKind
    {
        Construct,
        Adopt,
        Fail,
    }

    internal readonly record struct EnsureApplicationVerdict(EnsureApplicationKind Kind, string? FailureMessage)
    {
        internal static EnsureApplicationVerdict Construct { get; } = new(EnsureApplicationKind.Construct, null);

        internal static EnsureApplicationVerdict Adopt { get; } = new(EnsureApplicationKind.Adopt, null);

        internal static EnsureApplicationVerdict Fail(string message) => new(EnsureApplicationKind.Fail, message);
    }

    // Ownership decision for EnsureApplication: given the calling dispatcher, the shared STA dispatcher,
    // and the dispatcher that owns Application.Current (or null if none), what should happen. Pure —
    // no Application, so the adopt-mismatch branch is testable with ordinary Sta.Run dispatchers.
    internal static EnsureApplicationVerdict DecideEnsureApplication(
        Dispatcher current,
        Dispatcher shared,
        Dispatcher? applicationOwner)
    {
        if (!ReferenceEquals(current, shared))
        {
            return EnsureApplicationVerdict.Fail(
                "OffscreenHost.EnsureApplication must run on the shared STA UI thread " +
                $"(Sta.RunOnSharedStaThread, '{Sta.SharedStaThreadName}'). " +
                "Do not call it from Sta.Run — that thread dies with the test and cannot own Application.");
        }

        if (applicationOwner is not null && !ReferenceEquals(applicationOwner, shared))
        {
            return EnsureApplicationVerdict.Fail(
                "Application.Current exists but is not owned by the shared STA dispatcher. " +
                "Do not construct Application on a short-lived Sta.Run thread; go through " +
                "OffscreenHost.EnsureApplication on Sta.RunOnSharedStaThread.");
        }

        if (applicationOwner is not null)
        {
            return EnsureApplicationVerdict.Adopt;
        }

        return EnsureApplicationVerdict.Construct;
    }

    /// <summary>
    /// Verbatim replica of AST/App.xaml's flat 7-entry merge -- same entries, same order, same pack URIs.
    /// The flatness and the order are load-bearing for the reasons App.xaml documents: ThemesDictionary must be
    /// a TOP-LEVEL entry of the merge that owns it (that is where ApplicationThemeManager looks for it), and
    /// Controls.xaml resolves its {x:Type ui:...} BasedOn and its token StaticResources against its PRECEDING
    /// SIBLINGS here (it never self-merges them).
    /// Guard strength, measured rather than assumed: dropping Controls.xaml or ui:ControlsDictionary
    /// reddens TWO consumers of this replica — Controls/AstDateBoxRealStyleTests.cs (2026-08-07: 'AstDateBox'
    /// not found / Fluent TextBox chrome fails to build) AND Controls/AstOrgUnitPickerLayoutTests.cs
    /// (2026-08-15: deferred AstLabelMediumText / collapsed-chrome pin). Moving Controls.xaml AHEAD of the
    /// token dictionaries does NOT — merged-dictionary lookups at template-instantiation time search the whole
    /// collection, so this harness cannot police the ORDER, only the membership. Order stays a real App.xaml
    /// rule (parse-time BasedOn / accent application); keep this list identical to App.xaml by inspection.
    /// </summary>
    internal static ResourceDictionary BuildApplicationResources()
    {
        EnsurePackSchemeRegistered();

        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        resources.MergedDictionaries.Add(new ControlsDictionary());
        resources.MergedDictionaries.Add(DesignSystem("Palette"));
        resources.MergedDictionaries.Add(DesignSystem("Typography"));
        resources.MergedDictionaries.Add(DesignSystem("Spacing"));
        resources.MergedDictionaries.Add(DesignSystem("Controls"));
        resources.MergedDictionaries.Add(DesignSystem("WpfUiOverrides"));
        return resources;
    }

    private static ResourceDictionary DesignSystem(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/AST.UI;component/Resources/DesignSystem/{name}.xaml", UriKind.Absolute),
    };

    // The "pack" URI scheme is registered by a static initializer that a WPF app normally trips on its way up.
    // Registering it explicitly keeps this harness independent of WHEN the Application above is constructed --
    // without it the first pack:// Source can throw "Invalid URI: Invalid port specified".
    private static void EnsurePackSchemeRegistered()
    {
        if (!UriParser.IsKnownScheme(PackUriSchemeName))
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        }
    }

    private const string PackUriSchemeName = "pack";
}
