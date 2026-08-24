using System.Data;
using AST.Core.EffectivePeriod;
using AST.Infrastructure;
using AST.Modules.IAM.Data;
using AST.Modules.IAM.Tests.TestSupport;
using FluentAssertions;

namespace AST.Modules.IAM.Tests.Integration;

// Pins the fail-clear guard on the ambient-transaction path of the two DB coverage providers
// (commit 0e9568d). When a caller passes a non-null IDbTransaction whose Connection is already
// null (committed/disposed), the provider MUST throw InvalidOperationException rather than
// silently opening a fresh connection — that fallback would re-create the stale-read bug the
// ambientTransaction parameter exists to prevent.
//
// Chosen as a NEW file (not folded into CompositeWriteTests / CoverageReductionTests): those
// classes pin write/algebra behaviour; this pins a provider-level connection-routing guard that
// is orthogonal to either.
//
// Real MySQL, no mocking (rule-testing invariant 1).
public sealed class AmbientTransactionGuardTests : IamRepositoryTestBase
{
    [Fact]
    public void GetActiveCoverage_DisposedAmbientTransaction_ThrowsInvalidOperationException()
    {
        SkipUnlessDbAvailable();

        var connections = new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!));
        var provider = new DbParentCoverageProvider(connections);

        using var connection = connections.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        transaction.Commit();

        var act = () => provider.GetActiveCoverage("role_version", parentIdentityId: 1L, transaction);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("must NOT silently fall back")
            .And.Contain("stale-read");
    }

    [Fact]
    public void GetDependentPeriods_DisposedAmbientTransaction_ThrowsInvalidOperationException()
    {
        SkipUnlessDbAvailable();

        var connections = new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!));
        var provider = new DbDependentCoverageProvider(connections);
        var edge = IamTemporalFkEdges.All.First(e =>
            e.ChildVersionTable == "role_permission_version" && e.ChildParentColumn == "role_id");

        using var connection = connections.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        transaction.Commit();

        var act = () => provider.GetDependentPeriods(edge, parentIdentityId: 1L, transaction);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("must NOT silently fall back")
            .And.Contain("stale-read");
    }
}
