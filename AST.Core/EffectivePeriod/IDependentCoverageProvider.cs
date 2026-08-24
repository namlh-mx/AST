using System.Data;

namespace AST.Core.EffectivePeriod;

// [RATIFIED — the detailing step] Added BEYOND signature §3.5, for the same reason as IParentCoverageProvider:
// ValidateParentChange needs to know the effective periods of the CHILD RECORDS depending on parentIdentityId
// via a temporal-FK edge, in order to detect "lost coverage" (reverse-FK, D8).
// Constraints on any implementation (the DB-backed one is AST.Modules.IAM/Data/DbDependentCoverageProvider.cs):
//   - The DB-reading implementation lives in the data layer, does NOT expose Entities — only returns a pure EffectivePeriod.
//   - IMPORTANT: filters by only 2 of the 3 conditions — isactive=1 AND within the child identity's period.
//     Does NOT apply the org-unit scope condition (DataScope/org-scope) — temporal-FK is a SYSTEM-WIDE
//     invariant, independent of the operator's permissions/scope. Condition (3) org-scope is dropped.
[SharedComponent]
public interface IDependentCoverageProvider
{
    // Returns the ACTIVE effective periods of the child records (table edge.ChildVersionTable) that
    // reference parentIdentityId via column edge.ChildParentColumn.
    // `ambientTransaction` is REQUIRED (no default) on purpose: a caller running inside a write
    // transaction MUST pass its transaction so this read sees that transaction's own uncommitted rows
    // (e.g. P11 auto-cut) instead of silently reading stale committed-only state. A caller outside a
    // transaction passes an explicit `null`.
    IReadOnlyList<EffectivePeriod> GetDependentPeriods(
        TemporalFkEdge edge, long parentIdentityId, IDbTransaction? ambientTransaction);
}
