namespace AST.Core.Iam.Repositories;

public sealed record UserVersionDto(
    long Id,
    long UserId,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    bool IsActive,
    string Username,
    string DisplayName,
    long OrgUnitId,
    long RoleId,
    DateTime RecordedAt,
    string RecordedBy,
    string? Reason);
