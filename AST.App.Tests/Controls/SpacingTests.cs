using System.Windows;
using System.Windows.Controls;
using AST.Controls;

namespace AST.App.Tests.Controls;

// Tier-1 headless coverage of Spacing.Between: DP default + round-trip, the pure per-child margin decision,
// and the panel Apply loop (skip the last child, orientation-aware). The pure/DP tests run on the default
// (MTA) worker thread; tests that construct a StackPanel/Border wrap their body in Sta.Run because a WPF
// FrameworkElement ctor requires STA. No rendering happens, so no Application is spun up. The visible gap
// itself (and the Loaded re-apply) is the Tier-2 requester F5 gate, not covered here.
public class SpacingTests
{
    [Fact]
    public void Between_default_is_NaN_when_unset()
    {
        var target = new DependencyObject();
        Assert.True(double.IsNaN(Spacing.GetBetween(target)));
    }

    [Fact]
    public void Between_getter_returns_what_the_setter_set()
    {
        var target = new DependencyObject();
        Spacing.SetBetween(target, 16d);
        Assert.Equal(16d, Spacing.GetBetween(target));
    }

    [Fact]
    public void ComputeChildMargin_vertical_non_last_puts_the_gap_on_the_bottom()
    {
        Assert.Equal(new Thickness(0, 0, 0, 16), Spacing.ComputeChildMargin(Orientation.Vertical, 16, isLast: false));
    }

    [Fact]
    public void ComputeChildMargin_horizontal_non_last_puts_the_gap_on_the_right()
    {
        Assert.Equal(new Thickness(0, 0, 16, 0), Spacing.ComputeChildMargin(Orientation.Horizontal, 16, isLast: false));
    }

    [Fact]
    public void ComputeChildMargin_last_child_gets_no_margin()
    {
        Assert.Equal(new Thickness(0), Spacing.ComputeChildMargin(Orientation.Vertical, 16, isLast: true));
        Assert.Equal(new Thickness(0), Spacing.ComputeChildMargin(Orientation.Horizontal, 16, isLast: true));
    }

    [Fact]
    public void Apply_gaps_every_child_but_the_last_vertically() => Sta.Run(() =>
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        var a = new Border();
        var b = new Border();
        var c = new Border();
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        Spacing.SetBetween(panel, 24d);

        Spacing.Apply(panel);

        Assert.Equal(new Thickness(0, 0, 0, 24), a.Margin);
        Assert.Equal(new Thickness(0, 0, 0, 24), b.Margin);
        Assert.Equal(new Thickness(0), c.Margin);
    });

    [Fact]
    public void Apply_gaps_horizontally_on_the_right_edge() => Sta.Run(() =>
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var a = new Border();
        var b = new Border();
        panel.Children.Add(a);
        panel.Children.Add(b);
        Spacing.SetBetween(panel, 8d);

        Spacing.Apply(panel);

        Assert.Equal(new Thickness(0, 0, 8, 0), a.Margin);
        Assert.Equal(new Thickness(0), b.Margin);
    });

    [Fact]
    public void Apply_is_a_no_op_when_Between_is_unset() => Sta.Run(() =>
    {
        var panel = new StackPanel();
        var only = new Border { Margin = new Thickness(5) };
        panel.Children.Add(only);

        Spacing.Apply(panel); // Between is NaN

        Assert.Equal(new Thickness(5), only.Margin);
    });
}
