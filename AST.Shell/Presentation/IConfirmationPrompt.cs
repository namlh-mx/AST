namespace AST.Shell.Presentation;

// Lets a ViewModel ask the operator to confirm a DB-mutating action mid-command (H2: Edit/Save would
// affect other active versions of the same identity) without depending on System.Windows
// (rule-mvvm-constraints — no System.Windows in a ViewModel). The View supplies the real implementation
// (AstDialog, the same control DeclarationFormView already uses for the leave-confirmation); tests supply
// a hand-written fake.
public interface IConfirmationPrompt
{
    Task<bool> ConfirmAsync(string message, IReadOnlyList<string> details);
}
