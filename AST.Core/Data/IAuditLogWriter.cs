using System.Data;
using ErrorOr;

namespace AST.Core.Data;

// Writer for the `audit_log` table (migrations/V008__audit_log.sql — hạ tầng, KHÔNG temporal, append-only).
// Deliberately minimal: ONE capability (write one row), no filtering/query capability (that is the later
// permission-journal read model's own job) and no repository wiring here — Role grant/revoke (D-2/O2,
// the IAM declaration-screens business analysis) is its first consumer,
// added separately once that write path exists.
//
// Callable from WITHIN an ambient CompositeWrite transaction (spec §16.1) so a grant/revoke's audit row
// commits/rolls back atomically with the write it documents — see AST.Infrastructure/CompositeWrite.cs.
[SharedComponent]
public interface IAuditLogWriter
{
    // Writes ONE row on `transaction`'s own connection — never opens a second connection (rule-platform-infra:
    // no leaked write outside the ambient transaction). `transaction` is required (no nullable/default
    // overload): every known caller already runs inside a CompositeWrite transaction, and a silent
    // auto-commit fallback could desync the audit row from the write it is meant to document. A caller with no
    // ambient transaction must decide its own strategy — this contract does not invent one.
    Task<ErrorOr<Success>> WriteAsync(
        AuditLogEntry entry, IDbTransaction transaction, CancellationToken cancellationToken = default);
}

// One `audit_log` row, matching the table's actual columns (V008) — no invented fields.
//   EventType  -> event_type  (VARCHAR(50) NOT NULL): login | break-glass | signature-fail | permission-change | ...
//   Username   -> username    (VARCHAR(100) NULL): who caused the event, if known
//   Target     -> target      (VARCHAR(150) NULL): the affected object (table/identity/id)
//   DetailJson -> detail      (JSON NULL): minimal before/after payload, pre-serialized by the caller
// `occurred_at` is intentionally NOT a parameter here — like every other `recorded_at`-style column in this
// codebase (AST.Core/Time/IClock.cs, [R1]), it is left to the DB's own DEFAULT CURRENT_TIMESTAMP so all ~30
// clients share one clock source instead of skewing against each other.
public sealed record AuditLogEntry(string EventType, string? Username, string? Target, string? DetailJson);
