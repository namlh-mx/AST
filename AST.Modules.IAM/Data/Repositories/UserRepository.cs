using System.Globalization;
using AST.Core.Data;
using AST.Infrastructure;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Time;
using AST.Modules.IAM.Data.Entities;
using Dapper;
using ErrorOr;

namespace AST.Modules.IAM.Data.Repositories;

internal sealed class UserRepository(
    IDbConnectionFactory connections,
    IStandardScopeFilterBuilder scopeFilter,
    IEffectivePeriodResolver resolver,
    IPeriodEditor periodEditor,
    ITemporalFkValidator fkValidator,
    ITemporalFkRegistry fkRegistry,
    IBusinessDateProvider dates)
    : VersionedRepository<UserVersionEntity>(connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates),
        IUserRepository
{
    protected override string VersionTable => "user_version";
    protected override string IdentityColumn => "user_id";

    protected override string OrgUnitColumn => $"{Alias}.org_unit_id";
    protected override string OwnerColumn => $"{Alias}.username";

    protected override IReadOnlyList<(string Column, string Property)> BusinessColumns =>
    [
        ("username", nameof(UserVersionEntity.Username)),
        ("display_name", nameof(UserVersionEntity.DisplayName)),
        ("org_unit_id", nameof(UserVersionEntity.OrgUnitId)),
        ("role_id", nameof(UserVersionEntity.RoleId)),
    ];

    protected override IReadOnlyDictionary<string, long> ExtractParentIdentityIds(UserVersionEntity newValues) =>
        new Dictionary<string, long>
        {
            ["org_unit_id"] = newValues.OrgUnitId,
            ["role_id"] = newValues.RoleId,
        };

    public async Task<IReadOnlyList<UserVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf)
    {
        var rows = await QueryInScopeAsync(scope, asOf);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ErrorOr<UserVersionDto>> GetByIdentityAsync(long userId, DateOnly asOf)
    {
        var result = await ResolveAtAsync(userId, asOf);
        if (result.IsError)
        {
            return result.Errors;
        }

        return ToDto(result.Value);
    }

    public async Task<ErrorOr<UpsertResult>> UpsertAsync(
        long userId, EffectivePeriod period, string username, string displayName,
        long orgUnitId, long roleId, string recordedBy, string? reason)
    {
        var newValues = new UserVersionEntity
        {
            IdentityId = userId,
            EffectiveFrom = period.From,
            EffectiveTo = period.To,
            IsActive = true,
            Username = username,
            DisplayName = displayName,
            OrgUnitId = orgUnitId,
            RoleId = roleId,
            RecordedBy = recordedBy,
            Reason = reason,
        };

        return await UpsertVersionAsync(userId, period, newValues, recordedBy, reason);
    }

    public async Task<ErrorOr<UserVersionDto>> GetByUsernameAsync(string username, DateOnly asOf)
    {
        var rows = await QueryApplicableAsync($"{Alias}.username = @username", new { username }, asOf);
        if (rows.Count == 0)
        {
            return Error.NotFound(
                "User.NotFoundByUsername",
                $"Không có user hiệu lực tại {asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} với username '{username}'.");
        }

        if (rows.Count > 1)
        {
            return Error.Conflict(
                "User.DuplicateUsername",
                $"Có {rows.Count} user active trùng username '{username}' tại {asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} — vi phạm bất biến username-duy-nhất (§1.3).");
        }

        return ToDto(rows[0]);
    }

    public async Task<bool> TrySetSidOnceAsync(long userId, string sid)
    {
        using var connection = Connections.CreateConnection();
        var affected = await connection.ExecuteAsync(
            "UPDATE `user` SET sid = @sid WHERE id = @userId AND sid IS NULL",
            new { sid, userId });
        return affected == 1;
    }

    private static UserVersionDto ToDto(UserVersionEntity e) => new(
        e.Id, e.IdentityId, e.EffectiveFrom, e.EffectiveTo, e.IsActive,
        e.Username, e.DisplayName, e.OrgUnitId, e.RoleId,
        e.RecordedAt, e.RecordedBy, e.Reason);
}
