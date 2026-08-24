using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Modules.IAM.Data.Repositories;
using Dapper;
using ErrorOr;
using FluentAssertions;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// Backlog 0.4b-A — FunctionRepository.CreateAsync carries TWO invariants in one transaction, and each needs
// its own proof because either can hold while the other is broken:
//   * §7 atomicity — the identity and its first version commit or roll back together (T1).
//   * C2 "1 key = 1 identity" — decided under the catalog-create lock, concurrently (T3) and sequentially (T4).
// T2 pins the successful path, which no existing test established: FunctionCatalogSyncTests resolves through
// a FirstOrDefault, so a SECOND identity carrying one resolvable version passes it unchanged — which is
// exactly why that suite could never have caught the concurrency defect this file closes.
public sealed class FunctionRepositoryCreateTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod Epoch = new(new DateOnly(2000, 1, 1), EffectivePeriod.OpenEnd);

    private async Task<long> CountVersionsForKeyAsync(string functionKey)
    {
        using var connection = Connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM function_version WHERE function_key = @functionKey", new { functionKey });
    }

    private Task<ErrorOr<FunctionCreateOutcome>> CreateAsync(string key, EffectivePeriod period) =>
        Functions.CreateAsync(
            period, key, "FX900", "Chức năng thử", MenuGroupCodes.ConfigSecurity, $"{key}/Target",
            "system-sync", "code-sync-new");

    // =============================================================================================
    // T1 — §7 atomicity. Counting HEADER rows is the whole point: a zero-version header passes every
    // version-row assertion in this suite, so only this count can see the defect.
    // =============================================================================================

    [Fact]
    public async Task CreateAsync_WhenTheVersionWriteFails_LeavesNoHeaderRowBehind()
    {
        SkipUnlessDbAvailable();

        var headersBefore = await CountAllRowsAsync("function");

        // An inverted period is constructible -- EffectivePeriod is a plain record struct with no validation
        // -- and PeriodEditor.PlanUpsert rejects it with EffectivePeriod.InvalidRange. That rejection lands
        // AFTER the mint and BEFORE any INSERT, which is precisely the window under test: it exercises
        // CompositeWrite's returned-error rollback branch. It is NOT a chk_fv_period or MySqlException
        // rejection -- the row never reaches the database at all.
        var result = await CreateAsync("Iam.Atomicity.Fail", new EffectivePeriod(new DateOnly(2030, 1, 1), new DateOnly(2020, 1, 1)));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(
            "EffectivePeriod.InvalidRange",
            "the failure this test relies on must be the post-mint period rejection -- pin it so a different " +
            "reject (one landing BEFORE the mint) cannot make the header-count assertion vacuous");

        (await CountAllRowsAsync("function")).Should().Be(
            headersBefore,
            "the identity is minted inside the transaction that fails, so the rollback removes it -- no " +
            "compensating delete runs here, and none is relied upon");
    }

    // =============================================================================================
    // T2 — the successful path, asserted on the RETURNED value and on exact persisted cardinality.
    // =============================================================================================

    [Fact]
    public async Task CreateAsync_Success_WritesExactlyOneHeaderAndOneVersion_AndReturnsBoth()
    {
        SkipUnlessDbAvailable();

        var headersBefore = await CountAllRowsAsync("function");

        var result = await CreateAsync("Iam.Create.Ok", Epoch);

        result.IsError.Should().BeFalse();
        var created = result.Value.Should().BeOfType<FunctionCreateOutcome.Created>().Subject;
        created.FunctionId.Should().BeGreaterThan(0);

        (await CountAllRowsAsync("function")).Should().Be(headersBefore + 1, "exactly one identity is created");
        (await CountVersionsForKeyAsync("Iam.Create.Ok")).Should().Be(1, "exactly one version is written");

        var resolved = await Functions.GetByKeyAsync("Iam.Create.Ok", Today);
        resolved.IsError.Should().BeFalse("the created function must resolve by its key");
        resolved.Value.FunctionId.Should().Be(created.FunctionId, "the returned id must be the one that was written");
        // Written is non-null on every path that constructs Created, so asserting only that proves nothing.
        // Tie it to the row instead: the returned version id must be the version that actually landed.
        created.Written.NewVersionId.Should().Be(
            resolved.Value.Id, "the returned result must describe the row that was persisted");
    }

    // =============================================================================================
    // T3 — C2 under genuine contention. A task barrier would prove only that two tasks were RELEASED
    // together; pooling or scheduling could still serialise them so that the loser's unlocked re-read
    // happens after the winner has committed, leaving a missing-lock mutation green. The external
    // holder + PROCESSLIST waiter check proves both writers are actually BLOCKED on the named lock
    // before either proceeds. (Harness: IamRepositoryTestBase.HoldLockAsync /
    // WaitUntilUserLockWaiterAsync, as used by RoleDeclarationServiceTests.)
    //
    // ENVIRONMENTAL PREMISE, stated because this key is weaker than the harness's usual one:
    // the barrier counts every SERVER session waiting on the key, not the two this
    // test owns. RoleDeclarationServiceTests can lean on a per-role-code key that only its own case uses;
    // CatalogCreateLockKey is a CONSTANT, so the barrier is sound only while no process outside this suite
    // takes that lock on this server. That holds for a dedicated test database whose IAM suite is serialised
    // by AstTestDatabaseCollection, and it is why the assertion is strong rather than absolute. Binding the
    // count to the writers' own connection ids would close it, and needs a seam CreateAsync does not expose.
    // =============================================================================================

    [Fact]
    public async Task CreateAsync_TwoConcurrentCreatesOfOneKey_ProduceExactlyOneIdentity()
    {
        SkipUnlessDbAvailable();

        var ct = TestContext.Current.CancellationToken;
        var headersBefore = await CountAllRowsAsync("function");

        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(ct);

        var gate = await HoldLockAsync(FunctionRepository.CatalogCreateLockKey);
        try
        {
            var a = Task.Run(() => CreateAsync("Iam.Race.Key", Epoch), ct);
            var b = Task.Run(() => CreateAsync("Iam.Race.Key", Epoch), ct);

            await WaitUntilUserLockWaiterAsync(observer, FunctionRepository.CatalogCreateLockKey, ct, minWaiters: 2);

            await ReleaseLockAsync(gate, FunctionRepository.CatalogCreateLockKey);
            var results = await Task.WhenAll(a, b);

            results.Should().AllSatisfy(r => r.IsError.Should().BeFalse(
                "losing the race is a normal outcome, never an error -- an error would abandon the rest of the sync"));
            results.Count(r => r.Value is FunctionCreateOutcome.Created).Should().Be(
                1, "exactly one writer may create the identity");
            results.Count(r => r.Value is FunctionCreateOutcome.KeyAlreadyPresent).Should().Be(
                1, "the other must observe the key as already present and write nothing");
        }
        finally
        {
            await gate.DisposeAsync();
        }

        (await CountVersionsForKeyAsync("Iam.Race.Key")).Should().Be(
            1, "1 key = 1 identity (C2) -- two versions here is the duplicate that later surfaces as Function.DuplicateKey");
        (await CountAllRowsAsync("function")).Should().Be(
            headersBefore + 1,
            "the loser mints NOTHING: it returns before InsertIdentityAsync, so it leaves no header and " +
            "consumes no AUTO_INCREMENT value of its own");
    }

    // =============================================================================================
    // T4 — the same guard with no race at all. This is why the outcome is named for what the
    // repository can OBSERVE (the key is present) and not for why: a sequential second call is
    // indistinguishable from a lost race here, and both must refuse to mint a second identity.
    // =============================================================================================

    [Fact]
    public async Task CreateAsync_SequentialSecondCreateOfTheSameKey_ReportsKeyAlreadyPresent_AndCreatesNothing()
    {
        SkipUnlessDbAvailable();

        (await CreateAsync("Iam.Sequential.Key", Epoch)).Value
            .Should().BeOfType<FunctionCreateOutcome.Created>("the first create establishes the key");

        var headersAfterFirst = await CountAllRowsAsync("function");

        var second = await CreateAsync("Iam.Sequential.Key", Epoch);

        second.IsError.Should().BeFalse();
        second.Value.Should().BeOfType<FunctionCreateOutcome.KeyAlreadyPresent>();
        (await CountAllRowsAsync("function")).Should().Be(headersAfterFirst, "no second identity may be minted");
        (await CountVersionsForKeyAsync("Iam.Sequential.Key")).Should().Be(1, "no second version may be written");
    }
}
