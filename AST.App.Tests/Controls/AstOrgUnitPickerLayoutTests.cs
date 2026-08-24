using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AST.Controls;
using AST.Core.Presentation;
using FluentAssertions;

namespace AST.App.Tests.Controls;

// Tier-2-adjacent: unlike AstOrgUnitPickerTests (pure DP, no real template), this instantiates the REAL
// AstOrgUnitPicker/AstField keyed styles from Controls.xaml -- INCLUDING WPF-UI's own ThemesDictionary/
// ControlsDictionary, per the App.xaml merge order -- inside a real (offscreen) Window and forces a real
// layout pass. dotnet build / loading the ResourceDictionary in isolation never instantiate a
// ControlTemplate, so a collapsed-width regression passes both silently.
//
// The chevron-only-arrow regression (requester F5 report, live-debugged via VS's Live Visual Tree) was NOT
// EditableComboBox itself collapsing -- ElementName-bound Width/Height on EditableComboBox always resolved
// correctly. The real break was one level deeper: PART_ToggleButton (inside AstOrgUnitPickerComboBox's own
// ControlTemplate) rendered at ~30px (just its fixed chevron column) despite Grid.ColumnSpan="2", because it
// had no explicit HorizontalAlignment and picked up WPF-UI's own implicit ToggleButton style's alignment
// instead of stretching -- a style that is ONLY present when ui:ControlsDictionary is actually merged, which
// earlier versions of this test never included. Asserting only EditableComboBox.ActualWidth (as an earlier
// version of this test did) would have stayed green while the actual rendered chevron collapsed -- assert on
// PART_ToggleButton specifically, the element that visually IS the field's chrome.
//
// Application is a per-process, thread-affine singleton. This test does not construct or mutate it: it runs on
// the shared STA thread (Sta.RunOnSharedStaThread) and goes through OffscreenHost.EnsureApplication, which
// supplies the live, correctly-affine dispatcher, the single-owner guarantee, AND the Application-level 7-entry
// merge. Keyed styles are assigned from the Window copy of OffscreenHost.BuildApplicationResources(). That copy
// is not enough on its own: proof P5 (2026-08-15) emptied Application.Resources and this test went RED on
// deferred {StaticResource AstLabelMediumText} during ApplyTemplate — so the Application merge IS load-bearing
// for this control, same cell as OffscreenHost's AstDateBox matrix. A short-lived Sta.Run thread must never
// create Application (EnsureApplication throws if called off the shared dispatcher, including the adopt path).
// Window-level copy details: OffscreenHost's measured matrix (2026-08-15 AstLabelMediumText rows).
public class AstOrgUnitPickerLayoutTests
{
    private static (FrameworkElement DisplayBox, FrameworkElement ComboBox) MeasureInsideAstField(AstOrgUnitPickerMode mode)
    {
        // Window-level copy of OffscreenHost.BuildApplicationResources(). Why a copy is required lives in
        // OffscreenHost's measured matrix (2026-08-15 AstLabelMediumText row), not here.
        var resources = OffscreenHost.BuildApplicationResources();
        var picker = new AstOrgUnitPicker
        {
            Style = (Style)resources["AstOrgUnitPicker"],
            Mode = mode,
            Items = new[] { new OrgUnitPickerItem(1, "R2-ROOT — R2-ROOT"), new OrgUnitPickerItem(2, "R2-CHILD — R2-CHILD") },
        };
        var field = new AstField
        {
            Style = (Style)resources["AstField"],
            Label = "Đơn vị cha",
            Content = picker,
        };

        // Mirrors the real form row: a 2-column Grid (`*`, 16, `*`) hosting the field in column 0, the same
        // layout OrgUnitDeclarationView.xaml uses for "Đơn vị cha" / "Mã đơn vị".
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(field, 0);
        host.Children.Add(field);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 900,
            Height = 400,
            Resources = resources,
        };
        window.Content = host;
        try
        {
            window.Show();
            window.UpdateLayout();
            field.ApplyTemplate();
            picker.ApplyTemplate();
            window.UpdateLayout();

            var displayBox = (FrameworkElement)picker.Template.FindName("DisplayTextBox", picker)!;
            var comboBox = (FrameworkElement)picker.Template.FindName("EditableComboBox", picker)!;
            return (displayBox, comboBox);
        }
        finally
        {
            window.Close();
            Sta.PumpToIdle();
        }
    }

    // Both assertions run on the shared STA thread against OffscreenHost's one Application.
    [Fact]
    public void EditableComboBox_and_its_internal_chrome_never_collapse() => Sta.RunOnSharedStaThread(() =>
    {
        OffscreenHost.EnsureApplication();
        ComboBox? editableComboBox = null;

        foreach (var mode in new[] { AstOrgUnitPickerMode.Display, AstOrgUnitPickerMode.Editable })
        {
            var (displayBox, comboBox) = MeasureInsideAstField(mode);

            comboBox.ActualWidth.Should().BeApproximately(displayBox.ActualWidth, 0.001);
            comboBox.ActualHeight.Should().BeApproximately(displayBox.ActualHeight, 0.001);

            // The chevron-only-arrow regression this guards against renders EditableComboBox at ~28px (its
            // fixed chevron column). A real field row in a 900px-wide test window resolves to ~435px per
            // column -- 100px is a safe floor that only a genuine collapse (not viewport-size variance) would
            // cross.
            displayBox.ActualWidth.Should().BeGreaterThan(100, $"[{mode}] DisplayTextBox collapsed: ActualWidth={displayBox.ActualWidth}");
            comboBox.ActualWidth.Should().BeGreaterThan(100, $"[{mode}] EditableComboBox collapsed: ActualWidth={comboBox.ActualWidth}");

            if (mode == AstOrgUnitPickerMode.Editable)
                editableComboBox = (ComboBox)comboBox;
        }

        // The actual regression (live-debugged via VS's Live Visual Tree): EditableComboBox's own
        // ActualWidth above always resolved correctly, but PART_ToggleButton -- the element whose Border
        // ("Chrome") paints the visible chrome the user sees -- did not fill it, because it had no explicit
        // HorizontalAlignment and picked up WPF-UI's own implicit ToggleButton style instead of stretching.
        var toggle = (FrameworkElement)editableComboBox!.Template.FindName("PART_ToggleButton", editableComboBox)!;

        toggle.ActualWidth.Should().BeApproximately(editableComboBox.ActualWidth, 0.001);
        toggle.ActualWidth.Should().BeGreaterThan(100, $"PART_ToggleButton collapsed: ActualWidth={toggle.ActualWidth}");
    });

    [Fact]
    public void EnsureApplication_owns_Application_on_the_shared_STA_thread() => Sta.RunOnSharedStaThread(() =>
    {
        OffscreenHost.EnsureApplication();
        Application.Current.Should().NotBeNull();
        Application.Current!.Dispatcher.Thread.Name.Should().Be(Sta.SharedStaThreadName);
    });

    [Fact]
    public void EnsureApplication_throws_when_called_off_the_shared_STA_thread()
    {
        var act = () => Sta.Run(OffscreenHost.EnsureApplication);
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Sta.RunOnSharedStaThread")
            .And.Contain("Do not call it from Sta.Run");
    }

    [Fact]
    public void DecideEnsureApplication_wrong_thread_fails_even_when_application_owner_is_shared()
    {
        var (current, shared) = TwoStaDispatchers();

        var verdict = OffscreenHost.DecideEnsureApplication(current, shared, applicationOwner: shared);

        verdict.Kind.Should().Be(OffscreenHost.EnsureApplicationKind.Fail);
        verdict.FailureMessage.Should().Contain("Sta.RunOnSharedStaThread")
            .And.Contain("Do not call it from Sta.Run");
    }

    [Fact]
    public void DecideEnsureApplication_adopt_mismatch_fails()
    {
        var (shared, owner) = TwoStaDispatchers();

        var verdict = OffscreenHost.DecideEnsureApplication(shared, shared, applicationOwner: owner);

        verdict.Kind.Should().Be(OffscreenHost.EnsureApplicationKind.Fail);
        verdict.FailureMessage.Should().Contain("not owned by the shared STA dispatcher");
    }

    [Fact]
    public void DecideEnsureApplication_matching_owner_adopts()
    {
        Dispatcher? shared = null;
        Sta.Run(() => shared = Dispatcher.CurrentDispatcher);

        var verdict = OffscreenHost.DecideEnsureApplication(shared!, shared!, applicationOwner: shared);

        verdict.Kind.Should().Be(OffscreenHost.EnsureApplicationKind.Adopt);
        verdict.FailureMessage.Should().BeNull();
    }

    [Fact]
    public void DecideEnsureApplication_no_application_constructs()
    {
        Dispatcher? shared = null;
        Sta.Run(() => shared = Dispatcher.CurrentDispatcher);

        var verdict = OffscreenHost.DecideEnsureApplication(shared!, shared!, applicationOwner: null);

        verdict.Kind.Should().Be(OffscreenHost.EnsureApplicationKind.Construct);
        verdict.FailureMessage.Should().BeNull();
    }

    private static (Dispatcher First, Dispatcher Second) TwoStaDispatchers()
    {
        Dispatcher? first = null;
        Dispatcher? second = null;
        Sta.Run(() => first = Dispatcher.CurrentDispatcher);
        Sta.Run(() => second = Dispatcher.CurrentDispatcher);
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().NotBeSameAs(second);
        return (first!, second!);
    }
}
