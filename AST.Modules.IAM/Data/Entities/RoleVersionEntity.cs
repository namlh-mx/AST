using AST.Core.Data;
using AST.Core.EffectivePeriod;

namespace AST.Modules.IAM.Data.Entities;

// Direct mapping of table role_version (docs/design-iam-schema.md §1.2).
internal sealed class RoleVersionEntity : IVersionRow
{
    public long Id { get; init; }
    public long IdentityId { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly EffectiveTo { get; init; }
    public bool IsActive { get; init; }
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public bool IsAdminRole { get; init; }
    public VersionLifecycleStatus Status { get; init; }
    public VersionOperationKind? OperationKind { get; init; }
    public DateTime RecordedAt { get; init; }
    public string RecordedBy { get; init; } = string.Empty;
    public string? Reason { get; init; }
}
