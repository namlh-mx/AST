using AST.Core.Data;
using ErrorOr;

namespace AST.Core.Iam.Repositories;

public interface IRoleRepository
{
    Task<IReadOnlyList<RoleVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf);

    Task<ErrorOr<RoleVersionDto>> GetByIdentityAsync(long roleId, DateOnly asOf);

    // Full timeline — every version ever recorded (active, inactive, cancelled alike), no isactive/period
    // filter. History-grid / audit read. `roleId` null = every role identity.
    Task<IReadOnlyList<RoleVersionDto>> GetHistoryAsync(long? roleId = null);
}
