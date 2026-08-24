using AST.Core.EffectivePeriod;

namespace AST.Modules.IAM.Data.Entities;

// Direct mapping of table user_version (docs/design-iam-schema.md §1.3).
// `sid` is NOT here -- it lives on the `user` header (Q2 settled), handled via UserRepository.TrySetSidOnceAsync.
internal sealed class UserVersionEntity : IVersionRow
{
    public long Id { get; init; }
    public long IdentityId { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly EffectiveTo { get; init; }
    public bool IsActive { get; init; }
    public string Username { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public long OrgUnitId { get; init; }
    public long RoleId { get; init; }
    public DateTime RecordedAt { get; init; }
    public string RecordedBy { get; init; } = string.Empty;
    public string? Reason { get; init; }
}
