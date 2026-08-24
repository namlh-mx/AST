using System;
using System.Windows.Controls;
using AST.Controls;
using AST.Core.Presentation;
using AST.Shell.Presentation;
using Prism.Navigation.Regions;
using Serilog;
using Wpf.Ui;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace AST.Views;

// Base for every screen that hosts a declaration / data-entry form: it warns before dropping unfinished
// work and wipes the form on the way out. A screen opts in by deriving from this and having its ViewModel
// implement IDeclarationForm — see that interface for why this is not left to each screen.
//
// Navigation authority is untouched (rule-module-boundary §1c): this base never navigates, it only answers
// the confirmation Prism already asks before navigating.
//
// The confirmation itself lives in ONE LeaveGate per screen instance, reachable by derived screens through
// protected ConfirmLeaveAsync(). A screen-internal leave (selecting another record, closing a sub-panel,
// changing the as-of date) must go through it too, so two triggers in one gesture share one question and
// one answer instead of stacking dialogs.
public abstract class DeclarationFormView : UserControl, IConfirmNavigationRequest
{
    private const string LeaveMessage = "Thao tác chưa hoàn tất sẽ không được lưu.";

    protected DeclarationFormView(IContentDialogService dialogs) => Dialogs = dialogs;

    // Exposed so a derived screen reuses this one instance instead of holding a second field for it.
    protected IContentDialogService Dialogs { get; }

    private IDeclarationForm? Form
    {
        get
        {
            switch (DataContext)
            {
                case IDeclarationForm form:
                    return form;
                case null:
                    return null;
                default:
                    // Deriving this view is compiler-checked; implementing the contract on the ViewModel is
                    // not. A screen that misses it would compile clean and silently lose BOTH the leave
                    // warning and the wipe — re-shipping the very defect this base exists to prevent. Fail
                    // loudly in the log rather than protect nothing, quietly.
                    Log.Error(
                        "{View}'s DataContext {ViewModel} does not implement IDeclarationForm: its form is " +
                        "NOT wiped on navigate-away and unsaved input is NOT confirmed.",
                        GetType().Name, DataContext.GetType().Name);
                    return null;
            }
        }
    }

    // One gate per screen instance. Derived screens MUST route every leave-like action through
    // ConfirmLeaveAsync instead of calling AstDialog themselves -- IDeclarationForm's locked clause says
    // "never hand-roll leave/wipe", and Screen A previously carried two extra copies of this dialog whose
    // answers could stack into two prompts for one gesture.
    private LeaveGate? _leave;

    private LeaveGate Leave => _leave ??= new LeaveGate(
        () => Form?.HasUnsavedInput == true,
        ShowLeaveDialogAsync);

    // The single sanctioned "may I drop unfinished work?" question for this screen and every part of it.
    protected Task<bool> ConfirmLeaveAsync() => Leave.ConfirmAsync();

    private async Task<bool> ShowLeaveDialogAsync()
    {
        try
        {
            return await AstDialog.ConfirmAsync(
                Dialogs, LeaveMessage, StatusSeverity.Warning,
                "Rời đi", SymbolRegular.ArrowLeft24,
                "Ở lại", SymbolRegular.Dismiss24);
        }
        catch (Exception ex)
        {
            // The navigation callback must always be answered or Prism hangs forever. Stay put: a dialog
            // failure must never be the reason unsaved edits are discarded.
            Log.Error(ex, "Failed to show the leave-confirmation dialog; staying on the screen.");
            return false;
        }
    }

    public async void ConfirmNavigationRequest(NavigationContext navigationContext, Action<bool> continuationCallback)
    {
        // No "is the form dirty?" short-circuit here: the gate owns that question, and its Form-is-null
        // case is already the `== true` in the delegate above.
        var leave = await ConfirmLeaveAsync();

        try
        {
            continuationCallback(leave);
        }
        catch (Exception ex)
        {
            // Prism runs the REST of the navigation inside this callback. We are past an await, so Prism's
            // own try/catch in RequestNavigate has unwound and an escaping exception would only be caught by
            // the app-level DispatcherUnhandledException net (App.xaml.cs) -- logged and a controlled
            // shutdown, but still an unrecoverable exit for what should be a recoverable navigation failure.
            Log.Error(ex, "Navigating away from a declaration form failed; staying on the screen.");
        }
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public virtual void OnNavigatedTo(NavigationContext navigationContext) { }

    // The region keeps this view AND its ViewModel alive, so whatever was typed would otherwise survive the
    // operator leaving. The bound controls clear through their bindings.
    //
    // Deliberately NOT virtual: a derived screen that overrode it for its own teardown and forgot the base
    // call would silently drop the wipe. Override OnLeaving instead — same reasoning as AstPasswordBox
    // pinning IsUndoEnabled as a local value: a security invariant must not be overridable by a screen.
    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
        Form?.Clear();
        OnLeaving(navigationContext);
    }

    // Hook for a derived screen's own navigate-away teardown (stop a timer, unsubscribe, ...).
    protected virtual void OnLeaving(NavigationContext navigationContext) { }
}
