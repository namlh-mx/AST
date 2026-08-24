using System.Data;
using AST.Core.Data;
using Dapper;
using ErrorOr;

namespace AST.Infrastructure;

// ============================================================================================
// Composite write — multi-identity, one transaction (spec §16.1 capability 1).
//
// Behaviour pinned by AST.Modules.IAM.Tests/Integration/CompositeWriteTests.cs:
//   * ONE connection, ONE transaction, READ COMMITTED, for every write in the composite;
//   * EVERY required GET_LOCK key acquired UP FRONT, in the existing fixed order
//     (ordinal by version-table name, then by identity id) BEFORE the first write runs — so a
//     composite write can never deadlock against a concurrent single-identity write;
//   * any failure (returned Error or thrown exception) rolls the whole thing back — nothing
//     partially persists.
//
// Callers MUST Enlist every PRE-EXISTING identity the work delegate will write, INCLUDING temporal-FK
// parent identities those writes will touch (same set BuildLockKeys would take for a single-identity
// upsert). A context upsert against a non-enlisted identity fails clearly — late lock acquisition
// is forbidden (clear failure over silent ambiguity).
//
// The ONE exception is an identity minted inside this transaction through the base seam
// VersionedRepository.InsertIdentityAsync(ICompositeWriteContext) (design-effective-period.md §7
// carve-out): it cannot be Enlist-ed because it does not exist when locks are taken, and it needs no
// lock of its own because no other session can name it until commit. It is NOT an exception to the two
// conditions that bound the carve-out: pre-existing identities still lock up front, and no GET_LOCK is
// ever taken after the transaction opens.
// ============================================================================================

// A repository that can take part in a composite write. Implemented by VersionedRepository<T> so a
// caller can Enlist a repository without knowing its version-table name (which stays protected).
public interface IVersionedWriteTarget
{
    // The version table this repository writes — the `{table}` half of the §7 lock key
    // `astep:{table}:{identityId}`.
    string VersionTableName { get; }
}

// Handle passed to the composite work delegate: the single shared connection + transaction that
// every enlisted repository must write through.
public interface ICompositeWriteContext
{
    IDbConnection Connection { get; }

    IDbTransaction Transaction { get; }

    // True when (versionTable, identityId) was in the enlistment SNAPSHOT taken as this transaction opened
    // — which is the set whose GET_LOCK keys were actually acquired. Enlisting later does not register.
    bool IsEnlisted(string versionTable, long identityId);
}

// The created-here registry — BOTH halves internal, and deliberately NOT on the public context interface
// above. The enlistment check treats "created here" as permission to write with no named lock, so it must
// rest on evidence this assembly produced: only VersionedRepository.InsertIdentityAsync(
// ICompositeWriteContext), which mints on the composite's own connection and transaction, writes a mark.
// Were the READ half public too, a foreign ICompositeWriteContext could answer `true` for a PRE-EXISTING
// identity and buy itself an unlocked write — the §7 violation this carve-out exists inside, not around.
//
// Keyed by the (table, id) PAIR, never by the id alone: a role identity minted here and a PRE-EXISTING
// function identity can carry the same number, and an id-only registry would let the new role's id vouch
// for the function's missing lock.
internal interface ICompositeCreatedIdentityRegistry
{
    void MarkCreated(string versionTable, long identityId);

    bool IsCreatedHere(string versionTable, long identityId);
}

// Unit of work spanning 2+ identities across 1+ VersionedRepository subclasses (spec §16.1).
public sealed class CompositeWrite(IDbConnectionFactory connections)
{
    private readonly List<(string Table, long IdentityId)> _enlisted = [];

    // Seam-2 gap F2: extra named-lock keys NOT tied to one (versionTable, identityId) — e.g. a future
    // business singleton (an admin-flag-role singleton, an active-grant-per-role+function singleton).
    // Contributed via the Enlist(string) overload below and acquired in the SAME up-front, fixed-order
    // §7 batch as the identity-scoped keys — never a separate connection, never acquired after the batch.
    private readonly List<string> _extraLockKeys = [];

    // The factory the single shared connection is opened from (see the class comment).
    internal IDbConnectionFactory Connections { get; } = connections;

    // Every identity the composite will write — INCLUDING the temporal-FK parents of those writes,
    // because their lock keys must also be part of the up-front, fixed-order acquisition.
    internal IReadOnlyList<(string Table, long IdentityId)> Enlisted => _enlisted;

    public CompositeWrite Enlist(IVersionedWriteTarget target, long identityId)
    {
        var key = (target.VersionTableName, identityId);
        if (!_enlisted.Contains(key))
        {
            _enlisted.Add(key);
        }

        return this;
    }

    // F2: lets a caller/repository contribute an arbitrary extra named-lock key (not identity-scoped) to
    // the SAME up-front, fixed-order §7 batch this composite already acquires — e.g. a business singleton
    // lock that isn't any one (versionTable, identityId). The caller owns the key's naming/uniqueness
    // (this class does not prefix or reinterpret it); pass the same fully-formed key every time the same
    // logical lock is meant, exactly like VersionedRepositoryLockKeys.Format does for identity-scoped keys.
    public CompositeWrite Enlist(string lockKey)
    {
        if (!_extraLockKeys.Contains(lockKey))
        {
            _extraLockKeys.Add(lockKey);
        }

        return this;
    }

    public async Task<ErrorOr<Success>> ExecuteAsync(Func<ICompositeWriteContext, Task<ErrorOr<Success>>> work)
    {
        using var connection = Connections.CreateConnection();
        connection.Open();

        // §7 fixed order: ordinal by version-table name, then by identity id (same as
        // VersionedRepository.BuildLockKeys) — preserved exactly for the identity-scoped keys by sorting
        // the (Table, IdentityId) tuples BEFORE formatting (numeric id order, not a lexicographic sort of
        // the formatted string, which would misorder e.g. "...:9" after "...:10"). Extra raw keys (F2) are
        // woven into the SAME total order as their own pseudo-table bucket (SortId = 0) so the combined
        // list stays one single deterministic order without disturbing the identity-key invariant above.
        // Enlisted already unions write targets + parents the caller declared; we do not invent late keys
        // after work starts.
        var lockKeys = _enlisted
            .Select(k => (SortTable: k.Table, SortId: k.IdentityId, Key: VersionedRepositoryLockKeys.Format(k.Table, k.IdentityId)))
            .Concat(_extraLockKeys.Select(k => (SortTable: k, SortId: 0L, Key: k)))
            .OrderBy(e => e.SortTable, StringComparer.Ordinal)
            .ThenBy(e => e.SortId)
            .Select(e => e.Key)
            .ToList();

        var acquired = new List<string>();
        try
        {
            foreach (var key in lockKeys)
            {
                var got = await connection.ExecuteScalarAsync<long?>("SELECT GET_LOCK(@name, 10)", new { name = key });
                if (got != 1)
                {
                    return Error.Failure(
                        "VersionedRepository.LockTimeout",
                        $"Không lấy được khóa ghi cho '{key}' (timeout hoặc lỗi).");
                }

                acquired.Add(key);
            }

            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

            // A SNAPSHOT of the enlistment, taken as the transaction opens — never the live list.
            // The delegate closes over this CompositeWrite in every real caller, so
            // handing it the live list would let an Enlist() made INSIDE the delegate answer IsEnlisted for
            // a key whose GET_LOCK was never taken — a lock-free write of a pre-existing identity, wearing
            // the enlistment check's own approval. Enlisting late now simply does not register.
            var context = new CompositeWriteContext(connection, transaction, _enlisted.ToList());
            try
            {
                var result = await work(context);
                if (result.IsError)
                {
                    transaction.Rollback();
                    return result;
                }

                transaction.Commit();
                return result;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            for (var i = acquired.Count - 1; i >= 0; i--)
            {
                await connection.ExecuteAsync("SELECT RELEASE_LOCK(@name)", new { name = acquired[i] });
            }
        }
    }

    private sealed class CompositeWriteContext(
        IDbConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<(string Table, long IdentityId)> enlisted) : ICompositeWriteContext, ICompositeCreatedIdentityRegistry
    {
        // Identities minted inside this transaction, keyed by (version table, id) — see IsCreatedHere.
        private readonly HashSet<(string Table, long IdentityId)> _createdHere = [];

        public IDbConnection Connection { get; } = connection;

        public IDbTransaction Transaction { get; } = transaction;

        public bool IsEnlisted(string versionTable, long identityId) =>
            enlisted.Contains((versionTable, identityId));

        public bool IsCreatedHere(string versionTable, long identityId) =>
            _createdHere.Contains((versionTable, identityId));

        public void MarkCreated(string versionTable, long identityId) =>
            _createdHere.Add((versionTable, identityId));
    }
}

// Shared §7 lock-key formatting so CompositeWrite and VersionedRepository never drift.
internal static class VersionedRepositoryLockKeys
{
    internal static string Format(string versionTable, long identityId) =>
        $"astep:{versionTable}:{identityId}";
}
