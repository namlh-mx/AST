namespace AST.Core.Startup;

// Schema-check result reduced for StartupOrchestrator (separated from ISchemaVersionChecker's own
// SchemaVersionCheckResult so the orchestrator's checkSchema delegate returns exactly what
// StartupModeResolver needs, nothing more, per spec §9 -- Shell is where CheckAsync's result is
// mapped -> this record).
public sealed record SchemaCheckOutcome(bool IsMatch, string? BlockMessage);
