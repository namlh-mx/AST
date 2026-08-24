using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AST.Core.Presentation;

namespace AST.Views.Iam.OrgUnit;

// Screen-A–local TreeView. Selection has exactly one origin: the header gesture gate raises
// SelectionRequested → View's TreeSelectionGate → SelectSilently. OnGotFocus no longer selects, so
// there is nothing illegitimate to snap back — remembered "true selection", focus-suppression, and
// DispatcherPriority hops are gone. The one irreducible undo remains: TreeView.HandleSelectionAndCollapsed
// promotes selection onto a collapsing ancestor through an internal ChangeSelection no public hook can
// veto (spec §2.7.5: collapse keeps selection); OnSelectedItemChanged reads e.OldValue and restores.
// Not registered as shared — revisit if a second tree screen needs the same behaviour.
public class OrgUnitTreeView : TreeView
{
    private bool _isRestoringSelection;

    /// <summary>Raised for a real header click. The View answers it through TreeSelectionGate.</summary>
    internal event Func<OrgUnitTreeNode, Task>? SelectionRequested;

    protected override DependencyObject GetContainerForItemOverride() => new OrgUnitTreeViewItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is OrgUnitTreeViewItem;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var container = ContainerUnderMouse(e.OriginalSource as DependencyObject);
        if (container is null)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }

        // The expander is chrome, not navigation (spec 2.7.5): let the stock ToggleButton toggle
        // IsExpanded. It can no longer select the row, because OnGotFocus no longer selects.
        if (container.Expander is { } expander && e.OriginalSource is DependencyObject source
            && OrgUnitTreeViewItem.IsUnder(expander, source))
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }

        // Header click. Handle it here so nothing visual happens before the operator answers the
        // leave-confirm: stock TreeViewItem.OnMouseLeftButtonDown selects (and double-click expands)
        // synchronously, before any async handler could ask. Everything inside its body is gated on
        // !e.Handled, so marking it handled suppresses BOTH -- we therefore own the double-click too.
        e.Handled = true;

        if (e.ClickCount % 2 == 0)
        {
            // 2.7.5: double-click toggles the subtree. F5 (2026-08-04, Fix Round 1 F6): while a dirty
            // leave-confirm ContentDialog is already open from Down#1, Down#2 does not reach here — the
            // dialog swallows it — so the subtree does not toggle behind the question. Do not add a second
            // leave gate on this branch unless that observation regresses. After "Rời đi", only the
            // Down#1 SelectionRequested commit runs (select/load); expand is not replayed — settled
            // 2026-08-04 (requester): dirty double-click + leave = select only, not full clean
            // double-click semantics.
            container.IsExpanded = !container.IsExpanded;
            return;
        }

        if (ItemsControlFromItemContainer(container)?.ItemContainerGenerator.ItemFromContainer(container)
            is OrgUnitTreeNode node && SelectionRequested is { } request)
        {
            // async void by nature of an input event: the handler logs its own failures.
            _ = request(node);
        }
    }

    protected override void OnSelectedItemChanged(RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isRestoringSelection)
            return;   // silent: the View must not read our own restore as the operator navigating

        if (IsCollapsePromotion(e.OldValue, e.NewValue, out var keep))
        {
            // The ONE thing the framework leaves no alternative to: TreeView.HandleSelectionAndCollapsed
            // deliberately moves selection onto a collapsing ancestor, through an internal ChangeSelection
            // call that no public hook can veto. Spec 2.7.5 says collapse keeps the selection, so undo it.
            // Safe to re-select from inside this event: ChangeSelection clears IsSelectionChangeActive in
            // its finally BEFORE raising us. No BringIntoView loop can form -- see the class handler on
            // OrgUnitTreeViewItem.
            _isRestoringSelection = true;
            try { keep.IsSelected = true; }
            finally { _isRestoringSelection = false; }
            return;
        }

        base.OnSelectedItemChanged(e);
    }

    // No remembered "true selection" is needed: ChangeSelection captures oldValue = SelectedItem BEFORE
    // reassigning and hands it to us as e.OldValue. The fields that used to cache it are exactly why a
    // node rejected at a leave-confirm could later be resurrected by a collapse.
    private bool IsCollapsePromotion(object? oldValue, object? newValue, out TreeViewItem keep)
    {
        keep = null!;
        if (oldValue is not OrgUnitTreeNode || newValue is not OrgUnitTreeNode)
            return false;
        if (FindContainer(this, newValue) is not { IsExpanded: false } promoted)
            return false;   // a collapse promotion always lands on a COLLAPSED ancestor
        if (FindContainer(this, oldValue) is not { } previous || !IsDescendantOf(previous, promoted))
            return false;

        keep = previous;
        return true;
    }

    /// <summary>
    /// Move the highlight without raising SelectedItemChanged for the View. The ONLY sanctioned way to
    /// set the tree's visual selection: it always follows a committed card load, never precedes it.
    /// </summary>
    internal void SelectSilently(OrgUnitTreeNode? node)
    {
        _isRestoringSelection = true;
        try
        {
            if (node is null)
            {
                if (SelectedItem is not null && FindContainer(this, SelectedItem) is { } current)
                    current.IsSelected = false;
                return;
            }

            if (FindContainer(this, node) is { } container)
                container.IsSelected = true;
        }
        finally { _isRestoringSelection = false; }
    }

    private static OrgUnitTreeViewItem? ContainerUnderMouse(DependencyObject? source)
    {
        for (var node = source; node is not null; node = GetVisualOrLogicalParent(node))
            if (node is OrgUnitTreeViewItem item)
                return item;
        return null;
    }

    private static bool IsDescendantOf(TreeViewItem node, TreeViewItem ancestor)
    {
        for (var p = ItemsControlFromItemContainer(node) as TreeViewItem;
             p is not null;
             p = ItemsControlFromItemContainer(p) as TreeViewItem)
        {
            if (ReferenceEquals(p, ancestor))
                return true;
        }

        return false;
    }

    public static TreeViewItem? FindContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
            return direct;

        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem childContainer)
                continue;
            var found = FindContainer(childContainer, item);
            if (found is not null)
                return found;
        }

        return null;
    }

    internal static DependencyObject? GetVisualOrLogicalParent(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
