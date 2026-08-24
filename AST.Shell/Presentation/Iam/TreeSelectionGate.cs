using AST.Core.Presentation;

namespace AST.Shell.Presentation.Iam;

// Screen A's answer to "may this clicked tree node become the card?". Deliberately NOT a shared
// component (no second tree consumer -- rule-shared-components) and deliberately UI-free, so the
// policy is unit-tested: the WPF seam this replaced was F5-only and regressed repeatedly.
//
// The commit delegate is the ONLY way a TREE SELECTION reaches the card -- that single-origin property
// is what lets the WPF adapter drop its snap-back machinery. The card itself has other writers (History
// "Xem" loads a past version; the as-of rebuild and the empty-area click clear it), which is exactly why
// the short-circuit below reads the tree's committed selection rather than the card's identity.
//
// Single-threaded caller — the WPF dispatcher. Not thread-safe, and deliberately so: the check-then-act
// on the current selection and the ordering of commit are only meaningful on one thread.
public sealed class TreeSelectionGate(
    Func<long?> currentTreeSelectionId,
    Func<Task<bool>> confirmLeave,
    Func<OrgUnitTreeNode, Task> commit)
{
    /// <summary>True = the request was committed to the card.</summary>
    public async Task<bool> RequestAsync(OrgUnitTreeNode requested)
    {
        // Re-clicking the node the tree already committed is not a leave; but a card written by any
        // OTHER path (History-Xem, a cleared card) is NOT the tree's selection, so clicking that same
        // unit must still commit and reload it at the tree as-of.
        if (currentTreeSelectionId() == requested.Id)
            return false;

        if (!await confirmLeave())
            return false;

        await commit(requested);
        return true;
    }
}
