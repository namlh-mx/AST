using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AST.Controls;
using AST.Core.Presentation;
using FluentAssertions;
using UiTextBox = Wpf.Ui.Controls.TextBox;

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

    // Half (B) of backlog 3.72: the outer boxes already match (assertion above); this checks renderer-
    // element origins (Display TextBoxView; Editable selected TextBlock under ContentSite), not ink and
    // not content hosts. Card 308 renamed the helper — TransformToAncestor of (0,0) never inspected a glyph.
    // Tolerance stays at the suite's existing 0.001 DIP — exact equality is the invariant, and this
    // harness already holds outer ActualWidth/Height to that epsilon at one process DPI.
    // Card 310 restores the 40-DIP arm deleted by card 308: it guards F-299-01 (height-dependent
    // Editable centering vs top-anchored Display). Keep MeasureRendererElementOrigin's honest name.
    [Fact]
    public void Display_and_Editable_renderer_element_origins_match_at_36_and_40_dip() => Sta.RunOnSharedStaThread(() =>
    {
        OffscreenHost.EnsureApplication();

        foreach (var heightDip in new[] { 36.0, 40.0 })
        {
            var display = MeasureRendererElementOrigin(AstOrgUnitPickerMode.Display, heightDip);
            var editable = MeasureRendererElementOrigin(AstOrgUnitPickerMode.Editable, heightDip);

            var dx = display.Origin.X - editable.Origin.X;
            var dy = display.Origin.Y - editable.Origin.Y;
            const double tolerance = 0.001;
            var detail =
                $"height={heightDip:F0} Display=({display.Origin.X:F3},{display.Origin.Y:F3}) [{display.RendererKind}] " +
                $"Editable=({editable.Origin.X:F3},{editable.Origin.Y:F3}) [{editable.RendererKind}] " +
                $"delta=({dx:F3},{dy:F3})";

            display.Origin.X.Should().BeApproximately(editable.Origin.X, tolerance,
                $"renderer-element origin X differs: {detail}");
            display.Origin.Y.Should().BeApproximately(editable.Origin.Y, tolerance,
                $"renderer-element origin Y differs: {detail}");
        }
    });

    // Card 310 / backlog 3.72 half (B): display and editable ink must share one height-independent offset
    // from the box top (same construction as sibling ui:TextBox). Independence comes from the layout
    // construction, not from the sample: every vertical ancestor that owns allocated height surplus is
    // top-anchored — the 1 DIP chrome border row and TextControlThemePadding.Top of 8 — so surplus falls
    // below the desired-size subtree instead of moving its origin. Display gets the same property from
    // WPF-UI's own top-aligned content host. Heights 36/40/44/60 are regression sentinels only: natural
    // height, the exact height that failed before card 310, a nearby continuation, and a visibly tall
    // outlier. Each height must also equal its own 36-DIP baseline so a re-tune at four points still fails.
    [Fact]
    public void Display_Editable_and_sibling_TextBox_first_ink_offsets_are_height_independent_at_96_144_168_dpi()
        => Sta.RunOnSharedStaThread(() =>
        {
            OffscreenHost.EnsureApplication();

            foreach (var dpi in new[] { 96.0, 144.0, 168.0 })
            {
                var displayAt36 = MeasureFirstInkOffsetFromBoxTopPx(AstOrgUnitPickerMode.Display, dpi, 36.0);
                var editableAt36 = MeasureFirstInkOffsetFromBoxTopPx(AstOrgUnitPickerMode.Editable, dpi, 36.0);

                foreach (var heightDip in new[] { 36.0, 40.0, 44.0, 60.0 })
                {
                    var display = MeasureFirstInkOffsetFromBoxTopPx(AstOrgUnitPickerMode.Display, dpi, heightDip);
                    var editable = MeasureFirstInkOffsetFromBoxTopPx(AstOrgUnitPickerMode.Editable, dpi, heightDip);

                    var detail =
                        $"dpi={dpi:F0} height={heightDip:F0} " +
                        $"display={display.PickerOffsetPx:F3} sibling(display-host)={display.SiblingOffsetPx:F3} " +
                        $"editable={editable.PickerOffsetPx:F3} sibling(editable-host)={editable.SiblingOffsetPx:F3} " +
                        $"baseline36 display={displayAt36.PickerOffsetPx:F3} editable={editableAt36.PickerOffsetPx:F3}";

                    display.PickerOffsetPx.Should().BeApproximately(display.SiblingOffsetPx, 0.51,
                        $"display-mode picker ink offset must match sibling ui:TextBox: {detail}");
                    editable.PickerOffsetPx.Should().BeApproximately(editable.SiblingOffsetPx, 0.51,
                        $"editable-mode picker ink offset must match sibling ui:TextBox: {detail}");
                    display.PickerOffsetPx.Should().BeApproximately(editable.PickerOffsetPx, 0.51,
                        $"display-mode and editable-mode picker ink offsets must match: {detail}");
                    display.PickerOffsetPx.Should().BeApproximately(displayAt36.PickerOffsetPx, 0.51,
                        $"display ink offset must not depend on control height: {detail}");
                    editable.PickerOffsetPx.Should().BeApproximately(editableAt36.PickerOffsetPx, 0.51,
                        $"editable ink offset must not depend on control height: {detail}");
                }
            }
        });

    // Card 310 / F-309-02: live measurement was stable only over 1%–20% of peak-above-fill contrast.
    // A perimeter that floors at max(8, 35%) skips a real first row at 25% contrast (fill 20 / peak 120 →
    // threshold 55) and lets a one-row shift pass. This mutation paints that low-contrast first row, shifts
    // it by one pixel, and requires the scanner to report different offsets.
    [Fact]
    public void First_ink_scanner_detects_one_row_shift_of_low_contrast_ink()
    {
        const int fillDarkness = 20;
        const int peakDarkness = 120;
        const int lowContrastDarkness = fillDarkness + (int)((peakDarkness - fillDarkness) * 0.25);
        const int width = 32;
        const int height = 40;
        const int stride = width * 4;
        const int left = 2;
        const int right = 30;
        const int top = 0;
        const int bottom = 40;
        const double boxTopPx = 0.0;

        var baseline = PaintSyntheticInkBand(width, height, stride, left, right, top, bottom,
            fillDarkness, peakDarkness, firstInkRow: 8, firstInkDarkness: lowContrastDarkness);
        var mutated = PaintSyntheticInkBand(width, height, stride, left, right, top, bottom,
            fillDarkness, peakDarkness, firstInkRow: 9, firstInkDarkness: lowContrastDarkness);

        var baselineOffset = MeasureFirstInkOffsetInPixelBand(
            baseline, stride, left, right, top, bottom, boxTopPx, "synthetic-baseline", dpi: 96.0);
        var mutatedOffset = MeasureFirstInkOffsetInPixelBand(
            mutated, stride, left, right, top, bottom, boxTopPx, "synthetic-mutated", dpi: 96.0);

        mutatedOffset.Should().NotBe(baselineOffset,
            $"scanner must see a one-row low-contrast shift (25% of peak-above-fill); " +
            $"fill={fillDarkness} peak={peakDarkness} low={lowContrastDarkness} " +
            $"baseline={baselineOffset:F3} mutated={mutatedOffset:F3}");
    }

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

    // F-302-01: DisplayMemberPath keeps SelectionBoxItem as the record and publishes the display
    // template via ItemTemplateSelector (dotnet/wpf ComboBox.UpdateSelectionBoxItem +
    // ItemsControl.UpdateDisplayMemberTemplateSelector). A hard-coded ContentSite template that
    // binds Text="{Binding}" therefore paints ToString(), not Display — and the suite stayed green
    // while only asserting trimming. This fact must fail at 141d99d.
    [Fact]
    public void Editable_closed_selection_renders_Display_not_item_ToString() => Sta.RunOnSharedStaThread(() =>
    {
        OffscreenHost.EnsureApplication();

        const string display = "R2-ROOT — R2-ROOT";
        var item = new OrgUnitPickerItem(1, display);
        var measured = MeasureEditableClosedSelectionText(display, outerWidthDip: 240);

        measured.TextBlock.Text.Should().Be(display,
            "closed ContentSite must render DisplayMemberPath's value, not the record ToString()");
        measured.TextBlock.Text.Should().NotBe(item.ToString(),
            "record ToString() is the severed-pipeline failure Assurance Advisor measured on 141d99d");
    });

    // F-302-01 future-consumer half: custom ItemTemplate must reach BOTH popup row and closed box.
    // Advisor's probe cleared DisplayMemberPath and assigned a CUSTOM: prefix template; closed still
    // painted the record. Shape reused here.
    [Fact]
    public void Editable_closed_selection_and_popup_row_both_honor_custom_ItemTemplate() => Sta.RunOnSharedStaThread(() =>
    {
        OffscreenHost.EnsureApplication();

        const string display = "R2-ROOT — R2-ROOT";
        const string customPrefix = "CUSTOM:";
        var expected = customPrefix + display;

        var (closedText, popupText, chevronWidth) = MeasureEditableCustomItemTemplateTexts(display, customPrefix, outerWidthDip: 240);

        closedText.Should().Be(expected,
            "closed selection must consume ItemTemplate / SelectionBoxItemTemplate pipeline");
        popupText.Should().Be(expected,
            "generated ComboBoxItem must render ItemTemplate after the control prepares the container");
        chevronWidth.Should().BeApproximately(28.0, 0.001,
            "custom-template probe must not widen or drop the fixed chevron column");
    });

    // F-302-02: visible text budget vs pre-card-297 baseline (64efd65 measured 196.000 DIP at 240).
    // Card 301 compared against rejected 467e8e1 (188.571) and missed the 7.429 DIP regression.
    [Fact]
    public void Editable_closed_selection_text_budget_meets_pre_297_baseline_at_240_dip() => Sta.RunOnSharedStaThread(() =>
    {
        OffscreenHost.EnsureApplication();

        const string longLabel =
            "Đơn vị hành chính cấp tỉnh — tên rất dài để tràn khung đóng của picker cha và buộc cắt chữ";
        var measured = MeasureEditableClosedSelectionText(longLabel, outerWidthDip: 240);

        measured.ChevronColumnWidth.Should().BeApproximately(28.0, 0.001,
            "chevron column stays 28 DIP; budget arithmetic is 240 − 28 − left − right");
        measured.LaidOutTextWidth.Should().BeGreaterThanOrEqualTo(196.0,
            "visible text budget must be at least the pre-card-297 baseline of 196.000 DIP at the 240 DIP probe " +
            $"(laidOut={measured.LaidOutTextWidth:F3}; 141d99d measured 188.571)");
        measured.LaidOutTextWidth.Should().BeLessThan(measured.UnconstrainedTextWidth,
            "budget fact still requires a real overflow so capacity is the constraining host, not the string");
    });

    private static (Point Origin, string RendererKind) MeasureRendererElementOrigin(
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

    private static (double PickerOffsetPx, double SiblingOffsetPx) MeasureFirstInkOffsetFromBoxTopPx(
        AstOrgUnitPickerMode mode,
        double dpi,
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
        var pickerField = new AstField
        {
            Style = (Style)resources["AstField"],
            Label = "Đơn vị cha",
            Content = picker,
        };
        var siblingBox = new UiTextBox
        {
            Text = label,
            IsEnabled = false,
        };
        var siblingField = new AstField
        {
            Style = (Style)resources["AstField"],
            Label = "Mã đơn vị",
            Content = siblingBox,
        };

        // Real form row: `* | 16 | *` with picker in column 0 and a plain ui:TextBox field in column 2.
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(pickerField, 0);
        Grid.SetColumn(siblingField, 2);
        host.Children.Add(pickerField);
        host.Children.Add(siblingField);

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
            Background = Brushes.White,
            Resources = resources,
        };
        window.Content = host;
        try
        {
            window.Show();
            window.UpdateLayout();
            pickerField.ApplyTemplate();
            siblingField.ApplyTemplate();
            picker.ApplyTemplate();
            window.UpdateLayout();

            var displayBox = (FrameworkElement)picker.Template.FindName("DisplayTextBox", picker)!;
            var comboBox = (ComboBox)picker.Template.FindName("EditableComboBox", picker)!;
            if (fieldHeightDip is { } height)
            {
                // EditableComboBox Height binds to DisplayTextBox.ActualHeight; pin Display and sibling
                // so every presentation in the row shares the forced height under test.
                displayBox.Height = height;
                siblingBox.Height = height;
            }

            comboBox.ApplyTemplate();
            window.UpdateLayout();

            if (fieldHeightDip is { } expectedHeight)
            {
                displayBox.ActualHeight.Should().BeApproximately(expectedHeight, 0.001,
                    "forced field height must land on DisplayTextBox so Editable mirrors it");
                siblingBox.ActualHeight.Should().BeApproximately(expectedHeight, 0.001,
                    "forced field height must land on sibling ui:TextBox for the form reference");
            }

            if (mode == AstOrgUnitPickerMode.Editable)
            {
                comboBox.SelectedItem.Should().NotBeNull(
                    "editable ink measurement requires a real selection; a blank SelectedItem would measure nothing and pass");
            }

            FrameworkElement pickerBox = mode == AstOrgUnitPickerMode.Display ? displayBox : comboBox;

            var scale = dpi / 96.0;
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(host.ActualWidth * scale));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(host.ActualHeight * scale));
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(host);

            var stride = pixelWidth * 4;
            var pixels = new byte[pixelHeight * stride];
            bitmap.CopyPixels(pixels, stride, 0);

            var pickerOffset = MeasureFirstInkOffsetFromBoxTopPx(pixels, stride, pixelWidth, pixelHeight, host, pickerBox, dpi,
                excludeRightChevronColumn: mode == AstOrgUnitPickerMode.Editable);
            var siblingOffset = MeasureFirstInkOffsetFromBoxTopPx(pixels, stride, pixelWidth, pixelHeight, host, siblingBox, dpi,
                excludeRightChevronColumn: false);
            return (pickerOffset, siblingOffset);
        }
        finally
        {
            window.Close();
            Sta.PumpToIdle();
        }
    }

    private static byte[] PaintSyntheticInkBand(
        int width,
        int height,
        int stride,
        int left,
        int right,
        int top,
        int bottom,
        int fillDarkness,
        int peakDarkness,
        int firstInkRow,
        int firstInkDarkness)
    {
        var pixels = new byte[height * stride];
        void PaintRow(int y, int darkness)
        {
            var value = (byte)(255 - darkness);
            for (var x = left; x < right; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }

        // Fill band under the top border (same 3-px band the scanner samples at 96 DPI).
        var fillBottom = Math.Min(bottom, top + 3);
        for (var y = top; y < fillBottom; y++)
            PaintRow(y, fillDarkness);

        // Remainder of the content band stays at fill so only the planted rows read as ink.
        for (var y = fillBottom; y < bottom; y++)
            PaintRow(y, fillDarkness);

        PaintRow(firstInkRow, firstInkDarkness);

        // Peak body in the vertical middle so the scanner's peak sample lands above fill.
        var midTop = top + ((bottom - top) / 4);
        var midBottom = bottom - ((bottom - top) / 4);
        var peakRow = midTop + ((midBottom - midTop) / 2);
        PaintRow(peakRow, peakDarkness);
        return pixels;
    }

    private static double MeasureFirstInkOffsetFromBoxTopPx(
        byte[] pixels,
        int stride,
        int bitmapWidth,
        int bitmapHeight,
        Visual ancestor,
        FrameworkElement box,
        double dpi,
        bool excludeRightChevronColumn)
    {
        var scale = dpi / 96.0;
        var topLeft = box.TransformToAncestor(ancestor).Transform(new Point(0, 0));
        var boxLeftPx = topLeft.X * scale;
        var boxTopPx = topLeft.Y * scale;
        var boxRightPx = (topLeft.X + box.ActualWidth) * scale;
        var boxBottomPx = (topLeft.Y + box.ActualHeight) * scale;

        // Content column only: clear the 1-DIP chrome and the TextControlThemePadding.Left (10) so left/right
        // border AA cannot register as the first ink row. Editable also drops the 28-DIP chevron column.
        var insetLeft = 11.0 * scale;
        var insetTop = 2.0 * scale;
        var insetRight = excludeRightChevronColumn ? 30.0 * scale : 11.0 * scale;
        var insetBottom = 2.0 * scale;

        var left = Math.Clamp((int)Math.Ceiling(boxLeftPx + insetLeft), 0, bitmapWidth - 1);
        var right = Math.Clamp((int)Math.Floor(boxRightPx - insetRight), left + 1, bitmapWidth);
        var top = Math.Clamp((int)Math.Ceiling(boxTopPx + insetTop), 0, bitmapHeight - 1);
        var bottom = Math.Clamp((int)Math.Floor(boxBottomPx - insetBottom), top + 1, bitmapHeight);

        return MeasureFirstInkOffsetInPixelBand(
            pixels, stride, left, right, top, bottom, boxTopPx, box.GetType().Name, dpi);
    }

    // Fill-relative first-ink row. Disabled ui:TextBox fill is grey, so absolute darkness is not usable.
    // Live measurement for this repair was stable only over 1%–20% of peak-above-fill contrast; the old
    // max(8, 35%) perimeter skipped real first rows inside that band (F-309-02).
    private static double MeasureFirstInkOffsetInPixelBand(
        byte[] pixels,
        int stride,
        int left,
        int right,
        int top,
        int bottom,
        double boxTopPx,
        string boxName,
        double dpi)
    {
        var scale = dpi / 96.0;
        var fillBottom = Math.Min(bottom, top + Math.Max(1, (int)Math.Ceiling(3.0 * scale)));
        var fillDarkness = 0;
        for (var y = top; y < fillBottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var i = y * stride + x * 4;
                if (pixels[i + 3] < 8)
                    continue;
                var darkness = 255 - ((pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3);
                if (darkness > fillDarkness)
                    fillDarkness = darkness;
            }
        }

        var midTop = top + ((bottom - top) / 4);
        var midBottom = bottom - ((bottom - top) / 4);
        var peakDarkness = fillDarkness;
        for (var y = midTop; y < midBottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var i = y * stride + x * 4;
                if (pixels[i + 3] < 8)
                    continue;
                var darkness = 255 - ((pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3);
                if (darkness > peakDarkness)
                    peakDarkness = darkness;
            }
        }

        (peakDarkness - fillDarkness).Should().BeGreaterThan(0,
            $"no glyph-above-fill ink inside {boxName} at dpi={dpi:F0}; fill={fillDarkness} peak={peakDarkness}");

        // Threshold inside the measured 1%–20% of peak-above-fill contrast (F-309-02). Floor at 1 so a
        // low-contrast first row still registers; the old max(8, 35%) skipped Advisor's 25% counterexample.
        var contrast = peakDarkness - fillDarkness;
        var threshold = fillDarkness + Math.Max(1, (int)Math.Ceiling(contrast * 0.10));
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var i = y * stride + x * 4;
                if (pixels[i + 3] < 8)
                    continue;
                var darkness = 255 - ((pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3);
                if (darkness >= threshold)
                    return y - boxTopPx;
            }
        }

        throw new InvalidOperationException(
            $"peak ink found but no row reached threshold={threshold} inside {boxName} at dpi={dpi:F0}");
    }

    private static (TextBlock TextBlock, double LaidOutTextWidth, double UnconstrainedTextWidth, double ChevronColumnWidth)
        MeasureEditableClosedSelectionText(
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

            var rootGrid = VisualTreeHelper.GetChild(comboBox, 0) as Grid
                ?? throw new InvalidOperationException("ComboBox template root Grid missing for chevron-column measure");
            rootGrid.ColumnDefinitions.Count.Should().BeGreaterThanOrEqualTo(2);
            var chevronWidth = rootGrid.ColumnDefinitions[1].ActualWidth;

            var laidOutTextWidth = textBlock.ActualWidth;
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return (textBlock, laidOutTextWidth, textBlock.DesiredSize.Width, chevronWidth);
        }
        finally
        {
            window.Close();
            Sta.PumpToIdle();
        }
    }

    private static (string ClosedText, string PopupText, double ChevronColumnWidth) MeasureEditableCustomItemTemplateTexts(
        string display,
        string customPrefix,
        double outerWidthDip)
    {
        var resources = OffscreenHost.BuildApplicationResources();
        var picker = new AstOrgUnitPicker
        {
            Style = (Style)resources["AstOrgUnitPicker"],
            Mode = AstOrgUnitPickerMode.Editable,
            DisplayText = display,
            Items = new[] { new OrgUnitPickerItem(1, display) },
            SelectedOrgUnitId = 1,
            Width = outerWidthDip,
        };

        var layoutRoot = new Grid();
        layoutRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(picker, 0);
        layoutRoot.Children.Add(picker);

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
            Content = layoutRoot,
        };
        try
        {
            window.Show();
            picker.ApplyTemplate();
            window.UpdateLayout();

            var comboBox = (ComboBox)picker.Template.FindName("EditableComboBox", picker)!;
            comboBox.ApplyTemplate();
            window.UpdateLayout();

            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(OrgUnitPickerItem.Display))
            {
                StringFormat = customPrefix + "{0}",
            });
            var itemTemplate = new DataTemplate(typeof(OrgUnitPickerItem)) { VisualTree = textFactory };

            // DisplayMemberPath is set by AstOrgUnitPicker's ControlTemplate (TemplateBinding), so
            // ClearValue only reveals the template value again. A local empty string overrides it.
            // ItemsControl rejects DisplayMemberPath together with ItemTemplate.
            comboBox.DisplayMemberPath = string.Empty;
            comboBox.ItemTemplate = itemTemplate;
            // Setting ItemTemplate does not by itself refresh SelectionBoxItemTemplate; re-select
            // so ComboBox.UpdateSelectionBoxItem republishes ItemTemplate onto the closed box.
            var selected = comboBox.Items[0];
            comboBox.SelectedItem = null;
            window.UpdateLayout();
            comboBox.SelectedItem = selected;
            window.UpdateLayout();

            var contentSite = (FrameworkElement)comboBox.Template.FindName("ContentSite", comboBox)!;
            var closedBlock = FindDescendant<TextBlock>(contentSite)
                ?? throw new InvalidOperationException(
                    $"Closed TextBlock missing under ContentSite. Visual tree: {DescribeVisualTree(contentSite)}");

            // Host a container the ComboBox's own generator produces and prepares. Opening the
            // dropdown takes Mouse.Capture and can close on LostMouseCapture (dotnet/wpf
            // ComboBox.cs); a Popup HWND is also non-deterministic off-screen. Generation /
            // PrepareItemContainer observes container style + ItemTemplate without that path.
            // PrepareItemContainer must run after the container is in the visual tree
            // (IItemContainerGenerator.PrepareItemContainer, windowsdesktop-10.0).
            var generator = (IItemContainerGenerator)comboBox.ItemContainerGenerator;
            ComboBoxItem rowContainer;
            using (generator.StartAt(
                       generator.GeneratorPositionFromIndex(0),
                       GeneratorDirection.Forward,
                       allowStartAtRealizedItem: true))
            {
                rowContainer = generator.GenerateNext() as ComboBoxItem
                    ?? throw new InvalidOperationException(
                        "ComboBox ItemContainerGenerator.GenerateNext returned no ComboBoxItem for index 0.");
            }

            Grid.SetRow(rowContainer, 1);
            layoutRoot.Children.Add(rowContainer);
            generator.PrepareItemContainer(rowContainer);
            window.UpdateLayout();
            Sta.PumpToIdle();

            var popupBlock = FindDescendant<TextBlock>(rowContainer)
                ?? throw new InvalidOperationException(
                    $"ItemTemplate TextBlock missing on generated ComboBoxItem. Visual tree: {DescribeVisualTree(rowContainer)}");

            var rootGrid = VisualTreeHelper.GetChild(comboBox, 0) as Grid
                ?? throw new InvalidOperationException("ComboBox template root Grid missing for chevron-column measure");
            rootGrid.ColumnDefinitions.Count.Should().BeGreaterThanOrEqualTo(2);

            return (closedBlock.Text, popupBlock.Text, rootGrid.ColumnDefinitions[1].ActualWidth);
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
