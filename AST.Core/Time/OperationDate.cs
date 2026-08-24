namespace AST.Core.Time;

// The ONE business date a single operation runs on (docs/design-effective-period.md §3). Captured ONCE
// at the caller via `Capture`, then threaded as a REQUIRED parameter into every write-path guard that
// needs a date. A guard must never re-derive it: `VersionedRepository._scopeToday` is the READ path's
// date and is explicitly forbidden to write-path guards (TASK 0, 2026-08-11 — see the comment on that
// field), and reading a live `IBusinessDateProvider` twice lets one operation run on two dates across
// a midnight rollover.
//
// Deliberately a distinct type rather than a bare `DateOnly`: the guards that need it already take a
// version-boundary date (`newTo`, `period.From`), and two `DateOnly` parameters side by side let a
// caller swap them silently — the compiler cannot object. As its own type the swap is a build error.
// It is also the type `ICompositeWriteContext` will expose when the operation-context task lands, so
// threading it now is not work that has to be torn out then.
[SharedComponent]
public readonly record struct OperationDate(DateOnly Value)
{
    // The single capture point. Callers hold the result for the whole operation and pass it down;
    // nothing below the caller touches `IBusinessDateProvider` on a write path.
    public static OperationDate Capture(IBusinessDateProvider dates) => new(dates.Today);

    public override string ToString() => Value.ToString("yyyy-MM-dd");
}
