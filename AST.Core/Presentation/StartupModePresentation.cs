using AST.Core.Startup;

namespace AST.Core.Presentation;

// Pure, headless-testable banner mapping. The View resolves the brush KEY to a themed brush resource
// (keeps the VM/kernel layer free of any System.Windows reference). Moved from AST.Shell to the shared
// kernel (2026-07-18, Task Y) so the View layer (AST.UI) can consume it without a Shell edge.
[SharedComponent]
public static class StartupModePresentation
{
    public static string BrushKey(StartupMode mode) =>
        mode == StartupMode.Connected ? "AstConnectedBrush" : "AstNotConnectedBrush";

    public static bool IsConnected(StartupMode mode) => mode == StartupMode.Connected;
}
