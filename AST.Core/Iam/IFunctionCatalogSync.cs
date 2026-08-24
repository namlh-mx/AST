using ErrorOr;

namespace AST.Core.Iam;

// [Slice C2] Syncs the `function` catalog from IFunctionRegistry (code is the source of truth).
// Semantic source of truth: docs/design-function-catalog-sync.md.
// AUTOMATIC: ADD new functions (epoch 2000-01-01) + UPDATE metadata (case 7 exact-period match, no column UPDATE).
// NOT automatic: REMOVE and RESTORE -> only lists candidates for admin confirmation (no closing/opening periods).
public interface IFunctionCatalogSync
{
    Task<ErrorOr<FunctionSyncReport>> SyncAsync();
}

// Sync result (function_key). Created/MetadataUpdated = already performed; *Candidates = informational only for the admin.
public sealed record FunctionSyncReport(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> MetadataUpdated,
    IReadOnlyList<string> RemovalCandidates,
    IReadOnlyList<string> ReopenCandidates);
