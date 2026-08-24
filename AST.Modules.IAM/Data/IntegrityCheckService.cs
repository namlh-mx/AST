using System.Globalization;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using Dapper;

namespace AST.Modules.IAM.Data;

// DB-backed implementation of IIntegrityCheckService (§12 docs/design-effective-period.md, C1; the check
// catalog [R3] added in docs/design-iam-schema.md's end-of-file note). READ-ONLY -- does not fix
// data. Runs over ALL data, no org-scope (same as DbParentCoverageProvider/DbDependentCoverageProvider).
// [R5] Uses exactly `isactive` (version tables) -- does NOT touch `is_active` (app_control, outside the
// scope of the 5 IAM version tables here).
internal sealed class IntegrityCheckService(
    IDbConnectionFactory connections,
    ITemporalFkRegistry fkRegistry,
    ITemporalFkValidator fkValidator) : IIntegrityCheckService
{
    // The 5 IAM version tables (§1 docs/design-iam-schema.md) -- a static list, in the same spirit
    // as IamVersionTables (this module OWNS these 5 specific tables, no need to generalize further).
    private static readonly string[] VersionTables =
    [
        "org_unit_version", "role_version", "function_version", "user_version", "role_permission_version",
    ];

    // [R3] Natural keys that must be checked for duplicates, per table (single or composite column).
    private static readonly (string Table, string[] KeyColumns)[] NaturalKeys =
    [
        ("user_version", ["username"]),
        ("org_unit_version", ["org_code"]),
        ("role_version", ["role_code"]),
        ("function_version", ["function_key"]),
        ("role_permission_version", ["role_id", "function_id"]),
    ];

    public async Task<IReadOnlyList<IntegrityViolation>> RunAllChecksAsync()
    {
        using var connection = connections.CreateConnection();
        var violations = new List<IntegrityViolation>();

        foreach (var table in VersionTables)
        {
            violations.AddRange(await FindOverlapsAsync(connection, table));
        }

        violations.AddRange(await FindCoverageGapsAsync(connection));
        violations.AddRange(await FindOrphansAsync(connection));

        foreach (var (table, keyColumns) in NaturalKeys)
        {
            violations.AddRange(await FindDuplicateNaturalKeysAsync(connection, table, keyColumns));
        }

        violations.AddRange(await FindDuplicateAdminFlagRolesAsync(connection));

        return violations;
    }

    // Overlap (D6/§4): 2 versions with isactive=1 for the SAME identity, with intersecting periods.
    private static async Task<List<IntegrityViolation>> FindOverlapsAsync(System.Data.IDbConnection connection, string table)
    {
        var identityColumn = IamVersionTables.IdentityColumnFor(table);
        var rows = await connection.QueryAsync<(long IdentityId, long VersionIdA, long VersionIdB, DateOnly FromA, DateOnly ToA, DateOnly FromB, DateOnly ToB)>(
            $"""
            SELECT a.{identityColumn} AS IdentityId, a.id AS VersionIdA, b.id AS VersionIdB,
                   a.effective_from AS FromA, a.effective_to AS ToA, b.effective_from AS FromB, b.effective_to AS ToB
            FROM {table} a
            JOIN {table} b ON a.{identityColumn} = b.{identityColumn} AND a.id < b.id
            WHERE a.isactive = 1 AND b.isactive = 1
              AND a.effective_from <= b.effective_to AND b.effective_from <= a.effective_to
            """);

        return rows.Select(r => new IntegrityViolation(
            IntegrityViolationKind.OverlappingActivePeriods,
            table,
            r.IdentityId,
            $"phiên bản #{r.VersionIdA} [{r.FromA.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}-{r.ToA.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}] chồng lấn phiên bản #{r.VersionIdB} [{r.FromB.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}-{r.ToB.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}]"))
            .ToList();
    }

    // Temporal-FK coverage gap (D8/§5): reuses ITemporalFkValidator/ITemporalFkRegistry OVER ALL data
    // currently in the DB (not just the data just upserted) -- catches cases of data directly tampered
    // with/faulty migrations that lose coverage after having been validly saved initially.
    private async Task<List<IntegrityViolation>> FindCoverageGapsAsync(System.Data.IDbConnection connection)
    {
        var violations = new List<IntegrityViolation>();

        foreach (var table in VersionTables)
        {
            var identityColumn = IamVersionTables.IdentityColumnFor(table);

            foreach (var edge in fkRegistry.EdgesForChild(table))
            {
                var rows = await connection.QueryAsync<(long VersionId, long ChildIdentityId, long ParentId, DateOnly EffectiveFrom, DateOnly EffectiveTo)>(
                    $"""
                    SELECT id AS VersionId, {identityColumn} AS ChildIdentityId, {edge.ChildParentColumn} AS ParentId,
                           effective_from AS EffectiveFrom, effective_to AS EffectiveTo
                    FROM {table}
                    WHERE isactive = 1 AND {edge.ChildParentColumn} IS NOT NULL
                    """);

                foreach (var row in rows)
                {
                    var result = fkValidator.ValidateChildCoverage(
                        table,
                        new Dictionary<string, long> { [edge.ChildParentColumn] = row.ParentId },
                        new EffectivePeriod(row.EffectiveFrom, row.EffectiveTo),
                        null);

                    if (result.IsError)
                    {
                        var description = string.Join("; ", result.Errors.Select(e => e.Description));
                        violations.Add(new IntegrityViolation(
                            IntegrityViolationKind.ParentCoverageGap,
                            table,
                            row.ChildIdentityId,
                            $"phiên bản #{row.VersionId} (cha '{edge.ParentVersionTable}' qua cột '{edge.ChildParentColumn}'): {description}"));
                    }
                }
            }
        }

        return violations;
    }

    // Orphan: points to a parent identity that does NOT exist in the parent IDENTITY table (header) -- a normal
    // DB FK would block this, the grid still checks it to catch cases of direct data tampering (e.g. SET FOREIGN_KEY_CHECKS=0).
    private async Task<List<IntegrityViolation>> FindOrphansAsync(System.Data.IDbConnection connection)
    {
        var violations = new List<IntegrityViolation>();

        foreach (var table in VersionTables)
        {
            var identityColumn = IamVersionTables.IdentityColumnFor(table);

            foreach (var edge in fkRegistry.EdgesForChild(table))
            {
                var parentHeaderTable = edge.ParentVersionTable[..^"_version".Length];

                var rows = await connection.QueryAsync<(long VersionId, long ChildIdentityId, long ParentId)>(
                    $"""
                    SELECT v.id AS VersionId, v.{identityColumn} AS ChildIdentityId, v.{edge.ChildParentColumn} AS ParentId
                    FROM {table} v
                    LEFT JOIN `{parentHeaderTable}` h ON v.{edge.ChildParentColumn} = h.id
                    WHERE v.isactive = 1 AND v.{edge.ChildParentColumn} IS NOT NULL AND h.id IS NULL
                    """);

                violations.AddRange(rows.Select(r => new IntegrityViolation(
                    IntegrityViolationKind.OrphanedChild,
                    table,
                    r.ChildIdentityId,
                    $"phiên bản #{r.VersionId} trỏ căn cước cha #{r.ParentId} không tồn tại ở '{parentHeaderTable}' (cột '{edge.ChildParentColumn}')")));
            }
        }

        return violations;
    }

    // [R3] Duplicate natural key: 2 DIFFERENT identities, same table, active on the same day (intersecting periods),
    // sharing the same natural-key value (username/code/function_key/(role_id,function_id)).
    private static async Task<List<IntegrityViolation>> FindDuplicateNaturalKeysAsync(
        System.Data.IDbConnection connection, string table, string[] keyColumns)
    {
        var identityColumn = IamVersionTables.IdentityColumnFor(table);
        var joinPredicate = string.Join(" AND ", keyColumns.Select(c => $"a.{c} = b.{c}"));
        var keyLabel = string.Join(",", keyColumns);

        var rows = await connection.QueryAsync<(long VersionIdA, long VersionIdB, long IdA, long IdB)>(
            $"""
            SELECT a.id AS VersionIdA, b.id AS VersionIdB, a.{identityColumn} AS IdA, b.{identityColumn} AS IdB
            FROM {table} a
            JOIN {table} b ON {joinPredicate} AND a.{identityColumn} < b.{identityColumn}
            WHERE a.isactive = 1 AND b.isactive = 1
              AND a.effective_from <= b.effective_to AND b.effective_from <= a.effective_to
            """);

        return rows.Select(r => new IntegrityViolation(
            IntegrityViolationKind.DuplicateNaturalKey,
            table,
            r.IdA,
            $"căn cước #{r.IdA} (phiên bản #{r.VersionIdA}) và căn cước #{r.IdB} (phiên bản #{r.VersionIdB}) cùng dùng khóa tự nhiên trùng ({keyLabel}) trong cùng kỳ hoạt động"))
            .ToList();
    }

    // N-14: at most one role_version with is_admin_role=1 may be active on any given day
    // (overlapping active periods across DIFFERENT role identities). Reuses DuplicateNaturalKey
    // kind — IntegrityViolationKind is a SharedKernel enum outside this brief's Scope.
    private static async Task<List<IntegrityViolation>> FindDuplicateAdminFlagRolesAsync(
        System.Data.IDbConnection connection)
    {
        var rows = await connection.QueryAsync<(long VersionIdA, long VersionIdB, long IdA, long IdB)>(
            """
            SELECT a.id AS VersionIdA, b.id AS VersionIdB, a.role_id AS IdA, b.role_id AS IdB
            FROM role_version a
            JOIN role_version b ON a.role_id < b.role_id
            WHERE a.isactive = 1 AND b.isactive = 1
              AND a.is_admin_role = 1 AND b.is_admin_role = 1
              AND a.effective_from <= b.effective_to AND b.effective_from <= a.effective_to
            """);

        return rows.Select(r => new IntegrityViolation(
            IntegrityViolationKind.DuplicateNaturalKey,
            "role_version",
            r.IdA,
            $"căn cước #{r.IdA} (phiên bản #{r.VersionIdA}) và căn cước #{r.IdB} (phiên bản #{r.VersionIdB}) cùng bật is_admin_role trong cùng kỳ hoạt động (N-14)"))
            .ToList();
    }
}
