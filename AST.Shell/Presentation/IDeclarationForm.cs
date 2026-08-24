namespace AST.Shell.Presentation;

// Implemented by the ViewModel of any screen that hosts a declaration / data-entry form.
//
// Prism reuses a view AND its ViewModel across navigation, so every such screen has to warn before it drops
// unfinished work and wipe what was typed on the way out. Screens must not each re-implement that: one of
// them will forget, and forgetting is how a typed password once survived leaving a screen. The behaviour
// lives once in DeclarationFormView (WPF layer), which drives it through this contract.
public interface IDeclarationForm
{
    // True only when there is work worth warning about: the operator touched the form AND something is
    // still in it. A just-opened screen, a cleared form and a saved form all leave silently.
    bool HasUnsavedInput { get; }

    // Reset every field the operator can type into, so nothing entered — a secret above all — outlives the
    // screen. Called when navigating away.
    void Clear();
}
