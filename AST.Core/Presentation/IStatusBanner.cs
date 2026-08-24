namespace AST.Core.Presentation;

// A ViewModel that surfaces a single command-result status band implements this. Home = AST.Core so both AST.UI
// (the AstStatusBand control) and every VM (Shell + future modules) bind one contract. A VM either exposes its
// own StatusMessage/Severity directly, or reconciles an internal aggregate onto them before exposing it (e.g.
// AdminAuthViewModel funnels its own + two child VMs' status into one aggregate).
[SharedComponent]
public interface IStatusBanner
{
    string? StatusMessage { get; }
    StatusSeverity Severity { get; }
}
