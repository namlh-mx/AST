using AST.Core.Data;
using AST.Core.Iam;

namespace AST.Core.Iam.Repositories;

public sealed record RolePermissionVersionDto(
    long Id,
    long RolePermissionId,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    bool IsActive,
    long RoleId,
    long FunctionId,
    ScopeLevel ScopeLevel,
    DateTime RecordedAt,
    string RecordedBy,
    string? Reason,
    VersionLifecycleStatus Status,
    VersionOperationKind? OperationKind = null);
