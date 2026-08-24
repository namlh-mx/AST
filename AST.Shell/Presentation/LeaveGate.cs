using AST.Core;

namespace AST.Shell.Presentation;

// The single "may I drop unfinished work?" decision for a declaration screen. Every trigger -- tree
// navigate, as-of change, history View, closing a sub-panel, Prism shell navigation -- asks THIS
// object (history Show All no longer triggers it as of 2026-08-06 -- History is decoupled from
// the card/tree), so a second trigger arriving while the dialog is open awaits
// the same question and gets the same real answer. That property is why no caller needs an "already confirmed" flag:
// the previous design coordinated 7 entry points with ambient one-shot flags, which could leak on an
// early return and then silently discard edits without prompting.
//
// UI-free on purpose (rule-module-boundary: AST.Shell references no System.Windows): the View
// supplies `ask`, so this policy is unit-testable -- the seam it replaces was F5-only.
[SharedComponent]
public sealed class LeaveGate(Func<bool> hasUnsavedInput, Func<Task<bool>> ask)
{
    private Task<bool>? _pending;

    /// <summary>
    /// True = the caller may proceed (nothing unsaved, or the operator chose to leave).
    /// False = stay put; the caller must abandon its action and change nothing.
    /// A failing <c>ask</c> propagates rather than being swallowed as "stay": <see cref="LeaveGate"/> is
    /// UI-free and cannot log, so the caller's own try/catch + Log.Error is the one place the failure can be
    /// recorded. Abandoning the action there means the operator still stays put — edits are never dropped
    /// because a dialog failed.
    /// </summary>
    public async Task<bool> ConfirmAsync()
    {
        if (!hasUnsavedInput())
            return true;

        var pending = _pending ??= ask();
        try
        {
            return await pending;
        }
        finally
        {
            // Release only our own question: a later gesture must be able to ask again. This runs on the
            // faulted path too, so a dialog that failed once cannot wedge the gate shut forever.
            if (ReferenceEquals(_pending, pending))
                _pending = null;
        }
    }
}
