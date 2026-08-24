using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Time;
using AST.Infrastructure;
using AST.Modules.IAM.Data;
using ErrorOr;
using FluentAssertions;
using AST.Core.Data;

namespace AST.Modules.IAM.Tests.Integration;

// "One operation, one business date" (docs/design-effective-period.md §3) at the role/grant write guards.
//
// Why these tests exist: the `Immediate` guards (a role/grant version starts on the operation's date and
// ends open; a close/revoke ends at date - 1) were first implemented by reading IBusinessDateProvider
// INSIDE the repository. That compiles, passes every same-day test, and is wrong: production registers
// the provider as a SINGLETON over a live clock (AST/App.xaml.cs, SystemBusinessDateProvider), so an
// operation straddling midnight runs its service half on D and its repository half on D+1. The failure is
// not symmetric — the close path fails OPEN: a close date of D, which the operation's own captured date
// says must be rejected, is accepted by a repository that has already rolled over to D+1.
//
// Every test here therefore builds ONE repository whose own clock is deliberately a day AHEAD of the
// operation date it is handed. A guard that honours the passed OperationDate is unaffected by that clock;
// a guard that reads its own is caught. This is the only shape that discriminates the two — while the two
// dates agree, the defect is invisible.
//
// If a change makes one of these fail, the fix is never to relax the assertion: it means a write-path
// guard has gone back to reading a clock (VersionedRepository's `_scopeToday` comment forbids exactly
// that, TASK 0 2026-08-11), or the operation date stopped being threaded from its single capture point.
public sealed class OperationDateGuardTests : IamRepositoryTestBase
{
    private const string Actor = "tester";

    // The operation captures D. Every repository below is built a day ahead of it.
    private static DateOnly OperationDay => Today;
    private static DateOnly RepositoryClockAhead => Today.AddDays(1);

    // T2 — close guard reads the PASSED date, not the repository's clock.
    // Closing "through today" is illegal under Immediate (the only legal close end is today - 1, the
    // INCLUSIVE end that means "ceases from today"). A repository already rolled over to D+1 computes
    // required = D and would wave it through.
    [Fact]
    public async Task Close_rejects_a_date_the_operations_own_business_date_forbids_even_when_the_repositorys_clock_has_rolled_over()
    {
        SkipUnlessDbAvailable();

        var roleId = await CreateRoleAsync("OPD-CLOSE", "Close guard role", new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd));
        var versionId = (await Roles.GetHistoryAsync(roleId)).Single().Id;
        var roleRepoAhead = BuildRoleRepositoryWithClock(RepositoryClockAhead);

        var result = await roleRepoAhead.CloseVersionAsync(
            roleId, versionId, newTo: OperationDay, new OperationDate(OperationDay), Actor, "retire");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.CloseDateMustBeImmediate");
        result.FirstError.Type.Should().Be(ErrorType.Validation);

        var afterwards = await Roles.GetHistoryAsync(roleId);
        afterwards.Should().ContainSingle("a rejected close must write nothing at all");
        afterwards.Single().EffectiveTo.Should().Be(EffectivePeriod.OpenEnd);
    }

    // T3 — the same date, threaded once, must let a LEGITIMATE revoke through. The pre-fix shape rejected
    // this: RolePermissionRepository read its clock in RevokeAsync and then AGAIN in the close path it
    // delegates to, so one revoke consulted the clock twice.
    [Fact]
    public async Task Revoke_accepts_the_operations_own_yesterday_even_when_the_repositorys_clock_has_rolled_over()
    {
        SkipUnlessDbAvailable();

        var roleId = await CreateRoleAsync("OPD-REVOKE", "Revoke guard role", new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd));
        var functionId = await CreateFunctionAsync("opd.revoke", new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd));
        var grantId = await CreateGrantAsync(roleId, functionId, new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd), ScopeLevel.Global);
        var versionId = (await RolePermissions.GetHistoryAsync(grantId)).Single().Id;
        var grantRepoAhead = BuildRolePermissionRepositoryWithClock(RepositoryClockAhead);

        var result = await grantRepoAhead.RevokeAsync(
            grantId, versionId, effectiveThrough: OperationDay.AddDays(-1), new OperationDate(OperationDay), Actor, "revoke");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var active = (await RolePermissions.GetHistoryAsync(grantId)).Where(v => v.IsActive).ToList();
        active.Should().ContainSingle();
        active.Single().EffectiveTo.Should().Be(
            OperationDay.AddDays(-1), "the grant ceases FROM the operation's own date, so its INCLUSIVE end is that date - 1");
    }

    // T4a — a role version starting on the OPERATION's date is legal, even though the repository's clock
    // says that date is already yesterday. A guard reading its own clock rejects a correct write.
    [Fact]
    public async Task Role_upsert_accepts_a_start_on_the_operations_own_date_even_when_the_repositorys_clock_has_rolled_over()
    {
        SkipUnlessDbAvailable();

        var roleId = await Roles.CreateIdentityAsync();
        var roleRepoAhead = BuildRoleRepositoryWithClock(RepositoryClockAhead);

        var result = await new CompositeWrite(Connections).Enlist(roleRepoAhead, roleId)
            .ExecuteAsync(async context =>
            {
                var write = await roleRepoAhead.UpsertAsync(
                    context, roleId, new EffectivePeriod(OperationDay, EffectivePeriod.OpenEnd), "OPD-R4A", "Operation-day role",
                    isAdminRole: false, adminFlagChangeAuthorized: false, VersionOperationKind.Add,
                    new OperationDate(OperationDay), Actor, "seed");
                return write.IsError ? write.Errors : Result.Success;
            });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await Roles.GetHistoryAsync(roleId)).Single().EffectiveFrom.Should().Be(OperationDay);
    }

    // T4b — THE FAIL-OPEN TWIN, and the reason this file exists. A start of D+1 is a forward-dated role
    // version, forbidden outright by Immediate. A repository whose clock already reads D+1 sees "starts
    // today" and PERSISTS it — the exact state whose unreachability the whole Immediate rule rests on.
    [Fact]
    public async Task Role_upsert_rejects_a_start_after_the_operations_own_date_even_when_the_repositorys_clock_has_reached_it()
    {
        SkipUnlessDbAvailable();

        var roleId = await Roles.CreateIdentityAsync();
        var roleRepoAhead = BuildRoleRepositoryWithClock(RepositoryClockAhead);

        var result = await new CompositeWrite(Connections).Enlist(roleRepoAhead, roleId)
            .ExecuteAsync(async context =>
            {
                var write = await roleRepoAhead.UpsertAsync(
                    context, roleId, new EffectivePeriod(RepositoryClockAhead, EffectivePeriod.OpenEnd), "OPD-R4B", "Forward role",
                    isAdminRole: false, adminFlagChangeAuthorized: false, VersionOperationKind.Add,
                    new OperationDate(OperationDay), Actor, "seed");
                return write.IsError ? write.Errors : Result.Success;
            });

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.EffectiveFromMustBeToday");
        result.FirstError.Type.Should().Be(ErrorType.Validation);
        (await Roles.GetHistoryAsync(roleId)).Should().BeEmpty("a forward-dated role version must never reach the table");
    }

    // T5 — the same pair on the PERMISSION writer. Not optional: this is the retroactive/future permission
    // seam, and the precedent set on 2026-08-12 is one guard test per writer, never one for the pair.
    [Fact]
    public async Task Grant_upsert_accepts_a_start_on_the_operations_own_date_even_when_the_repositorys_clock_has_rolled_over()
    {
        SkipUnlessDbAvailable();

        var roleId = await CreateRoleAsync("OPD-G5A", "Grant host role", new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd));
        var functionId = await CreateFunctionAsync("opd.grant.ok", new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd));
        var grantId = await RolePermissions.CreateIdentityAsync();
        var grantRepoAhead = BuildRolePermissionRepositoryWithClock(RepositoryClockAhead);

        var result = await grantRepoAhead.UpsertAsync(
            grantId, new EffectivePeriod(OperationDay, EffectivePeriod.OpenEnd), roleId, functionId, ScopeLevel.Global,
            VersionOperationKind.Add, new OperationDate(OperationDay), Actor, "seed");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await RolePermissions.GetHistoryAsync(grantId)).Single().EffectiveFrom.Should().Be(OperationDay);
    }

    [Fact]
    public async Task Grant_upsert_rejects_a_start_after_the_operations_own_date_even_when_the_repositorys_clock_has_reached_it()
    {
        SkipUnlessDbAvailable();

        var roleId = await CreateRoleAsync("OPD-G5B", "Grant host role", new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd));
        var functionId = await CreateFunctionAsync("opd.grant.future", new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd));
        var grantId = await RolePermissions.CreateIdentityAsync();
        var grantRepoAhead = BuildRolePermissionRepositoryWithClock(RepositoryClockAhead);

        var result = await grantRepoAhead.UpsertAsync(
            grantId, new EffectivePeriod(RepositoryClockAhead, EffectivePeriod.OpenEnd), roleId, functionId, ScopeLevel.Global,
            VersionOperationKind.Add, new OperationDate(OperationDay), Actor, "seed");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.EffectiveFromMustBeToday");
        result.FirstError.Type.Should().Be(ErrorType.Validation);
        (await RolePermissions.GetHistoryAsync(grantId)).Should().BeEmpty("a forward-dated grant version must never reach the table");
    }
}
