using System.Diagnostics;
using AST.Core.Data;
using FluentAssertions;
using AST.Infrastructure;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Time;
using AST.Core.Iam.Repositories;
using AST.Modules.IAM.Data;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using ErrorOr;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// Shared base for the B2 integration tests: each subclass isolates itself by DROPping+reapplying the entire
// `ast_test` schema (following the B1 SchemaBootstrapSmokeTests pattern), then builds all 5 IAM repos wired
// directly to a real DB (no mocking) -- matching B2's "FUNCTIONAL (single-threaded) integration test" requirement.
// No real DB available (mysql.secrets.json / env var) -> SKIP gracefully (DbAvailable = false).
[Collection(AstTestDatabaseCollection.Name)]
public abstract class IamRepositoryTestBase : IAsyncLifetime
{
    protected readonly string? ConnectionString = TestDatabase.TryGetConnectionString();
    private IDbConnectionFactory _connections = null!;

    protected bool DbAvailable { get; private set; }

    // Exposed for Integration tests that need CompositeWrite / a second factory handle without
    // re-wrapping ConnectionString (rule-prefer-existing — extend the base, don't reconstruct).
    protected IDbConnectionFactory Connections => _connections;

    // Concrete, like Roles/RolePermissions below: fixture setup seeds org-unit headers through
    // CreateIdentityAsync, which is deliberately absent from IOrgUnitRepository (production mints inside the
    // transaction instead — backlog 0.4b, 2026-08-17). Tests also reach the composite-write overloads here.
    private protected OrgUnitRepository OrgUnits { get; private set; } = null!;
    private protected RoleRepository Roles { get; private set; } = null!;
    protected IUserRepository Users { get; private set; } = null!;
    protected IFunctionRepository Functions { get; private set; } = null!;
    // Concrete, like Roles above: fixture setup seeds grant headers through CreateIdentityAsync, which is
    // deliberately absent from IRolePermissionRepository (production mints inside the transaction instead).
    private protected RolePermissionRepository RolePermissions { get; private set; } = null!;

    // B3: exposed so integrity-check-grid tests (§12) and reverse-FK/named-lock tests can run directly on the
    // already-built DB-backed components (do not rebuild them, to avoid config drift versus the repo).
    protected IIntegrityCheckService IntegrityChecks { get; private set; } = null!;
    protected ITemporalFkValidator FkValidator { get; private set; } = null!;

    // Fixed business "today" for the entire test suite (D13b: injected via IBusinessDateProvider, does not
    // read the system clock scattered around).
    protected static readonly DateOnly Today = new(2026, 7, 3);

    protected static OperationDate OperationDateForToday() => new(Today);

    public async ValueTask InitializeAsync()
    {
        if (ConnectionString is null)
        {
            // Not configured -> the tests SKIP (see SkipUnlessDbAvailable). This is the ONLY case that skips;
            // everything below means the machine IS configured, so any failure there is real and must throw.
            DbAvailable = false;
            return;
        }

        var migrationsDir = TestDatabase.RequireMigrationsDirectory();

        await using var setup = new MySqlConnection(ConnectionString);
        await setup.OpenAsync();
        await MigrationRunner.DropAllTablesAsync(setup);
        await MigrationRunner.ApplyAllAsync(setup, migrationsDir);

        DbAvailable = true;

        _connections = new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!));
        var scopeFilter = new StandardScopeFilterBuilder();
        var resolver = new EffectivePeriodResolver();
        var periodEditor = new PeriodEditor();
        var fkRegistry = IamTemporalFkEdges.CreateRegistry();
        var parentCoverage = new DbParentCoverageProvider(_connections);
        var dependentCoverage = new DbDependentCoverageProvider(_connections);
        var fkValidator = new TemporalFkValidator(fkRegistry, parentCoverage, dependentCoverage);
        var dates = new FixedBusinessDateProvider(Today);

        OrgUnits = new OrgUnitRepository(_connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates, parentCoverage);
        Roles = new RoleRepository(_connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates);
        Users = new UserRepository(_connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates);
        Functions = new FunctionRepository(_connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates);
        RolePermissions = new RolePermissionRepository(_connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates);

        IntegrityChecks = new IntegrityCheckService(_connections, fkRegistry, fkValidator);
        FkValidator = fkValidator;

        _scopeFilter = scopeFilter;
        _resolver = resolver;
        _periodEditor = periodEditor;
        _fkRegistry = fkRegistry;
    }

    private IStandardScopeFilterBuilder _scopeFilter = null!;
    private IEffectivePeriodResolver _resolver = null!;
    private IPeriodEditor _periodEditor = null!;
    private ITemporalFkRegistry _fkRegistry = null!;

    // Builds a SECOND instance of a repository whose injected IBusinessDateProvider says a DIFFERENT day
    // than the suite's `Today`, sharing the same connection factory and DB. Used to prove that a write-path
    // guard reads the OPERATION's captured date (passed as an `OperationDate` argument) and never the
    // repository's own clock — a distinction that is invisible while both agree, and is exactly what goes
    // wrong when one operation straddles midnight (docs/design-effective-period.md §3).
    // Deliberately two FIXED providers rather than AdvancingBusinessDateProvider: an advancing provider
    // advances on the first read by ANY consumer sharing the instance, which makes WHICH read sees the new
    // day depend on call order. Two fixed clocks reproduce the straddle deterministically.
    private protected RoleRepository BuildRoleRepositoryWithClock(DateOnly clockToday) =>
        new(Connections, _scopeFilter, _resolver, _periodEditor, FkValidator, _fkRegistry,
            new FixedBusinessDateProvider(clockToday));

    private protected RolePermissionRepository BuildRolePermissionRepositoryWithClock(DateOnly clockToday) =>
        new(Connections, _scopeFilter, _resolver, _periodEditor, FkValidator, _fkRegistry,
            new FixedBusinessDateProvider(clockToday));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected void SkipUnlessDbAvailable() => TestDatabase.SkipUnlessAvailable(DbAvailable);

    // Creates an empty identity (just an AUTO_INCREMENT id) and returns its id -- the 5 IAM header tables only have an identity column.
    protected async Task<long> InsertHeaderAsync(string table)
    {
        using var connection = _connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>($"INSERT INTO `{table}` () VALUES (); SELECT LAST_INSERT_ID();");
    }

    protected async Task<long> CreateOrgUnitAsync(string orgCode, string orgNameFullVn, string orgNameShortVn, long? parentId, EffectivePeriod period)
    {
        var id = await OrgUnits.CreateIdentityAsync();
        var result = await OrgUnits.UpsertAsync(
            id, period, orgCode, orgNameFullVn, orgNameShortVn, parentId, VersionOperationKind.Add, "tester", "seed");
        Assert.False(result.IsError, DescribeErrors(result.Errors));
        return id;
    }

    // role is Immediate (docs/design-temporality-classes.md §5, 2026-08-12): once RoleRepository.UpsertAsync
    // enforces R1 (effective_from must equal today), it can no longer manufacture a version starting in the
    // past or future — only the database can. Seeds role + role_version directly via raw SQL (bypassing
    // RoleRepository.UpsertAsync entirely, so no P6/admin-flag gates apply here — this is fixture setup, not
    // a claim about what a real caller may do).
    protected async Task<long> CreateRoleAsync(string roleCode, string roleName, EffectivePeriod period, bool isAdminRole = false)
    {
        var id = await Roles.CreateIdentityAsync();
        using var connection = _connections.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO role_version
                (role_id, role_code, role_name, is_admin_role, operation_kind, effective_from, effective_to,
                 isactive, recorded_by, reason)
            VALUES
                (@id, @roleCode, @roleName, @isAdminRole, 'Add', @from, @to, 1, 'tester', 'seed')
            """,
            new { id, roleCode, roleName, isAdminRole, from = period.From, to = period.To });
        return id;
    }

    // role_permission is Immediate too (same register row) — same rationale as CreateRoleAsync above.
    // Seeds role_permission + role_permission_version directly via raw SQL, bypassing
    // RolePermissionRepository.UpsertAsync (no overlap/temporal-FK gates here — fixture setup only).
    protected async Task<long> CreateGrantAsync(
        long roleId, long functionId, EffectivePeriod period, ScopeLevel scopeLevel, string? reason = "seed")
    {
        var id = await RolePermissions.CreateIdentityAsync();
        using var connection = _connections.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO role_permission_version
                (role_permission_id, role_id, function_id, scope_level, operation_kind, effective_from,
                 effective_to, isactive, recorded_by, reason)
            VALUES
                (@id, @roleId, @functionId, @scopeLevel, 'Add', @from, @to, 1, 'tester', @reason)
            """,
            new
            {
                id, roleId, functionId, scopeLevel = (int)scopeLevel, from = period.From, to = period.To, reason,
            });
        return id;
    }

    // Immediate writers cannot persist historical/future periods via UpsertAsync. Tests that need a
    // second role_version (future plan, disjoint later slice) insert it here, same raw-SQL path as
    // CreateRoleAsync. Returns the new version id.
    protected async Task<long> InsertRoleVersionAsync(
        long roleId, string roleCode, string roleName, EffectivePeriod period, bool isAdminRole = false,
        string operationKind = "Edit")
    {
        using var connection = _connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO role_version
                (role_id, role_code, role_name, is_admin_role, operation_kind, effective_from, effective_to,
                 isactive, recorded_by, reason)
            VALUES
                (@roleId, @roleCode, @roleName, @isAdminRole, @operationKind, @from, @to, 1, 'tester', 'seed');
            SELECT LAST_INSERT_ID();
            """,
            new { roleId, roleCode, roleName, isAdminRole, operationKind, from = period.From, to = period.To });
    }

    protected async Task<long> CreateFunctionAsync(string functionKey, EffectivePeriod period)
    {
        var id = await InsertHeaderAsync("function");
        var result = await Functions.UpsertAsync(
            id, period, functionKey, "FX000", "Chức năng test", MenuGroupCodes.ConfigSecurity, "Test/Target",
            "tester", "seed");
        Assert.False(result.IsError, DescribeErrors(result.Errors));
        return id;
    }

    protected async Task<long> CreateUserHeaderAsync() => await InsertHeaderAsync("user");

    protected async Task<long> CreateRolePermissionHeaderAsync() => await RolePermissions.CreateIdentityAsync();

    protected static string DescribeErrors(IReadOnlyList<ErrorOr.Error> errors) =>
        string.Join("; ", errors.Select(e => $"{e.Code}: {e.Description}"));

    // Delegates to VersionedRepositoryLockKeys.Format (InternalsVisibleTo) so the test-harness key
    // cannot drift from CompositeWrite / VersionedRepository.
    protected static string LockKey(string versionTable, long identityId) =>
        VersionedRepositoryLockKeys.Format(versionTable, identityId);

    // GET_LOCK is connection-scoped. Pooling=false keeps the harness connection from being returned
    // to the pool while it still holds the named lock (and keeps PROCESSLIST INFO stable for the
    // observed-waiter barrier below).
    protected string UnpooledConnectionString
    {
        get
        {
            var builder = new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false };
            return builder.ConnectionString;
        }
    }

    protected async Task<MySqlConnection> HoldLockAsync(string key)
    {
        var connection = new MySqlConnection(UnpooledConnectionString);
        await connection.OpenAsync();
        var got = await connection.ExecuteScalarAsync<long?>("SELECT GET_LOCK(@name, 10)", new { name = key });
        got.Should().Be(1, $"the test harness must be able to hold '{key}' before the writer starts");
        return connection;
    }

    protected static async Task ReleaseLockAsync(MySqlConnection connection, string key) =>
        await connection.ExecuteAsync("SELECT RELEASE_LOCK(@name)", new { name = key });

    // Strong PROCESSLIST predicate (FR2 probe: INFO carries the inlined GET_LOCK key). INSTR has
    // no LIKE wildcards; quoted GET_LOCK('key', closes numeric prefix collisions.
    // Constraint: this suite is the only actor holding asttest locks on this key, which holds
    // because IAM integration tests are serialized by AstTestDatabaseCollection.
    protected static async Task WaitUntilUserLockWaiterAsync(
        MySqlConnection observer, string key, CancellationToken cancellationToken, int minWaiters = 1)
    {
        const int budgetMs = 5_000;
        const int pollMs = 50;
        var clock = Stopwatch.StartNew();
        long count = 0;
        while (clock.ElapsedMilliseconds < budgetMs)
        {
            count = await observer.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM information_schema.PROCESSLIST
                WHERE STATE = 'User lock'
                  AND INSTR(INFO, CONCAT('GET_LOCK(''', @key, ''',')) > 0
                """,
                new { key });
            if (count >= minWaiters)
            {
                break;
            }

            await Task.Delay(pollMs, cancellationToken);
        }

        count.Should().BeGreaterThanOrEqualTo(minWaiters,
            "PROCESSLIST barrier: expected at least {0} STATE='User lock' waiter(s) whose INFO contains GET_LOCK('{1}', ...) within {2}ms (well under the writer's 10s GET_LOCK budget); saw {3}",
            minWaiters, key, budgetMs, count);
    }

    protected async Task<long> CountAllRowsAsync(string table)
    {
        using var connection = _connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM `{table}`");
    }
}
