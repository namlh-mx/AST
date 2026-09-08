using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    // Half (B) of backlog 3.72: the outer boxes already match (assertion above); the operator-visible
    // shift is the first glyph's origin inside each presentation. Measure the glyph renderers themselves
    // (Display TextBoxView; Editable selected TextBlock under ContentSite), not their content hosts.
    // Tolerance stays at the suite's existing 0.001 DIP — exact equality is the invariant, and this
    // harness already holds outer ActualWidth/Height to that epsilon at one process DPI.
    [Fact]
    public void Display_and_Editable_first_glyph_origins_match_at_36_and_40_dip() => Sta.RunOnSharedStaThread(() =>
    {
        OffscreenHost.EnsureApplication();

        // Height-dependent failure (F-299-01): Top-pinned ContentSite matched only the natural 36-DIP
        // box; Display recentres when the outer height grows. Exercise both observed field heights.
        foreach (var heightDip in new[] { 36.0, 40.0 })
        {
            var display = MeasureFirstGlyphOrigin(AstOrgUnitPickerMode.Display, heightDip);
            var editable = MeasureFirstGlyphOrigin(AstOrgUnitPickerMode.Editable, heightDip);

            var dx = display.Origin.X - editable.Origin.X;
            var dy = display.Origin.Y - editable.Origin.Y;
            const double tolerance = 0.001;
            var detail =
                $"height={heightDip:F0} Display=({display.Origin.X:F3},{display.Origin.Y:F3}) [{display.RendererKind}] " +
                $"Editable=({editable.Origin.X:F3},{editable.Origin.Y:F3}) [{editable.RendererKind}] " +
                $"delta=({dx:F3},{dy:F3})";

            display.Origin.X.Should().BeApproximately(editable.Origin.X, tolerance,
                $"first-glyph origin X differs: {detail}");
            display.Origin.Y.Should().BeApproximately(editable.Origin.Y, tolerance,
                $"first-glyph origin Y differs: {detail}");
        }
    });

    [Fact]
    public void Editable_closed_selection_text_trims_with_ellipsis_when_label_overflows() => Sta.RunOnSharedStaThread(() =>
    {
        OffscreenHost.EnsureApplication();

        const string longLabel =
            "Đơn vị hành chính cấp tỉnh — tên rất dài để tràn khung đóng của picker cha và buộc cắt chữ";
        var measured = MeasureEditableClosedSelectionText(longLabel, outerWidthDip: 240);

        measured.TextBlock.TextTrimming.Should().Be(TextTrimming.CharacterEllipsis,
            "closed Editable selection must ellipsize overflow instead of a hard clip (card 301 R4)");
        measured.TextBlock.TextWrapping.Should().Be(TextWrapping.NoWrap);
        measured.LaidOutTextWidth.Should().BeLessThan(measured.UnconstrainedTextWidth,
            "label must actually overflow the closed selection host so the ellipsis path is exercised " +
            $"(laidOut={measured.LaidOutTextWidth:F3}, unconstrained={measured.UnconstrainedTextWidth:F3})");
    });

    private static (Point Origin, string RendererKind) MeasureFirstGlyphOrigin(
        AstOrgUnitPickerMode mode,
        double? fieldHeightDip = null)
    {
        const string label = "R2-ROOT — R2-ROOT";
        var resources = OffscreenHost.BuildApplicationResources();
        var picker = new AstOrgUnitPicker
        {
            Style = (Style)resources["AstOrgUnitPicker"],
            Mode = mode,
            DisplayText = label,
            Items = new[] { new OrgUnitPickerItem(1, label), new OrgUnitPickerItem(2, "R2-CHILD — R2-CHILD") },
            SelectedOrgUnitId = 1,
        };
        var field = new AstField
        {
            Style = (Style)resources["AstField"],
            Label = "Đơn vị cha",
            Content = picker,
        };

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
            var comboBox = (ComboBox)picker.Template.FindName("EditableComboBox", picker)!;
            if (fieldHeightDip is { } height)
            {
                // EditableComboBox Height binds to DisplayTextBox.ActualHeight; pin Display to force both.
                displayBox.Height = height;
            }

            comboBox.ApplyTemplate();
            window.UpdateLayout();

            if (fieldHeightDip is { } expectedHeight)
            {
                displayBox.ActualHeight.Should().BeApproximately(expectedHeight, 0.001,
                    "forced field height must land on DisplayTextBox so Editable mirrors it");
            }

            FrameworkElement renderer;
            string kind;
            if (mode == AstOrgUnitPickerMode.Display)
            {
                // WPF-UI's shipped TextBox template hosts text under PassiveScrollViewer (not the stock
                // PART_ContentHost name alone). The operator-visible glyph lives on TextBoxView under that
                // host — locate by the type name the merged WPF-UI 4.3 dictionaries actually produce.
                renderer = FindDescendantByTypeName(displayBox, "TextBoxView")
                    ?? throw new InvalidOperationException(
                        $"Display TextBoxView missing. Visual tree under DisplayTextBox: {DescribeVisualTree(displayBox)}");
                kind = $"TextBoxView; host={DescribeContentHost(displayBox)}";
            }
            else
            {
                comboBox.SelectedItem.Should().NotBeNull(
                    "Editable glyph measurement requires a real selection; a blank SelectedItem would measure nothing and pass");
                var contentSite = (FrameworkElement)comboBox.Template.FindName("ContentSite", comboBox)!;
                contentSite.Should().NotBeNull("ContentSite is the Editable selection presenter");
                renderer = FindDescendant<TextBlock>(contentSite)
                    ?? throw new InvalidOperationException(
                        $"Editable TextBlock missing under ContentSite. Visual tree: {DescribeVisualTree(contentSite)}");
                kind = $"TextBlock under ContentSite; SelectedItem={comboBox.SelectedItem}";
            }

            var origin = renderer.TransformToAncestor(picker).Transform(new Point(0, 0));
            return (origin, kind);
        }
        finally
        {
            window.Close();
            Sta.PumpToIdle();
        }
    }

    private static (TextBlock TextBlock, double LaidOutTextWidth, double UnconstrainedTextWidth) MeasureEditableClosedSelectionText(
        string label,
        double outerWidthDip)
    {
        var resources = OffscreenHost.BuildApplicationResources();
        var picker = new AstOrgUnitPicker
        {
            Style = (Style)resources["AstOrgUnitPicker"],
            Mode = AstOrgUnitPickerMode.Editable,
            DisplayText = label,
            Items = new[] { new OrgUnitPickerItem(1, label) },
            SelectedOrgUnitId = 1,
            Width = outerWidthDip,
        };

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
            Content = picker,
        };
        try
        {
            window.Show();
            picker.ApplyTemplate();
            window.UpdateLayout();

            var comboBox = (ComboBox)picker.Template.FindName("EditableComboBox", picker)!;
            comboBox.ApplyTemplate();
            window.UpdateLayout();

            comboBox.SelectedItem.Should().NotBeNull("overflow measurement requires a real selection");
            var contentSite = (FrameworkElement)comboBox.Template.FindName("ContentSite", comboBox)!;
            contentSite.Should().NotBeNull("ContentSite hosts the closed selection string");
            var textBlock = FindDescendant<TextBlock>(contentSite)
                ?? throw new InvalidOperationException(
                    $"Editable TextBlock missing under ContentSite. Visual tree: {DescribeVisualTree(contentSite)}");

            var laidOutTextWidth = textBlock.ActualWidth;
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return (textBlock, laidOutTextWidth, textBlock.DesiredSize.Width);
        }
        finally
        {
            window.Close();
            Sta.PumpToIdle();
        }
    }

    private static string DescribeContentHost(FrameworkElement displayBox)
    {
        if (displayBox is Control control)
        {
            var named = control.Template?.FindName("PART_ContentHost", control) as FrameworkElement;
            if (named is not null)
                return $"{named.GetType().Name}(PART_ContentHost)";
        }

        var passive = FindDescendantByTypeName(displayBox, "PassiveScrollViewer");
        return passive is null
            ? "none"
            : $"{passive.GetType().Name}(type-walk, no PART_ContentHost name)";
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static FrameworkElement? FindDescendantByTypeName(DependencyObject root, string typeName)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child.GetType().Name == typeName && child is FrameworkElement fe)
                return fe;
            var nested = FindDescendantByTypeName(child, typeName);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static string DescribeVisualTree(DependencyObject root, int depth = 0, int maxDepth = 6)
    {
        if (depth > maxDepth)
            return "";
        var name = root is FrameworkElement { Name: { Length: > 0 } named } ? named : "-";
        var line = $"{new string(' ', depth * 2)}{root.GetType().Name}[{name}]";
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            line += "\n" + DescribeVisualTree(VisualTreeHelper.GetChild(root, i), depth + 1, maxDepth);
        return line;
    }

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
