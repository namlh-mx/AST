namespace AST.Core.Iam;

// Public contract (SharedKernel, rule-module-boundary item 2) for the IAM integrity-check grid (§12).
// The DB-backed implementation lives in AST.Modules.IAM (data layer) -- does NOT expose Entities, only
// returns the pure `IntegrityViolation` DTO. Used by integration tests (B3) and the future admin
// "Integrity check" screen.
public interface IIntegrityCheckService
{
    // Runs the ENTIRE check grid over ALL data (no org-scope -- D6/D8 are SYSTEM-WIDE invariants,
    // independent of the operator's scope/permissions, in the same spirit as
    // IParentCoverageProvider/IDependentCoverageProvider). Empty = clean data.
    Task<IReadOnlyList<IntegrityViolation>> RunAllChecksAsync();
}
