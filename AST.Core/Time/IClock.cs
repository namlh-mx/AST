namespace AST.Core.Time;

// [R1 settled 2026-07-03] IClock is ONLY for technical logging / app-side timestamps.
// `recorded_at` on version tables is set by the DB (DEFAULT CURRENT_TIMESTAMP) — a single clock
// source, avoiding skew across 30 clients and keeping polling deltas by recorded_at (⑧.3) consistent.
[SharedComponent]
public interface IClock
{
    DateTime UtcNow { get; }
}
