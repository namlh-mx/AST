using System.Data;

namespace AST.Core.EffectivePeriod;

// [RATIFIED — the detailing step] Added BEYOND signature §3.5: ValidateChildCoverage/ValidateParentChange
// do not receive the parent's actual effective-period data via parameters, so the "pure" validator
// (does not touch the DB, per the Slice A requirement) needs a coverage source INJECTED in order to
// compute without reading the DB itself.
// Constraints on any implementation (the in-memory one serves unit tests; the DB-backed one is
// AST.Modules.IAM/Data/DbParentCoverageProvider.cs):
//   - The DB-reading implementation lives in the data layer (in the spirit of D13a — still goes through the
//     base repository, does not bypass the filter), does NOT expose Entities — only returns a pure EffectivePeriod.
//   - IMPORTANT: filters by only 2 of the 3 conditions — isactive=1 AND within the parent identity's period.
//     Does NOT apply the org-unit scope condition (DataScope/org-scope) — temporal-FK is a SYSTEM-WIDE
//     invariant, independent of the operator's permissions/scope. Condition (3) org-scope is dropped.
[SharedComponent]
public interface IParentCoverageProvider
{
    // Returns the currently ACTIVE effective periods of a parent identity on the parent version table.
    // `ambientTransaction` is REQUIRED (no default) on purpose: a caller running inside a write
    // transaction MUST pass its transaction so this read sees that transaction's own uncommitted rows
    // (e.g. P11 auto-cut, or an earlier enlisted write in the same CompositeWrite) instead of silently
    // reading stale committed-only state. A caller outside a transaction passes an explicit `null`.
    IReadOnlyList<EffectivePeriod> GetActiveCoverage(
        string parentVersionTable, long parentIdentityId, IDbTransaction? ambientTransaction);
}
