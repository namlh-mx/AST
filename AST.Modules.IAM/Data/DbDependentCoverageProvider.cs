using System.Data;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using Dapper;

namespace AST.Modules.IAM.Data;

// DB-backed implementation of IDependentCoverageProvider (§3.5 AST.Core, Slice B constraint). Only filters
// isactive=1 AND falling within the child identity's period -- does NOT apply org-scope (same reason as
// DbParentCoverageProvider). Returns a plain EffectivePeriod, does NOT expose the Entity.
internal sealed class DbDependentCoverageProvider(IDbConnectionFactory connections) : IDependentCoverageProvider
{
    public IReadOnlyList<EffectivePeriod> GetDependentPeriods(
        TemporalFkEdge edge, long parentIdentityId, IDbTransaction? ambientTransaction)
    {
        var sql =
            $"SELECT effective_from AS `From`, effective_to AS `To` FROM {edge.ChildVersionTable} " +
            $"WHERE {edge.ChildParentColumn} = @id AND isactive = 1";

        if (ambientTransaction is not null)
        {
            var ambientConnection = ambientTransaction.Connection
                ?? throw new InvalidOperationException(
                    "ambientTransaction.Connection is null (the transaction was already committed/disposed) — " +
                    "GetDependentPeriods must NOT silently fall back to a fresh connection, that would re-create " +
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
