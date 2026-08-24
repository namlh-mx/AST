using System.Data;
using ErrorOr;

namespace AST.Core.EffectivePeriod;

[SharedComponent]
public interface ITemporalFkValidator
{
    // STRICT D8: every parent edge of a child must CONTINUOUSLY cover the whole [childPeriod] (multiple parent
    // periods are OK as long as there is no gap). Missing/gap -> Error.Failure("TemporalFk.ParentGap",
    //   "Tham số cha '{parent}' chưa khai báo hiệu lực cho khoảng [{d1}-{d2}]").
    //
    // `ambientTransaction` is REQUIRED (no default) BY DESIGN — do not "improve" this back to an optional
    // parameter. Every call site must explicitly state whether it is running inside a write transaction, so a
    // caller writing an earlier parent version in the SAME transaction (e.g. a CompositeWrite enlisting a
    // parent + a dependent child) is guaranteed to see that parent's own uncommitted coverage instead of
    // silently reading stale committed-only state. A caller outside a transaction passes an explicit `null`.
    ErrorOr<Success> ValidateChildCoverage(
        string childVersionTable,
        IReadOnlyDictionary<string, long> parentIdentityIdsByColumn, // FK column -> parent identity
        EffectivePeriod childPeriod,
        IDbTransaction? ambientTransaction);

    // Reverse-FK: shrinking/closing/deleting the parent period causing >=1 child to lose coverage -> BLOCKED (handle children first).
    // remainingParentCoverage: the parent periods REMAINING after the change (multi-span, multi-level — may be disjoint);
    // an empty list = the parent is fully closed (any existing child -> loses coverage over the whole period if children remain).
    // -> Error.Failure("TemporalFk.DependentsUncovered", "N tham số con phụ thuộc khoảng [...]").
    //
    // `ambientTransaction` is REQUIRED (no default) BY DESIGN, same rationale as above — e.g. CloseVersionAsync's
    // P11 auto-cut soft-deactivates dependents in-transaction, then needs THIS check to see them as inactive
    // rather than reading a separate connection's stale committed snapshot. A caller outside a transaction
    // passes an explicit `null`.
    ErrorOr<Success> ValidateParentChange(
        string parentVersionTable,
        long parentIdentityId,
        IReadOnlyList<EffectivePeriod> remainingParentCoverage,
        IDbTransaction? ambientTransaction);
}
