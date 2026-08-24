namespace AST.Core.Iam.Repositories;

public sealed record FunctionVersionDto(
    long Id,
    long FunctionId,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    bool IsActive,
    string FunctionKey,
    string BusinessCode,
    string DisplayName,
    string MenuGroup,
    string NavTarget,
    DateTime RecordedAt,
    string RecordedBy,
    string? Reason);
