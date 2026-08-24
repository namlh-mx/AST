using System.Windows;
using System.Windows.Controls;
using AST.Controls;

namespace AST.App.Tests.Controls;

// Tier-1 headless coverage of AstEffectivePeriod after the 2026-07-23 rebuild: DP defaults/round-trips + the
// IsUndetermined <-> To interlock wired in OnApplyTemplate. Date parsing/masking itself now lives entirely in
// AstDateBox (see AstDateBoxTests) -- this control only composes two AstDateBox atoms + the checkbox, so its
// own tests cover ONLY the composition/interlock, not text parsing. The masked-input visual template + the
// calendar-glyph chrome are the Tier-2 requester F5 gate. Semantic invariant asserted: IsUndetermined
// discriminates "open end" from "missing To" -- To == null no longer means both.
public class AstEffectivePeriodTests
{
    // Minimal template with the 3 named parts, matching the real keyed style's part names/types exactly
    // (Controls.xaml's PART_FromBox/PART_ToBox are now AstDateBox, PART_UndeterminedCheck is a plain
    // CheckBox). FrameworkElementFactory (not XamlReader) keeps this test independent of the XAML file.
    private static ControlTemplate BuildTemplate()
    {
        var template = new ControlTemplate(typeof(AstEffectivePeriod));
        var root = new FrameworkElementFactory(typeof(StackPanel));
        var fromBox = new FrameworkElementFactory(typeof(AstDateBox), "PART_FromBox");
        var toBox = new FrameworkElementFactory(typeof(AstDateBox), "PART_ToBox");
        var check = new FrameworkElementFactory(typeof(CheckBox), "PART_UndeterminedCheck");
        root.AppendChild(fromBox);
        root.AppendChild(toBox);
        root.AppendChild(check);
        template.VisualTree = root;
        return template;
    }

    [Fact]
    public void From_default_is_null()
        => Assert.Null(AstEffectivePeriod.FromProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void To_default_is_null()
        => Assert.Null(AstEffectivePeriod.ToProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void IsUndetermined_default_is_false()
        => Assert.False((bool)AstEffectivePeriod.IsUndeterminedProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void Today_default_is_default_date()
        => Assert.Equal(default, (DateOnly)AstEffectivePeriod.TodayProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void From_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var ep = new AstEffectivePeriod { From = new DateOnly(2026, 7, 23) };
        Assert.Equal(new DateOnly(2026, 7, 23), ep.From);
    });

    [Fact]
    public void To_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var ep = new AstEffectivePeriod { To = new DateOnly(2027, 1, 1) };
        Assert.Equal(new DateOnly(2027, 1, 1), ep.To);
    });

    [Fact]
    public void IsUndetermined_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var ep = new AstEffectivePeriod { IsUndetermined = true };
        Assert.True(ep.IsUndetermined);
    });

    [Fact]
    public void To_null_with_IsUndetermined_false_is_the_missing_case_not_open_end() => Sta.Run(() =>
    {
        // The bug this rebuild fixes: previously To == null alone could not tell "missing" from "open end".
        var ep = new AstEffectivePeriod { From = new DateOnly(2026, 7, 23), To = null, IsUndetermined = false };
        Assert.Null(ep.To);
        Assert.False(ep.IsUndetermined); // the VM reads this combination as MISSING -> BLOCK, per spec §1.4
    });

    [Fact]
    public void Checking_IsUndetermined_clears_To_and_disables_the_To_box() => Sta.Run(() =>
    {
        var ep = new AstEffectivePeriod { Template = BuildTemplate(), To = new DateOnly(2027, 1, 1) };
        ep.ApplyTemplate();

        ep.IsUndetermined = true;

        Assert.Null(ep.To);
        var toBox = (AstDateBox)ep.Template.FindName("PART_ToBox", ep)!;
        Assert.False(toBox.IsEnabled);
    });

    [Fact]
    public void Unchecking_IsUndetermined_reenables_the_To_box() => Sta.Run(() =>
    {
        var ep = new AstEffectivePeriod { Template = BuildTemplate(), IsUndetermined = true };
        ep.ApplyTemplate();

        ep.IsUndetermined = false;

        var toBox = (AstDateBox)ep.Template.FindName("PART_ToBox", ep)!;
        Assert.True(toBox.IsEnabled);
    });

    [Fact]
    public void IsFromEnabled_false_disables_the_from_box() => Sta.Run(() =>
    {
        var ep = new AstEffectivePeriod { Template = BuildTemplate() };
        ep.ApplyTemplate();

        ep.IsFromEnabled = false;

        var fromBox = (AstDateBox)ep.Template.FindName("PART_FromBox", ep)!;
        Assert.False(fromBox.IsEnabled);
    });

    [Fact]
    public void IsUndeterminedEnabled_false_disables_the_checkbox_without_mutating_IsUndetermined() => Sta.Run(() =>
    {
        var ep = new AstEffectivePeriod { Template = BuildTemplate(), IsUndetermined = true };
        ep.ApplyTemplate();

        ep.IsUndeterminedEnabled = false;

        var check = (CheckBox)ep.Template.FindName("PART_UndeterminedCheck", ep)!;
        Assert.False(check.IsEnabled);
        // Display-only lock must NOT mutate the bound value -- regression guard for the write-back-on-lock bug
        // (ApplyUndeterminedEnabledState previously forced IsUndetermined = false here, silently corrupting
        // TwoWay-bound VM data just from locking the checkbox for display).
        Assert.True(ep.IsUndetermined);
    });

    [Fact]
    public void IsFromEnabled_true_enables_the_from_box() => Sta.Run(() =>
    {
        var ep = new AstEffectivePeriod { Template = BuildTemplate() };
        ep.ApplyTemplate();
        ep.IsFromEnabled = false;

        ep.IsFromEnabled = true;

        var fromBox = (AstDateBox)ep.Template.FindName("PART_FromBox", ep)!;
        Assert.True(fromBox.IsEnabled);
    });

    [Fact]
    public void IsUndeterminedEnabled_true_enables_the_checkbox() => Sta.Run(() =>
    {
        var ep = new AstEffectivePeriod { Template = BuildTemplate() };
        ep.ApplyTemplate();
        ep.IsUndeterminedEnabled = false;

        ep.IsUndeterminedEnabled = true;

        var check = (CheckBox)ep.Template.FindName("PART_UndeterminedCheck", ep)!;
        Assert.True(check.IsEnabled);
    });
}
