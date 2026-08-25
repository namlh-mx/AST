using AST.Core.Data;

namespace AST.Core.Iam.Repositories;

public sealed record RoleVersionDto(
    long Id,
    long RoleId,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    bool IsActive,
    string RoleCode,
    string RoleName,
    bool IsAdminRole,
    DateTime RecordedAt,
    string RecordedBy,
    string? Reason,
    VersionLifecycleStatus Status,
    VersionOperationKind? OperationKind = null);
