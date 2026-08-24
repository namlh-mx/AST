using System.Windows;
using System.Windows.Controls;

namespace AST.Controls;

// Attached property that puts a uniform, token-driven directional gap between a StackPanel's children —
// WPF's StackPanel has no Spacing property (that is a WinUI feature), and WPF-UI 4.3 ships no reusable helper
// (verified, spec D4). Set it from a sys:Double spacing token, e.g.
//   <StackPanel controls:Spacing.Between="{StaticResource AstFieldGap}"> … </StackPanel>
// The gap becomes each non-last child's trailing Margin (bottom for Vertical, right for Horizontal); the last
// child gets no margin. This OWNS the children's Margin — do not also hand-set child margins on such a panel.
// Children are expected to be declared statically (our screens compose them in XAML); it re-applies on Loaded.
public static class Spacing
{
    public static readonly DependencyProperty BetweenProperty = DependencyProperty.RegisterAttached(
        "Between",
        typeof(double),
        typeof(Spacing),
        new PropertyMetadata(double.NaN, OnBetweenChanged));

    public static double GetBetween(DependencyObject element) => (double)element.GetValue(BetweenProperty);

    public static void SetBetween(DependencyObject element, double value) => element.SetValue(BetweenProperty, value);

    private static void OnBetweenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not StackPanel panel)
        {
            return;
        }

        // Idempotent re-subscribe so children added between property-set (the XAML attribute is parsed before
        // the child elements) and load are still gapped.
        panel.Loaded -= OnPanelLoaded;
        if (!double.IsNaN((double)e.NewValue))
        {
            panel.Loaded += OnPanelLoaded;
        }

        Apply(panel);
    }

    private static void OnPanelLoaded(object sender, RoutedEventArgs e) => Apply((StackPanel)sender);

    internal static void Apply(StackPanel panel)
    {
        double gap = GetBetween(panel);
        if (double.IsNaN(gap))
        {
            return;
        }

        int last = panel.Children.Count - 1;
        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is FrameworkElement child)
            {
                Thickness margin = ComputeChildMargin(panel.Orientation, gap, i == last);
                if (child.Margin != margin)
                {
                    child.Margin = margin;
                }
            }
        }
    }

    internal static Thickness ComputeChildMargin(Orientation orientation, double gap, bool isLast) =>
        isLast
            ? new Thickness(0)
            : orientation == Orientation.Vertical
                ? new Thickness(0, 0, 0, gap)
                : new Thickness(0, 0, gap, 0);
}
