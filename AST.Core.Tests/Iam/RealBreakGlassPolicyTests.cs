using AST.Core.Iam;
using ErrorOr;

namespace AST.Core.Tests.Iam;

public class RealBreakGlassPolicyTests
{
    private sealed class FakeStore(ErrorOr<IReadOnlyList<string>> result) : IBreakGlassStore
    {
        public ErrorOr<IReadOnlyList<string>> Read() => result;
        public ErrorOr<Success> Save(IReadOnlyList<string> admins, byte[]? pk, string? pp) => Result.Success;
    }

    private static RealBreakGlassPolicy WithAdmins(params string[] admins)
        => new(new FakeStore(admins.ToList()));

    [Theory]
    [InlineData(@"EXAMPLE\alice")]
    [InlineData("alice")]
    [InlineData("ALICE")]
    [InlineData("alice@example.local")]
    public void Matches_listed_admin_across_identity_forms(string login)
        => Assert.True(WithAdmins("alice").IsBreakGlassAdmin(login));

    [Fact]
    public void Rejects_unlisted_user()
        => Assert.False(WithAdmins("alice").IsBreakGlassAdmin(@"EXAMPLE\intruder"));

    [Fact]
    public void Fail_closed_when_store_errors()
    {
        var tampered = new FakeStore(Error.Validation("Config.SignatureInvalid", "x"));
        Assert.False(new RealBreakGlassPolicy(tampered).IsBreakGlassAdmin("alice"));
    }

    [Fact]
    public void Fail_closed_when_store_not_declared()
    {
        var missing = new FakeStore(Error.NotFound("Config.NotDeclared", "x"));
        Assert.False(new RealBreakGlassPolicy(missing).IsBreakGlassAdmin("alice"));
    }
}
