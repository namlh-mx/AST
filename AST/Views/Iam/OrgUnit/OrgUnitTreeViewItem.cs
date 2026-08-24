using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AST.Views.Iam.OrgUnit;

// Screen-A–local TreeViewItem. Selection has exactly one origin: OrgUnitTreeView's header gesture
// gate → TreeSelectionGate → SelectSilently. OnGotFocus never selects (D3: arrow keys move focus
// only, because TreeViewItem.AllowHandleKeyEvent returns false while the focused item is not
// selected). A RequestBringIntoView class handler marks Handled when the item is not visible so
// stock ancestor auto-expand cannot re-open a just-collapsed parent and re-enter the
// Collapsed↔restore loop that produced StackOverflowException twice before this rewrite.
// Not registered as shared — revisit if a second tree screen needs the same behaviour.
public class OrgUnitTreeViewItem : TreeViewItem
{
    private ToggleButton? _expander;

    static OrgUnitTreeViewItem()
    {
        // Stock TreeViewItem registers a class handler for RequestBringIntoView WITHOUT handledEventsToo,
        // and that handler expands every ancestor of the target (dotnet/wpf TreeViewItem.HandleBringIntoView).
        // Class handlers run most-derived first and a non-handledEventsToo handler is skipped once Handled
        // is true, so marking it here stops the auto-expand at its source. Without this, re-selecting a
        // descendant that a collapse just hid re-expands the ancestor, and undoing THAT re-raises Collapsed
        // -- the loop that produced StackOverflowException twice before this rewrite.
        EventManager.RegisterClassHandler(
            typeof(OrgUnitTreeViewItem),
            RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(OnRequestBringIntoViewFirst));
    }

    private static void OnRequestBringIntoViewFirst(object sender, RequestBringIntoViewEventArgs e)
    {
        // Only suppress the ancestor-expanding behaviour; scrolling a VISIBLE item into view is fine.
        if (sender is OrgUnitTreeViewItem item && !item.IsVisible)
            e.Handled = true;
    }

    protected override DependencyObject GetContainerForItemOverride() => new OrgUnitTreeViewItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is OrgUnitTreeViewItem;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _expander = GetTemplateChild("Expander") as ToggleButton
                    ?? FindExpanderToggle(this);
    }

    internal ToggleButton? Expander => _expander;

    protected override void OnGotFocus(RoutedEventArgs e)
    {
        // Focus must NEVER select. Stock TreeViewItem.OnGotFocus calls Select(true) unconditionally
        // (no e.Handled check), which is how a dialog's focus restore, an expander click and a collapse's
        // Focus() call all turned into "the operator navigated". Selection has exactly one origin now:
        // OrgUnitTreeView's header gesture gate -> TreeSelectionGate -> SelectSilently.
        // Consequence accepted by D3 (no keyboard tree navigation in v1): arrow keys move focus only,
        // because TreeViewItem.AllowHandleKeyEvent returns false while the focused item is not selected.
        e.Handled = true;
    }

    internal static bool IsUnder(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = OrgUnitTreeView.GetVisualOrLogicalParent(node);
        }

        return false;
    }

    private static ToggleButton? FindExpanderToggle(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ItemsPresenter)
                continue;
            if (child is ToggleButton tb)
                return tb;
            var nested = FindExpanderToggle(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
