using AST.Core.EffectivePeriod;

namespace AST.Modules.IAM.Data.Entities;

// Direct mapping of table function_version (docs/design-iam-schema.md §1.4).
// The epoch (effective_from defaulting to 2000-01-01) is the DB column's DEFAULT, only applied when the INSERT
// statement does not pass effective_from -- the B2 repository always passes the period explicitly (does not rely on DEFAULT);
// syncing from code by the C2 epoch is the job of the service layer (Slice C), outside B2's scope.
internal sealed class FunctionVersionEntity : IVersionRow
{
    public long Id { get; init; }
    public long IdentityId { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly EffectiveTo { get; init; }
    public bool IsActive { get; init; }
    public string FunctionKey { get; init; } = string.Empty;
    public string BusinessCode { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string MenuGroup { get; init; } = string.Empty;
    public string NavTarget { get; init; } = string.Empty;
    public DateTime RecordedAt { get; init; }
    public string RecordedBy { get; init; } = string.Empty;
    public string? Reason { get; init; }
}
