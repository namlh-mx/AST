using System.Data;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using Dapper;

namespace AST.Modules.IAM.Data;

// DB-backed implementation of IParentCoverageProvider (§3.5 AST.Core, Slice B constraint). Only filters isactive=1
// AND falling within the parent identity's period -- does NOT apply org-scope (temporal-FK is a SYSTEM-WIDE invariant,
// the contract flags this clearly). Returns a plain EffectivePeriod, does NOT expose the Entity.
internal sealed class DbParentCoverageProvider(IDbConnectionFactory connections) : IParentCoverageProvider
{
    public IReadOnlyList<EffectivePeriod> GetActiveCoverage(
        string parentVersionTable, long parentIdentityId, IDbTransaction? ambientTransaction)
    {
        var identityColumn = IamVersionTables.IdentityColumnFor(parentVersionTable);
        var sql =
            $"SELECT effective_from AS `From`, effective_to AS `To` FROM {parentVersionTable} " +
            $"WHERE {identityColumn} = @id AND isactive = 1";

        if (ambientTransaction is not null)
        {
            var ambientConnection = ambientTransaction.Connection
                ?? throw new InvalidOperationException(
                    "ambientTransaction.Connection is null (the transaction was already committed/disposed) — " +
                    "GetActiveCoverage must NOT silently fall back to a fresh connection, that would re-create " +
                    "the exact stale-read bug this parameter exists to prevent.");

            // `From`/`To` collide with a MySQL reserved keyword (`FROM`) -> backticks are REQUIRED when used as an alias.
            var ambientRows = ambientConnection.Query<(DateOnly From, DateOnly To)>(
                sql, new { id = parentIdentityId }, ambientTransaction);
            return ambientRows.Select(r => new EffectivePeriod(r.From, r.To)).ToList();
        }

        using var connection = connections.CreateConnection();
        var rows = connection.Query<(DateOnly From, DateOnly To)>(sql, new { id = parentIdentityId });
        return rows.Select(r => new EffectivePeriod(r.From, r.To)).ToList();
    }
}
