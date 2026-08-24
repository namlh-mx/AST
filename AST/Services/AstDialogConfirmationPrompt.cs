using AST.Controls;
using AST.Core.Presentation;
using AST.Shell.Presentation;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace AST.Services;

// Real IConfirmationPrompt for declaration-screen VMs (H2-style warn+confirm), built on the shared
// AstDialog builder -- same dialog shape DeclarationFormView already uses for leave-confirm. A plain
// DI service (not tied to a specific View instance) because IContentDialogService itself is a single
// app-wide singleton bound to MainWindow's RootDialogHost, not a per-screen resource.
public sealed class AstDialogConfirmationPrompt(IContentDialogService dialogs) : IConfirmationPrompt
{
    public async Task<bool> ConfirmAsync(string message, IReadOnlyList<string> details)
    {
        var body = details.Count == 0
            ? message
            : $"{message}\n\n{string.Join("\n", details)}";

        try
        {
            return await AstDialog.ConfirmAsync(
                dialogs,
                body,
                StatusSeverity.Warning,
                "Tiếp tục",
                SymbolRegular.Checkmark24,
                "Hủy",
                SymbolRegular.Dismiss24);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show the H2 collision confirmation dialog");
            return false;
        }
    }
}
