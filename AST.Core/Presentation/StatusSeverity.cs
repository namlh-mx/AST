namespace AST.Core.Presentation;

// The shared severity vocabulary for command-result status (status band / dialog). Moved from AST.Shell to the
// shared kernel (2026-07-18, Task Y) so the View layer (AST.UI) and future module VMs share it without a Shell edge.
[SharedComponent]
public enum StatusSeverity { None, Info, Success, Warning, Error }
