namespace AST.Core.Data;

// Per-row action recorded on a version write (Phase 4d history-grid read, 2026-07-31):
// which user-facing action produced THIS row, distinct from the 8-case algebra outcome (which may split one
// action into several written rows -- e.g. an Edit that overlap-cuts a neighbor still writes ONE Edit row for
// the new period plus a remnant row that ALSO carries the same Edit kind, since both are consequences of the
// same action). Persisted verbatim via `ToString()`/`Enum.Parse` (no other enum-to-DB-column precedent exists
// in this codebase to follow -- VersionStatus is resolved at read time, never persisted).
public enum VersionOperationKind { Add, Edit, Close, Cancel, Replace }
