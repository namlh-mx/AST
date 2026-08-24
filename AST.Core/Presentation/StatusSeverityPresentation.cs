namespace AST.Core.Presentation;

// Pure, headless-testable status mapping. The View resolves the brush KEY to a themed brush resource
// (keeps the VM/kernel layer free of any System.Windows reference). Moved from AST.Shell to the shared
// kernel (2026-07-18, Task Y) so the View layer (AST.UI) can consume it without a Shell edge.
[SharedComponent]
public static class StatusSeverityPresentation
{
    // Convention (requester 2026-07-13): success = green, any other message = red; no message = secondary.
    public static string BrushKey(StatusSeverity severity) => severity switch
    {
        StatusSeverity.None => "AstTextSecondaryBrush",
        StatusSeverity.Success => "AstSuccessBrush",
        _ => "AstErrorBrush",
    };
}
