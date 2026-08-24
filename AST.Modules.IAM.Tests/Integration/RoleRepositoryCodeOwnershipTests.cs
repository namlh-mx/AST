using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Infrastructure;
using AST.Modules.IAM.Data.Repositories;
using ErrorOr;
using FluentAssertions;
using Xunit;

namespace AST.Modules.IAM.Tests.Integration;

// B2 (spec 2026-08-14 §5) — the reads role-identity ownership is decided on. The plain overload picks the
// composite's lock set; the context overload re-decides under the lock. Both must answer identically for
// identical data, because RoleDeclarationService compares one against the other and treats a difference
// as a concurrent redeclaration.
public sealed class RoleRepositoryCodeOwnershipTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);

    private RoleRepository RoleRepo => (RoleRepository)Roles;

    [Fact]
    public async Task GetCodeOwners_FindsAClosedRole_WhichTheTodayOnlyResolutionCannot()
    {
        SkipUnlessDbAvailable();

        var closed = await CreateRoleAsync("CO-R1", "Vai trò đã ngừng",
            new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-2)));

        var owners = await RoleRepo.GetCodeOwnersAsync("CO-R1", Today);

        owners.Should().ContainSingle();
        owners[0].RoleId.Should().Be(closed);
        owners[0].HasVersionInForceToday.Should().BeFalse("this is the state that makes reattachment legal (settled item 8)");
        owners[0].HasFutureVersion.Should().BeFalse();
    }

    [Fact]
    public async Task GetCodeOwners_MarksALiveRoleAsInForce()
    {
        SkipUnlessDbAvailable();

        var live = await CreateRoleAsync("CO-R2", "Vai trò đang chạy", OpenFrom2020);

        var owners = await RoleRepo.GetCodeOwnersAsync("CO-R2", Today);

        owners.Should().ContainSingle();
        owners[0].RoleId.Should().Be(live);
        owners[0].HasVersionInForceToday.Should().BeTrue(
            "reattaching to a live role would silently turn 'declare a new role' into 'edit someone else's role'");
    }

    // The owner was RENAMED AWAY from this code and is live under a different one.
    // Aggregating status over only the rows carrying the queried code reports it dormant, and the service
    // then reattaches to a live role. The status must be aggregated over ALL of the owner's versions.
    [Fact]
    public async Task GetCodeOwners_OwnerRenamedAwayButStillLive_IsReportedInForce()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CO-R7-OLD", "Vai trò",
            new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-1)));
        await InsertRoleVersionAsync(role, "CO-R7-NEW", "Vai trò đổi mã",
            new EffectivePeriod(Today, EffectivePeriod.OpenEnd));

        var owners = await RoleRepo.GetCodeOwnersAsync("CO-R7-OLD", Today);

        owners.Should().ContainSingle();
        owners[0].RoleId.Should().Be(role);
        owners[0].HasVersionInForceToday.Should().BeTrue(
            "the identity is live under its NEW code -- querying its OLD code must not report it as dormant and reattachable");
    }

    // A Pending owner (a version that starts later) is not closed. The ViewModel already treats Pending as
    // an existing role (RoleDeclarationViewModel.cs:1182); reattaching to it would collide with a planned start.
    [Fact]
    public async Task GetCodeOwners_OwnerWithAFutureVersion_IsReportedAsHavingOne()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CO-R8", "Vai trò",
            new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-2)));
        await InsertRoleVersionAsync(role, "CO-R8", "Vai trò sẽ chạy lại",
            new EffectivePeriod(Today.AddDays(5), EffectivePeriod.OpenEnd));

        var owners = await RoleRepo.GetCodeOwnersAsync("CO-R8", Today);

        owners.Should().ContainSingle();
        owners[0].HasVersionInForceToday.Should().BeFalse();
        owners[0].HasFutureVersion.Should().BeTrue("a planned restart is not a dormant role");
    }

    [Fact]
    public async Task GetCodeOwners_IsCaseInsensitive_MatchingTheColumnCollation()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CO-R3", "Vai trò", OpenFrom2020);

        var owners = await RoleRepo.GetCodeOwnersAsync("co-r3", Today);

        owners.Should().ContainSingle();
        owners[0].RoleId.Should().Be(role,
            because: "role_code is utf8mb4_0900_ai_ci (V002__role.sql:29): a case-sensitive read here would mint a second "
            + "identity for a code P6 already considers taken");
    }

    [Fact]
    public async Task GetCodeOwners_ReturnsEachIdentityOnce_AcrossManyVersions()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CO-R4", "Vai trò",
            new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));
        await InsertRoleVersionAsync(role, "CO-R4", "Vai trò đổi tên",
            new EffectivePeriod(new DateOnly(2021, 1, 1), EffectivePeriod.OpenEnd));

        var owners = await RoleRepo.GetCodeOwnersAsync("CO-R4", Today);

        owners.Should().ContainSingle("two versions of ONE identity are one owner -- a duplicate would be reported as a false ambiguity");
        owners[0].HasVersionInForceToday.Should().BeTrue("the open-ended second version covers today");
    }

    [Fact]
    public async Task GetCodeOwnersInComposite_SeesAnOwnerCreatedEarlierInTheSameTransaction()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CO-R5", "Tên cũ", OpenFrom2020);

        IReadOnlyList<RoleRepository.RoleCodeOwner> seen = [];
        var result = await new CompositeWrite(Connections).Enlist(RoleRepo, role).Enlist(RoleRepository.AdminFlagLockKey)
            .ExecuteAsync(async context =>
            {
                var write = await RoleRepo.UpsertAsync(
                    context, role, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "CO-R5-NEW", "Tên mới",
                    false, false, VersionOperationKind.Edit, OperationDateForToday(), "tester", null);
                write.IsError.Should().BeFalse(DescribeErrors(write.Errors));

                seen = await RoleRepo.GetCodeOwnersAsync(context, "CO-R5-NEW", Today);
                return Result.Success;
            });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        seen.Should().ContainSingle();
        seen[0].RoleId.Should().Be(role,
            because: "the re-decision must see the transaction's own writes -- a read on a second connection would not");
    }

    [Fact]
    public async Task GetByIdentityInComposite_SeesAVersionWrittenEarlierInTheSameTransaction()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CO-R6", "Tên cũ", OpenFrom2020);

        string? nameSeen = null;
        var result = await new CompositeWrite(Connections).Enlist(RoleRepo, role).Enlist(RoleRepository.AdminFlagLockKey)
            .ExecuteAsync(async context =>
            {
                var write = await RoleRepo.UpsertAsync(
                    context, role, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "CO-R6", "Tên mới",
                    false, false, VersionOperationKind.Edit, OperationDateForToday(), "tester", null);
                write.IsError.Should().BeFalse(DescribeErrors(write.Errors));

                var read = await RoleRepo.GetByIdentityAsync(context, role, Today);
                read.IsError.Should().BeFalse(DescribeErrors(read.Errors));
                nameSeen = read.Value.RoleName;
                return Result.Success;
            });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        nameSeen.Should().Be("Tên mới", "B4 compares against what the transaction itself can see, not a pre-lock snapshot");
    }

    [Theory]
    [InlineData("vt-kt", "VT-KT")]
    [InlineData(" VT-KT ", "VT-KT")]
    public void CodeLockKey_FoldsCaseAndTrims_SoTwoSpellingsOfOneCodeShareOneLock(string a, string b)
    {
        RoleRepository.CodeLockKey(a).Should().Be(RoleRepository.CodeLockKey(b),
            "RoleDeclarationService normalises before deriving the key; two spellings that differ only by "
            + "case or surrounding whitespace must share one lock so concurrent declarations cannot both pass "
            + "the ownership re-check under different keys");
    }
}
