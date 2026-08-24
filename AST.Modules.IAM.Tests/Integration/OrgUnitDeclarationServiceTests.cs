using System.Text.Json;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Modules.IAM.Data;
using AST.Core.Iam.Repositories;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using ErrorOr;
using FluentAssertions;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// Pins OrgUnitDeclarationService (AST.Modules.IAM/OrgUnitDeclarationService.cs) -- closes the gap described
// in AST.Core/Iam/IOrgUnitDeclarationService.cs's own doc comment: the declaration screen used to call
// IOrgUnitRepository.CancelPlanAsync/CloseVersionAsync directly and pick the close-vs-cancel branch itself.
// Real MySQL, no mocking of persistence (rule-testing invariant 1) -- IAuthorizationService/
// ICurrentWindowsUser/IAuditLogWriter are non-persistence seams, plain hand-rolled fakes, never Moq.
public sealed class OrgUnitDeclarationServiceTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod Year2021 = new(new DateOnly(2021, 1, 1), new DateOnly(2021, 12, 31));

    private const string Actor = "tester";

    private OrgUnitRepository OrgUnitRepo => (OrgUnitRepository)OrgUnits;

    private OrgUnitDeclarationService BuildService(
        IAuthorizationService? authorization = null, IAuditLogWriter? auditLog = null, string actor = Actor,
        IBreakGlassPolicy? breakGlass = null) =>
        new(
            OrgUnitRepo,
            Connections,
            auditLog ?? new AST.Infrastructure.AuditLogWriter(),
            authorization ?? new FakeAuthorizationService(new DataScope(ScopeLevel.Global, null, actor)),
            new FakeCurrentWindowsUser(actor),
            new FixedBusinessDateProvider(Today),
            breakGlass ?? new FakeBreakGlassPolicy());

    // =========================================================================================
    // C1 -- unauthorized actor: Authz.* Forbidden, no audit_log row, no version write.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_AuthorizationDenied_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("C1-ORG", "Đơn vị", "C1-ORG", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;
        var versionRowsBefore = await CountRowsAsync(org);

        var denied = new FakeAuthorizationService(Error.Forbidden("Authz.NotGranted", "Không được cấp quyền."));

        var request = new CloseOrgUnitDeclarationRequest(org, versionId, Today, "retire");

        var result = await BuildService(authorization: denied).CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        // THE DISCRIMINATOR (qa fix round FR1): pins the authorizer's OWN denial code propagating through
        // unchanged -- if the service swallowed it and substituted its own Forbidden, Type alone would still
        // pass.
        result.FirstError.Code.Should().Be("Authz.NotGranted");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRowsAsync(org)).Should().Be(versionRowsBefore);
        (await CountAllAuditRowsAsync()).Should().Be(0);
    }

    // =========================================================================================
    // C2 -- authorized but the target unit is outside the resolved scope.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_TargetOutsideScope_IsRejected_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("C2-ORG", "Đơn vị", "C2-ORG", null, OpenFrom2020);
        var otherOrg = await CreateOrgUnitAsync("C2-OTHER", "Đơn vị khác", "C2-OTHER", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;
        var versionRowsBefore = await CountRowsAsync(org);

        var narrowScope = new FakeAuthorizationService(new DataScope(ScopeLevel.OwnOrgUnit, otherOrg, Actor));

        var request = new CloseOrgUnitDeclarationRequest(org, versionId, Today, "retire");

        var result = await BuildService(authorization: narrowScope).CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.NotInScope");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRowsAsync(org)).Should().Be(versionRowsBefore);
        (await CountAllAuditRowsAsync()).Should().Be(0);
    }

    // =========================================================================================
    // C3 -- ScopeLevel.Self is not applicable to org_unit_version (no owner column). Must fail clear
    // with Authz.ScopeInsufficient, never let IsWithinScopeAsync's InvalidOperationException escape.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_SelfScope_ReturnsScopeInsufficient_NeverThrows()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("C3-ORG", "Đơn vị", "C3-ORG", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var selfScope = new FakeAuthorizationService(new DataScope(ScopeLevel.Self, null, Actor));

        var request = new CloseOrgUnitDeclarationRequest(org, versionId, Today, "retire");

        // If the guard were missing, IsWithinScopeAsync/GetHistoryInScopeAsync would throw
        // InvalidOperationException here instead of returning an ErrorOr -- this call itself is the assertion
        // that nothing escapes.
        var result = await BuildService(authorization: selfScope).CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Authz.ScopeInsufficient");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
    }

    // =========================================================================================
    // C4 -- a version id that is already cancelled/superseded must resolve to THIS service's own
    // OrgUnit.VersionNotFound, never fall through to the engine's VersionedRepository.VersionNotFound.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_VersionAlreadyCancelled_ReturnsServiceOwnedVersionNotFound()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("C4-ORG", "Đơn vị hiện tại", "C4-ORG", null, OpenFrom2020);
        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var future = await OrgUnits.UpsertAsync(
            org, futurePlan, "C4-ORG", "Kế hoạch 2027", "Kế hoạch 2027", null, VersionOperationKind.Edit, Actor, "plan");
        future.IsError.Should().BeFalse(DescribeErrors(future.Errors));
        var futureVersionId = future.Value.NewVersionId;

        (await OrgUnits.CancelPlanAsync(org, futureVersionId, Today, Actor, "seed cancel")).IsError.Should().BeFalse();

        var request = new CloseOrgUnitDeclarationRequest(org, futureVersionId, EffectiveThrough: null, "retry cancel");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.VersionNotFound");
        result.FirstError.Type.Should().Be(ErrorType.NotFound);
    }

    // TASK 0 (2026-08-11) — Step 1, RED-first: twin of
    // RoleDeclarationServiceTests.CloseRoleDeclarationAsync_MidnightRolloverBetweenServiceAndEngineReads_CancelSucceeds
    // -- the org-unit service has the identical "capture today once, engine re-reads it independently"
    // pairing, so it must not be left as the surviving instance of design-effective-period.md §3's
    // violation. Pre-fix, this test is RED with VersionedRepository.NotAFuturePlan; after the fix it is
    // GREEN and the cancel persists (cancelled = 1, isactive = 0).
    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_MidnightRolloverBetweenServiceAndEngineReads_CancelSucceeds()
    {
        SkipUnlessDbAvailable();

        // Parented on purpose (backlog 0.8, 2026-08-21): a ROOT org unit may not be closed, so a
        // parentless unit here would now fail on the root gate and stop exercising this test's own subject.
        var parent = await CreateOrgUnitAsync("B60PAR", "Đơn vị cha", "B60PAR", null, OpenFrom2020);
        var org = await CreateOrgUnitAsync("B60ROLL", "Đơn vị hiện tại", "B60ROLL", parent, OpenFrom2020);
        var sameDay = await OrgUnits.UpsertAsync(
            org, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "B60ROLL", "Kế hoạch hôm nay",
            "Kế hoạch hôm nay", parent, VersionOperationKind.Edit, Actor, "plan");
        sameDay.IsError.Should().BeFalse(DescribeErrors(sameDay.Errors));

        var advancing = new AdvancingBusinessDateProvider(Today);
        var rolloverOrgUnitRepo = new OrgUnitRepository(
            Connections, new StandardScopeFilterBuilder(), new EffectivePeriodResolver(), new PeriodEditor(),
            FkValidator, IamTemporalFkEdges.CreateRegistry(), advancing, new DbParentCoverageProvider(Connections));
        var service = new OrgUnitDeclarationService(
            rolloverOrgUnitRepo, Connections, new AST.Infrastructure.AuditLogWriter(),
            new FakeAuthorizationService(new DataScope(ScopeLevel.Global, null, Actor)), new FakeCurrentWindowsUser(Actor),
            advancing, new FakeBreakGlassPolicy());

        var request = new CloseOrgUnitDeclarationRequest(org, sameDay.Value.NewVersionId, EffectiveThrough: null, "hủy kế hoạch");

        var result = await service.CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        result.Errors.Should().NotContain(e => e.Code == "VersionedRepository.NotAFuturePlan");

        var history = await OrgUnits.GetHistoryInScopeAsync(new DataScope(ScopeLevel.Global, null, Actor), org);
        var targetRow = history.Should().ContainSingle(h => h.Id == sameDay.Value.NewVersionId).Subject;
        targetRow.IsActive.Should().BeFalse();
        targetRow.Cancelled.Should().BeTrue();
    }

    // =========================================================================================
    // C5-C10 -- each of the six VersionClose.* guards, reached THROUGH the service.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_CancelPlanBranch_WithEffectiveThroughSupplied_IsRejected()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("C5-ORG", "Đơn vị", "C5-ORG", null, OpenFrom2020);
        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var future = await OrgUnits.UpsertAsync(
            org, futurePlan, "C5-ORG", "Kế hoạch 2027", "Kế hoạch 2027", null, VersionOperationKind.Edit, Actor, "plan");
        future.IsError.Should().BeFalse(DescribeErrors(future.Errors));

        var request = new CloseOrgUnitDeclarationRequest(org, future.Value.NewVersionId, new DateOnly(2027, 6, 1), "note");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateNotApplicableToCancelPlan);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_VersionAlreadyEnded_IsRejected()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("C6-ORG", "Đơn vị", "C6-ORG", null, Year2021);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, new DateOnly(2021, 6, 1))).Value.Id;

        var request = new CloseOrgUnitDeclarationRequest(org, versionId, Today, "retire outside period");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.VersionAlreadyEnded);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_RetireWithoutEffectiveThrough_RequiresCloseDate()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("C7-ORG", "Đơn vị", "C7-ORG", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var request = new CloseOrgUnitDeclarationRequest(org, versionId, EffectiveThrough: null, "retire");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateRequired);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // D2 (2026-08-10) relaxed the close-date floor by exactly one day (today - 1 is now accepted, see
    // VersionCloseRules.Validate) — pin two days back, still past the relaxed floor.
    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_CloseDateInPast_IsRejected()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("C8-ORG", "Đơn vị", "C8-ORG", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var request = new CloseOrgUnitDeclarationRequest(org, versionId, Today.AddDays(-2), "retire back-dated");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateInPast);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_CloseDateEqualsVersionEnd_IsRejected()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("C9-ORG", "Đơn vị", "C9-ORG", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var request = new CloseOrgUnitDeclarationRequest(org, versionId, EffectivePeriod.OpenEnd, "close at open end");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateEqualsVersionEnd);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_CloseDateOutsideVersionPeriod_IsRejected()
    {
        SkipUnlessDbAvailable();

        var boundedPeriod = new EffectivePeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var org = await CreateOrgUnitAsync("C10-ORG", "Đơn vị", "C10-ORG", null, boundedPeriod);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        // Exactly ONE DAY past the version's own EffectiveTo -- the boundary itself (qa fix round FR4: was
        // a far-future date identical in shape to C15's discriminator, so this now exercises the range
        // guard's actual edge rather than duplicating C15). The "before From" half of the range guard is
        // unreachable on the retire branch, per VersionCloseRules.Validate's own comment: reaching this
        // guard already requires requestedCloseDate >= today >= targetPeriod.From.
        var request = new CloseOrgUnitDeclarationRequest(
            org, versionId, boundedPeriod.To.AddDays(1), "close one day past end");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateOutsideVersionPeriod);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // =========================================================================================
    // C11 -- happy retire: effective_to shrunk; exactly one audit_log row with the described shape.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_RetireCurrentlyEffectiveOrgUnit_Succeeds_WritesOneAuditRow()
    {
        SkipUnlessDbAvailable();

        // Parented on purpose (backlog 0.8, 2026-08-21): a ROOT org unit may not be closed, so a
        // parentless unit here would now fail on the root gate and stop exercising this test's own subject.
        var parent = await CreateOrgUnitAsync("C11-PAR", "Đơn vị cha", "C11-PAR", null, OpenFrom2020);
        var org = await CreateOrgUnitAsync("C11-ORG", "Đơn vị", "C11-ORG", parent, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var request = new CloseOrgUnitDeclarationRequest(org, versionId, Today, "retire");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var closed = await OrgUnits.GetByIdentityAsync(org, Today);
        closed.IsError.Should().BeFalse(DescribeErrors(closed.Errors));
        closed.Value.EffectiveTo.Should().Be(Today);
        (await OrgUnits.GetByIdentityAsync(org, Today.AddDays(1))).IsError.Should().BeTrue(
            "the retired org unit must no longer be effective the day after the cut");

        (await CountAllAuditRowsAsync()).Should().Be(1, "exactly one audit_log row must be written for a successful retire");
        var target = $"org_unit_version:{versionId}";
        (await CountAuditRowsForTargetAsync(target)).Should().Be(1);

        var (eventType, username, detail) = await ReadAuditRowAsync(target);
        eventType.Should().Be("orgunit-close");
        username.Should().Be(Actor);
        detail.Should().Contain("\"branch\": \"close\"");
        detail.Should().Contain($"\"orgUnitId\": {org}");
        detail.Should().Contain($"\"versionId\": {versionId}");
    }

    // =========================================================================================
    // C12 -- happy cancel-plan: isactive=0, cancelled=1; exactly one audit row, branch=cancel, close
    // date null. No-adjacent-predecessor shape: the version row's OWN recorded_by never changes on this
    // branch, so the audit row is the only place that records WHO cancelled it.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_CancelFuturePlanWithNoAdjacentPredecessor_Succeeds_WritesAuditRowForCanceller()
    {
        SkipUnlessDbAvailable();

        // Parented on purpose (backlog 0.8, 2026-08-21): a ROOT org unit may not be closed, so a
        // parentless unit here would now fail on the root gate and stop exercising this test's own subject.
        var parent = await CreateOrgUnitAsync("C12-PAR", "Đơn vị cha", "C12-PAR", null, OpenFrom2020);
        var org = await CreateOrgUnitAsync("C12-ORG", "Đơn vị hiện tại", "C12-ORG", parent, OpenFrom2020);
        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var future = await OrgUnits.UpsertAsync(
            org, futurePlan, "C12-ORG", "Kế hoạch 2027", "Kế hoạch 2027", parent, VersionOperationKind.Edit, Actor, "plan");
        future.IsError.Should().BeFalse(DescribeErrors(future.Errors));
        var futureVersionId = future.Value.NewVersionId;

        const string canceller = "canceller";
        var request = new CloseOrgUnitDeclarationRequest(org, futureVersionId, EffectiveThrough: null, "cancel plan");

        var result = await BuildService(actor: canceller).CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(new DataScope(ScopeLevel.Global, null, canceller), org);
        var targetRow = history.Should().ContainSingle(h => h.Id == futureVersionId).Subject;
        targetRow.IsActive.Should().BeFalse();
        targetRow.Cancelled.Should().BeTrue();
        targetRow.RecordedBy.Should().Be(
            Actor,
            "the org_unit_version row itself still shows the CREATOR on a no-predecessor cancel -- proving the " +
            "audit_log row is the ONLY place the canceller's identity is recorded");

        var target = $"org_unit_version:{futureVersionId}";
        (await CountAuditRowsForTargetAsync(target)).Should().Be(1);
        var (eventType, username, detail) = await ReadAuditRowAsync(target);
        eventType.Should().Be("orgunit-close");
        username.Should().Be(canceller, "THE DISCRIMINATOR: the audit row must record the CANCELLER, not the plan's creator");
        detail.Should().Contain("\"branch\": \"cancel\"");
        detail.Should().Contain("\"effectiveThrough\": null");
    }

    // =========================================================================================
    // C13 -- an injected failing IAuditLogWriter must roll back the WHOLE composite: version row
    // unchanged AND no audit row.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_AuditWriteFails_RollsBackWholeComposite()
    {
        SkipUnlessDbAvailable();

        // Parented on purpose (backlog 0.8, 2026-08-21): a ROOT org unit may not be closed, so a
        // parentless unit here would stop at the root gate BEFORE FailingAuditLogWriter is ever
        // invoked -- and every assertion below is satisfied by that refusal path too, so the test
        // would stay green while proving nothing..
        var parent = await CreateOrgUnitAsync("C13-PAR", "Đơn vị cha", "C13-PAR", null, OpenFrom2020);
        var org = await CreateOrgUnitAsync("C13-ORG", "Đơn vị", "C13-ORG", parent, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var request = new CloseOrgUnitDeclarationRequest(org, versionId, Today, "retire");

        var result = await BuildService(auditLog: new FailingAuditLogWriter()).CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        // Pins WHICH failure this is. Without it any refusal keeps the test green -- which is exactly
        // how the root gate silently took this test over. rule-testing forbids a bare IsError.
        result.FirstError.Code.Should().Be("AuditLog.Injected");

        var unchanged = await OrgUnits.GetByIdentityAsync(org, Today);
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.EffectiveTo.Should().Be(
            EffectivePeriod.OpenEnd, "the version write must roll back together with the failed audit write");
        (await CountAllAuditRowsAsync()).Should().Be(0);
    }

    // =========================================================================================
    // C14 -- reverse-FK BLOCK (a child org unit would lose coverage): the engine's TemporalFk.* error
    // still surfaces through the service, and no audit row survives.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_ChildOrgUnitWouldLoseCoverage_IsBlocked_WritesNoAuditRow()
    {
        SkipUnlessDbAvailable();

        // Parented on purpose (backlog 0.8, 2026-08-21): a ROOT org unit may not be closed, so a
        // parentless unit here would now fail on the root gate and stop exercising this test's own subject.
        var grandparent = await CreateOrgUnitAsync("C14-GP", "Đơn vị ông", "C14-GP", null, OpenFrom2020);
        var parent = await CreateOrgUnitAsync("C14-PAR", "Đơn vị cha", "C14-PAR", grandparent, OpenFrom2020);
        await CreateOrgUnitAsync("C14-CHI", "Đơn vị con", "C14-CHI", parent, OpenFrom2020);
        var parentVersionId = (await OrgUnits.GetByIdentityAsync(parent, Today)).Value.Id;

        // Child stays open-ended -- shrinking the parent to end anywhere before OpenEnd leaves the child
        // uncovered beyond the cut.
        var request = new CloseOrgUnitDeclarationRequest(parent, parentVersionId, new DateOnly(2027, 12, 31), "retire");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.DependentsUncovered");

        var unchanged = await OrgUnits.GetByIdentityAsync(parent, Today);
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd, "a BLOCKed retire must not touch effective_to");
        (await CountAllAuditRowsAsync()).Should().Be(0, "a Close blocked by the temporal-FK guard must leave no audit_log row");
    }

    // =========================================================================================
    // C15 -- discriminating: a close date the OLD VM path would have accepted (after the version's own
    // effective_to) must now fail with VersionClose.CloseDateOutsideVersionPeriod, never the engine's
    // VersionedRepository.InvalidShrink.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_CloseDateAfterVersionEnd_IsRejected_NeverSurfacesEngineInvalidShrink()
    {
        SkipUnlessDbAvailable();

        var boundedPeriod = new EffectivePeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var org = await CreateOrgUnitAsync("C15-ORG", "Đơn vị", "C15-ORG", null, boundedPeriod);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        // Today (2026-07-03) is within [2026-01-01, 2026-12-31]. The old VM path floor-checked only
        // "close date >= today", which 2027-06-01 satisfies -- it never checked against the TARGET
        // version's own effective_to, so the old path would have let this reach the engine.
        var request = new CloseOrgUnitDeclarationRequest(org, versionId, new DateOnly(2027, 6, 1), "close after version end");

        var result = await BuildService().CloseOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateOutsideVersionPeriod);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
        result.Errors.Should().NotContain(e => e.Code == "VersionedRepository.InvalidShrink");

        var unchanged = await OrgUnits.GetByIdentityAsync(org, Today);
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.EffectiveTo.Should().Be(new DateOnly(2026, 12, 31), "a rejected close must not touch effective_to");
    }

    // =========================================================================================
    // C16 -- a Windows identity the OS could not resolve must still leave an attributable audit row:
    // the service substitutes "unknown" rather than writing a NULL actor or failing the close.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_UnresolvedWindowsUsername_WritesAuditRowWithUnknownActor()
    {
        SkipUnlessDbAvailable();

        // Parented on purpose (backlog 0.8, 2026-08-21): a ROOT org unit may not be closed, so a
        // parentless unit here would now fail on the root gate and stop exercising this test's own subject.
        var parent = await CreateOrgUnitAsync("C16-PAR", "Đơn vị cha", "C16-PAR", null, OpenFrom2020);
        var org = await CreateOrgUnitAsync("C16-ORG", "Đơn vị", "C16-ORG", parent, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var service = new OrgUnitDeclarationService(
            OrgUnitRepo,
            Connections,
            new AST.Infrastructure.AuditLogWriter(),
            new FakeAuthorizationService(new DataScope(ScopeLevel.Global, null, Actor)),
            new FakeCurrentWindowsUser(null),
            new FixedBusinessDateProvider(Today),
            new FakeBreakGlassPolicy());

        var result = await service.CloseOrgUnitDeclarationAsync(
            new CloseOrgUnitDeclarationRequest(org, versionId, Today, "retire"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var target = $"org_unit_version:{versionId}";
        (await CountAuditRowsForTargetAsync(target)).Should().Be(1);
        var (_, username, _) = await ReadAuditRowAsync(target);
        username.Should().Be("unknown", "an unresolved identity must still produce a written, non-null actor");
    }

    // =========================================================================================
    // C17 -- C13's rollback proof on the OTHER branch: a failing audit write must also roll back a
    // cancel-plan, which writes no business column of its own and so would otherwise leave a silently
    // cancelled version with nothing recording who cancelled it.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_AuditWriteFailsOnCancelBranch_RollsBackWholeComposite()
    {
        SkipUnlessDbAvailable();

        // Parented on purpose (backlog 0.8, 2026-08-21): a ROOT org unit may not be closed, so a
        // parentless unit here would stop at the root gate BEFORE FailingAuditLogWriter is ever
        // invoked -- and every assertion below is satisfied by that refusal path too, so the test
        // would stay green while proving nothing..
        var parent = await CreateOrgUnitAsync("C17-PAR", "Đơn vị cha", "C17-PAR", null, OpenFrom2020);
        var org = await CreateOrgUnitAsync("C17-ORG", "Đơn vị hiện tại", "C17-ORG", parent, OpenFrom2020);
        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var future = await OrgUnits.UpsertAsync(
            org, futurePlan, "C17-ORG", "Kế hoạch 2027", "Kế hoạch 2027", parent, VersionOperationKind.Edit, Actor, "plan");
        future.IsError.Should().BeFalse(DescribeErrors(future.Errors));
        var futureVersionId = future.Value.NewVersionId;

        var result = await BuildService(auditLog: new FailingAuditLogWriter()).CloseOrgUnitDeclarationAsync(
            new CloseOrgUnitDeclarationRequest(org, futureVersionId, EffectiveThrough: null, "cancel plan"));

        result.IsError.Should().BeTrue();
        // Pins WHICH failure this is. Without it any refusal keeps the test green -- which is exactly
        // how the root gate silently took this test over. rule-testing forbids a bare IsError.
        result.FirstError.Code.Should().Be("AuditLog.Injected");

        var history = await OrgUnits.GetHistoryInScopeAsync(new DataScope(ScopeLevel.Global, null, Actor), org);
        var targetRow = history.Should().ContainSingle(h => h.Id == futureVersionId).Subject;
        targetRow.IsActive.Should().BeTrue("the cancel must roll back together with the failed audit write");
        targetRow.Cancelled.Should().BeFalse();
        (await CountAllAuditRowsAsync()).Should().Be(0);
    }

    // =========================================================================================
    // C18 -- closing a CHILD unit (parent_id non-null) is the ordinary shape on a real org tree; C14
    // only ever proves the BLOCKed direction. Nothing extra is Enlisted for a parent, so this also
    // pins that the composite is complete without one.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_RetireChildOrgUnit_Succeeds_WritesOneAuditRow()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync("C18-PAR", "Đơn vị cha", "C18-PAR", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync("C18-CHI", "Đơn vị con", "C18-CHI", parent, OpenFrom2020);
        var childVersionId = (await OrgUnits.GetByIdentityAsync(child, Today)).Value.Id;

        var result = await BuildService().CloseOrgUnitDeclarationAsync(
            new CloseOrgUnitDeclarationRequest(child, childVersionId, Today, "retire"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var closed = await OrgUnits.GetByIdentityAsync(child, Today);
        closed.IsError.Should().BeFalse(DescribeErrors(closed.Errors));
        closed.Value.EffectiveTo.Should().Be(Today);

        (await CountAuditRowsForTargetAsync($"org_unit_version:{childVersionId}")).Should().Be(1);

        var parentUntouched = await OrgUnits.GetByIdentityAsync(parent, Today);
        parentUntouched.IsError.Should().BeFalse(DescribeErrors(parentUntouched.Errors));
        parentUntouched.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd, "closing a child must not touch its parent");
    }

    // =========================================================================================
    // A1..A10 -- AddOrgUnitDeclarationAsync (backlog 0.4b, 2026-08-17). The invariant under test is
    // design-effective-period.md §7: the identity header and its first version commit or roll back
    // TOGETHER, so a failure at ANY step leaves ZERO org_unit rows. Every negative case below therefore
    // asserts the header count, not only the error code -- the error code alone was what made the
    // pre-slice compensation look correct while a throw or a dead process still orphaned a header.
    // =========================================================================================

    private AddOrgUnitDeclarationRequest AddRequest(
        string orgCode, long? parentId = null, EffectivePeriod? period = null, string? reason = "khai báo mới") =>
        new(period ?? OpenFrom2020, orgCode, $"Đơn vị {orgCode}", orgCode, parentId, reason, null);

    [Fact]
    public async Task AddOrgUnitDeclarationAsync_WithParent_MintsHeaderVersionAndAuditInOneTransaction()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync("A1PAR", "Đơn vị cha", "A1PAR", null, OpenFrom2020);

        var result = await BuildService().AddOrgUnitDeclarationAsync(AddRequest("A1CHI", parent));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await CountRowsAsync(result.Value.OrgUnitId)).Should().Be(1);
        (await CountHeaderRowsAsync(result.Value.OrgUnitId)).Should().Be(1);

        var saved = await OrgUnits.GetByIdentityAsync(result.Value.OrgUnitId, Today);
        saved.IsError.Should().BeFalse(DescribeErrors(saved.Errors));
        saved.Value.ParentId.Should().Be(parent);
        saved.Value.OrgCode.Should().Be("A1CHI");

        var audit = await ReadAuditRowAsync($"org_unit_version:{result.Value.Write.NewVersionId}");
        audit.EventType.Should().Be("orgunit-add");
        audit.Username.Should().Be(Actor);
        // EXACTLY one. ReadAuditRowAsync's SQL ends in LIMIT 1, so it cannot see a duplicate -- without this
        // count a service that wrote the row twice would pass every assertion above.
        (await CountAllAuditRowsAsync()).Should().Be(1);
    }

    // Pins the `isactive = 1` clause of the root probe, which nothing else does: delete that clause and every
    // other A-test stays green. It is load-bearing in ordinary use -- the 8-case algebra
    // soft-deactivates a row and inserts a remnant on every edit, so a root that has ever been edited leaves
    // an isactive = 0 row whose ORIGINAL period is still on the table. Without the clause that dead row would
    // block every future root forever.
    [Fact]
    public async Task AddOrgUnitDeclarationAsync_Root_IgnoresASoftDeactivatedRootRow()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync(
            "A11OLD", "Đơn vị gốc", "A11OLD", null, new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd));

        // An in-place edit of the SAME period: the algebra soft-deactivates the original row and inserts a
        // replacement, leaving an isactive = 0 root row covering 2020-01-01..open.
        var edited = await OrgUnits.UpsertAsync(
            root, new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd), "A11OLD",
            "Đơn vị gốc đổi tên", "A11OLD", null, VersionOperationKind.Edit, Actor, "đổi tên");
        edited.IsError.Should().BeFalse(DescribeErrors(edited.Errors));
        (await CountInactiveRootRowsAsync()).Should().BeGreaterThan(
            0, "the fixture must actually leave a soft-deactivated root row, or this test proves nothing");

        // Now retire the LIVE root so only the dead row could still collide, and declare a successor.
        var closed = await OrgUnits.CloseVersionAsync(
            root, (await OrgUnits.GetByIdentityAsync(root, Today)).Value.Id, Today,
            OperationDateForToday(), Actor, "đóng");
        closed.IsError.Should().BeFalse(DescribeErrors(closed.Errors));

        var result = await BuildService().AddOrgUnitDeclarationAsync(
            AddRequest("A11NEW", period: new EffectivePeriod(Today.AddDays(1), EffectivePeriod.OpenEnd)));

        result.IsError.Should().BeFalse(
            "a soft-deactivated root row is not a root — only isactive = 1 rows are. " + DescribeErrors(result.Errors));
    }

    [Fact]
    public async Task AddOrgUnitDeclarationAsync_Root_WhenNoRootExists_Succeeds()
    {
        SkipUnlessDbAvailable();

        var result = await BuildService().AddOrgUnitDeclarationAsync(AddRequest("A2ROOT"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await CountRowsAsync(result.Value.OrgUnitId)).Should().Be(1);
    }

    // THE F-01 REGRESSION TEST. The pre-slice probe asked GetInScopeAsync "is a root
    // effective TODAY?", so a root declared for a future period was invisible and a second root went through.
    [Fact]
    public async Task AddOrgUnitDeclarationAsync_Root_WhenAnExistingRootIsFutureDated_IsRejectedAndMintsNothing()
    {
        SkipUnlessDbAvailable();

        var futurePeriod = new EffectivePeriod(Today.AddDays(30), EffectivePeriod.OpenEnd);
        await CreateOrgUnitAsync("A3FUT", "Đơn vị gốc tương lai", "A3FUT", null, futurePeriod);
        var headersBefore = await CountAllHeaderRowsAsync();

        // Overlaps the future root even though nothing is a root TODAY.
        var result = await BuildService().AddOrgUnitDeclarationAsync(
            AddRequest("A3SEC", period: new EffectivePeriod(Today.AddDays(60), EffectivePeriod.OpenEnd)));

        result.IsError.Should().BeTrue("a second root overlapping the future root's period is not allowed");
        result.FirstError.Code.Should().Be("OrgUnit.RootPeriodOverlaps");
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore, "a rejected Add must mint no header");
    }

    // THE DISCRIMINATOR for the test above: the rule is OVERLAP, not "a root has ever existed" (requester
    // ruling 2026-08-17). Without this, a probe that simply rejected every second root would also pass.
    [Fact]
    public async Task AddOrgUnitDeclarationAsync_Root_SucceedingARetiredRoot_IsAccepted()
    {
        SkipUnlessDbAvailable();

        await CreateOrgUnitAsync(
            "A4OLD", "Đơn vị gốc cũ", "A4OLD", null,
            new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));

        var result = await BuildService().AddOrgUnitDeclarationAsync(
            AddRequest("A4NEW", period: new EffectivePeriod(new DateOnly(2021, 1, 1), EffectivePeriod.OpenEnd)));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await CountRowsAsync(result.Value.OrgUnitId)).Should().Be(1);
    }

    // Note: every other root test presents at most ONE active root candidate, so a period-blind
    // `LIMIT 1` regression in the probe would still pass them all. Here the 8-case algebra leaves TWO active
    // root segments and only the SECOND one overlaps the proposed root -- a probe that reads one row, or
    // reads them in insertion order and stops, misses the collision.
    [Fact]
    public async Task AddOrgUnitDeclarationAsync_Root_ChecksEveryActiveRootSegment_NotJustTheFirst()
    {
        SkipUnlessDbAvailable();

        // One root over 2020-01-01..open, then an in-period edit that SPLITS it: the algebra leaves an
        // earlier segment plus a later remnant, both isactive = 1, both roots (InsertRemnantAsync copies
        // parent_id verbatim).
        var root = await CreateOrgUnitAsync(
            "A12ROOT", "Đơn vị gốc", "A12ROOT", null, new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd));
        var split = await OrgUnits.UpsertAsync(
            root, new EffectivePeriod(new DateOnly(2021, 1, 1), new DateOnly(2021, 12, 31)), "A12ROOT",
            "Đơn vị gốc 2021", "A12ROOT", null, VersionOperationKind.Edit, Actor, "tách kỳ");
        split.IsError.Should().BeFalse(DescribeErrors(split.Errors));

        var segments = await CountActiveRootRowsAsync();
        segments.Should().BeGreaterThan(
            1, "the fixture must actually leave MORE THAN ONE active root segment, or this test proves nothing");

        // Overlaps a LATER segment only -- 2030 is inside the trailing remnant, and far outside the first.
        var result = await BuildService().AddOrgUnitDeclarationAsync(
            AddRequest("A12NEW", period: new EffectivePeriod(new DateOnly(2030, 1, 1), new DateOnly(2030, 12, 31))));

        result.IsError.Should().BeTrue("the proposed root overlaps a LATER active root segment");
        result.FirstError.Code.Should().Be("OrgUnit.RootPeriodOverlaps");
    }

    // Note: the Shell fake forwards Supplemental itself and hard-codes VersionOperationKind.Add, so the
    // ViewModel tests would stay green even if the REAL service dropped the supplemental or wrote the wrong
    // operation kind. Nothing else asserts either against a real database. Distinctive values throughout, so
    // a default cannot masquerade as a pass.
    [Fact]
    public async Task AddOrgUnitDeclarationAsync_PersistsEveryRequestFieldAndTheAddOperationKind()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync("A13PAR", "Đơn vị cha", "A13PAR", null, OpenFrom2020);
        var supplemental = new OrgUnitSupplementalDto(
            BusinessNumber: "0101234567", AddrLineVn: "12 Trần Hưng Đạo", AdminDivisionLevel: 3,
            NameFullEn: "Branch Thirteen", Phone: "02439330000", Email: "a13@example.test");

        var result = await BuildService().AddOrgUnitDeclarationAsync(
            new AddOrgUnitDeclarationRequest(
                Year2021, "A13CHI", "Chi nhánh mười ba", "CN13", parent, "lý do khai báo", supplemental));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var row = await ReadFullVersionRowAsync(result.Value.Write.NewVersionId);
        row.OrgCode.Should().Be("A13CHI");
        row.OrgNameFullVn.Should().Be("Chi nhánh mười ba");
        row.OrgNameShortVn.Should().Be("CN13");
        row.ParentId.Should().Be((ulong)parent);
        DateOnly.FromDateTime(row.EffectiveFrom).Should().Be(Year2021.From);
        DateOnly.FromDateTime(row.EffectiveTo).Should().Be(Year2021.To);
        row.RecordedBy.Should().Be(Actor, "the actor is derived server-side, never sent by the caller");
        row.Reason.Should().Be("lý do khai báo");
        row.OperationKind.Should().Be("Add", "the kind is Add by construction — the caller cannot supply it");
        row.BusinessNumber.Should().Be("0101234567");
        row.AddrLineVn.Should().Be("12 Trần Hưng Đạo");
        row.AdminDivisionLevel.Should().Be((sbyte)3);
        row.NameFullEn.Should().Be("Branch Thirteen");
        row.Phone.Should().Be("02439330000");
        row.Email.Should().Be("a13@example.test");
    }

    [Fact]
    public async Task AddOrgUnitDeclarationAsync_AuthorizationDenied_MintsNothing()
    {
        SkipUnlessDbAvailable();

        var denied = new FakeAuthorizationService(Error.Forbidden("Authz.NotGranted", "not granted"));
        var headersBefore = await CountAllHeaderRowsAsync();

        var result = await BuildService(authorization: denied).AddOrgUnitDeclarationAsync(AddRequest("A5DENY"));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Authz.NotGranted");
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore);
        (await CountAllAuditRowsAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(ScopeLevel.OwnOrgUnit)]
    [InlineData(ScopeLevel.Self)]
    public async Task AddOrgUnitDeclarationAsync_NonGlobalScope_MintsNothing(ScopeLevel level)
    {
        SkipUnlessDbAvailable();

        var narrow = new FakeAuthorizationService(new DataScope(level, 1, Actor));
        var headersBefore = await CountAllHeaderRowsAsync();

        var result = await BuildService(authorization: narrow).AddOrgUnitDeclarationAsync(AddRequest("A6SCOPE"));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.AddRequiresGlobalScope");
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore);
    }

    // §7 core: the first-version write is REJECTED after the header was already minted inside the
    // transaction. The rollback -- not a compensation -- is what leaves no row behind.
    [Fact]
    public async Task AddOrgUnitDeclarationAsync_CodeInUse_RollsBackTheMintedHeader()
    {
        SkipUnlessDbAvailable();

        // Deliberately a CHILD, not a root: a root Add would hit the root-overlap check first and this test
        // would pass while proving nothing about the code check or its rollback.
        var parent = await CreateOrgUnitAsync("A7PAR", "Đơn vị cha", "A7PAR", null, OpenFrom2020);
        await CreateOrgUnitAsync("A7TAKEN", "Đơn vị đã có", "A7TAKEN", parent, OpenFrom2020);
        var headersBefore = await CountAllHeaderRowsAsync();

        var result = await BuildService().AddOrgUnitDeclarationAsync(
            AddRequest("A7TAKEN", parent, new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd)));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.CodeInUse");
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore, "the header minted inside the transaction must roll back with it");
    }

    // Same §7 assertion on the temporal-FK branch: the parent does not cover the child's whole period.
    [Fact]
    public async Task AddOrgUnitDeclarationAsync_ParentGap_RollsBackTheMintedHeader()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync(
            "A8PAR", "Đơn vị cha", "A8PAR", null,
            new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 6, 30)));
        var headersBefore = await CountAllHeaderRowsAsync();

        var result = await BuildService().AddOrgUnitDeclarationAsync(
            AddRequest("A8CHI", parent, new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd)));

        // THE DISCRIMINATOR: pin the CODE, not just IsError. An authorization denial, a lock timeout or any
        // other pre-mint failure also leaves the header count unchanged, so IsError alone would let this test
        // pass without ever exercising the temporal-FK rollback it is named for.
        result.FirstError.Code.Should().Be("TemporalFk.ParentGap");
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore);
    }

    // C13's counterpart for Add. Strictly more than C13 can assert: Add creates
    // BOTH rows, so an audit failure must leave neither.
    [Fact]
    public async Task AddOrgUnitDeclarationAsync_AuditWriteFails_RollsBackHeaderAndVersion()
    {
        SkipUnlessDbAvailable();

        // A ROOT Add on purpose, so this composite Enlists nothing. With a parent, the mutation used to prove
        // this test discriminating (mint + write on their own connections, outside the composite) deadlocks
        // against the parent's own lock and dies as VersionedRepository.LockTimeout before it can leave a
        // surviving row — the test would then be red for the wrong reason and prove nothing.
        var headersBefore = await CountAllHeaderRowsAsync();

        var result = await BuildService(auditLog: new FailingAuditLogWriter())
            .AddOrgUnitDeclarationAsync(AddRequest("A9CHI"));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("AuditLog.Injected");
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore, "an unaudited org unit must not survive");
        (await CountVersionRowsByCodeAsync("A9CHI")).Should().Be(0);
    }

    // The requester's trimming rule (2026-08-17): the audit detail is a SNAPSHOT written once. A later
    // change to the org unit must not alter what was recorded -- history is preserved, never re-derived.
    [Fact]
    public async Task AddOrgUnitDeclarationAsync_AuditDetail_IsNotReDerivedByALaterEdit()
    {
        SkipUnlessDbAvailable();

        var added = await BuildService().AddOrgUnitDeclarationAsync(AddRequest("A10CODE"));
        added.IsError.Should().BeFalse(DescribeErrors(added.Errors));
        var target = $"org_unit_version:{added.Value.Write.NewVersionId}";
        var detailAtWriteTime = (await ReadAuditRowAsync(target)).Detail;

        // Note: pin the JSON CONTRACT itself, not only its stability. Without this, dropping
        // `parentId`/`note`, or re-adding the deliberately trimmed business fields, leaves every other
        // assertion in this test green — so the trimming decision would be recorded nowhere executable.
        var detail = JsonDocument.Parse(detailAtWriteTime!).RootElement;
        detail.EnumerateObject().Select(prop => prop.Name).Should().BeEquivalentTo(
            ["orgUnitId", "versionId", "parentId", "note"],
            "the Add audit detail carries POINTERS only — exactly these four, no more and no fewer");
        detail.GetProperty("orgUnitId").GetInt64().Should().Be(added.Value.OrgUnitId);
        detail.GetProperty("versionId").GetInt64().Should().Be(added.Value.Write.NewVersionId);
        detail.GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null, "A10CODE is a root");
        detail.GetProperty("note").GetString().Should().Be("khai báo mới");

        var edited = await OrgUnits.UpsertAsync(
            added.Value.OrgUnitId, OpenFrom2020, "A10REN", "Đơn vị đổi tên", "A10REN", null,
            VersionOperationKind.Edit, Actor, "đổi tên");
        edited.IsError.Should().BeFalse(DescribeErrors(edited.Errors));

        (await ReadAuditRowAsync(target)).Detail.Should().Be(
            detailAtWriteTime, "an audit row records what happened, and is never recomputed from current data");

        // THE LOAD-BEARING HALF. The assertion above alone is nearly tautological:
        // nothing in the codebase UPDATEs audit_log, so it would pass identically even if the detail still
        // copied org_code and the period. What actually makes the trim safe is the OTHER claim in
        // OrgUnitDeclarationService's comment -- that no later operation rewrites the Add version row's own
        // business columns, which is where those trimmed fields live. Pin that: the Add's version row still
        // reads its declared code and period after the edit above.
        var addedVersion = await ReadVersionRowAsync(added.Value.Write.NewVersionId);
        addedVersion.OrgCode.Should().Be("A10CODE", "the trimmed fields must still be recoverable from the version row this audit row points at");
        addedVersion.EffectiveFrom.Should().Be(OpenFrom2020.From);
        addedVersion.EffectiveTo.Should().Be(OpenFrom2020.To);
    }

    // =========================================================================================
    // E -- Edit (backlog 0.7): the parent of an org unit is IMMUTABLE after creation. The request
    // cannot express a desired parent at all, so there is no rejection branch a caller can probe;
    // the value written is the one read back under the identity lock.
    // =========================================================================================

    [Fact]
    public async Task EditOrgUnitDeclarationAsync_KeepsTheStoredParent_AndWritesTheEditedBusinessFields()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync("E1PAR", "Đơn vị cha", "Cha", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync("E1CHI", "Đơn vị con", "Con", parent, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(child, Today)).Value.Id;

        var request = new EditOrgUnitDeclarationRequest(
            child, versionId, parent, OpenFrom2020, "E1CHI", "Đơn vị con đổi tên", "Con", "rename", null);

        var result = await BuildService().EditOrgUnitDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var after = await ReadFullVersionRowAsync(result.Value.NewVersionId);
        after.ParentId.Should().Be((ulong)parent, "the parent of an org unit is immutable after creation");
        after.OrgNameFullVn.Should().Be("Đơn vị con đổi tên");
        after.OperationKind.Should().Be("Edit");
    }

    [Fact]
    public async Task EditOrgUnitDeclarationAsync_AuthorizationDenied_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("E2ORG", "Đơn vị", "E2ORG", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;
        var rowsBefore = await CountRowsAsync(org);

        var denied = new FakeAuthorizationService(Error.Forbidden("Authz.NotGranted", "Không được cấp quyền."));

        var result = await BuildService(authorization: denied).EditOrgUnitDeclarationAsync(
            new EditOrgUnitDeclarationRequest(
                org, versionId, null, OpenFrom2020, "E2ORG", "Đổi tên", "E2ORG", "rename", null));

        result.IsError.Should().BeTrue();
        // Same discriminator as the close path's C1: pins the authorizer's OWN code propagating through
        // unchanged, so a service that swallowed it and substituted its own Forbidden would still fail here.
        result.FirstError.Code.Should().Be("Authz.NotGranted");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRowsAsync(org)).Should().Be(rowsBefore);
        (await CountAllAuditRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EditOrgUnitDeclarationAsync_SelfScope_ReturnsScopeInsufficient_NeverThrows()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("E3ORG", "Đơn vị", "E3ORG", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var selfScope = new FakeAuthorizationService(new DataScope(ScopeLevel.Self, null, Actor));

        // Without the guard, IsWithinScopeAsync would throw InvalidOperationException here rather than
        // return an ErrorOr -- this call completing at all is part of the assertion.
        var result = await BuildService(authorization: selfScope).EditOrgUnitDeclarationAsync(
            new EditOrgUnitDeclarationRequest(
                org, versionId, null, OpenFrom2020, "E3ORG", "Đổi tên", "E3ORG", "rename", null));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Authz.ScopeInsufficient");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task EditOrgUnitDeclarationAsync_TargetOutsideScope_IsRejected_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("E4ORG", "Đơn vị", "E4ORG", null, OpenFrom2020);
        var otherOrg = await CreateOrgUnitAsync("E4OTH", "Đơn vị khác", "E4OTH", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;
        var rowsBefore = await CountRowsAsync(org);

        var narrowScope = new FakeAuthorizationService(new DataScope(ScopeLevel.OwnOrgUnit, otherOrg, Actor));

        var result = await BuildService(authorization: narrowScope).EditOrgUnitDeclarationAsync(
            new EditOrgUnitDeclarationRequest(
                org, versionId, null, OpenFrom2020, "E4ORG", "Đổi tên", "E4ORG", "rename", null));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.NotInScope");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRowsAsync(org)).Should().Be(rowsBefore);
    }

    [Fact]
    public async Task EditOrgUnitDeclarationAsync_ExpectedVersionIdIsNoLongerActive_ReturnsVersionNotFound()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("E5ORG", "Đơn vị", "E5ORG", null, OpenFrom2020);
        var supersededId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        // Case 7 (exact-period correction): soft-deactivates the row above and inserts a replacement, so
        // `supersededId` is a version the caller could plausibly still be holding on a stale card.
        var superseding = await OrgUnitRepo.UpsertAsync(
            org, OpenFrom2020, "E5ORG", "Đơn vị v2", "E5ORG", null, VersionOperationKind.Edit, Actor, "v2");
        superseding.IsError.Should().BeFalse(DescribeErrors(superseding.Errors));

        var result = await BuildService().EditOrgUnitDeclarationAsync(
            new EditOrgUnitDeclarationRequest(
                org, supersededId, null, OpenFrom2020, "E5ORG", "Đổi tên", "E5ORG", "rename", null));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.VersionNotFound");
        result.FirstError.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task EditOrgUnitDeclarationAsync_ExpectedParentDoesNotMatchStored_ReturnsParentMismatch()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync("E6PAR", "Đơn vị cha", "Cha", null, OpenFrom2020);
        var other = await CreateOrgUnitAsync("E6OTH", "Đơn vị cha khác", "Khác", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync("E6CHI", "Đơn vị con", "Con", parent, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(child, Today)).Value.Id;
        var rowsBefore = await CountRowsAsync(child);

        // The caller echoes a parent it did not read. There is no way to ASK for a re-parent, so this is
        // the only shape a stale or tampered echo can take -- and it must not be silently coerced.
        var result = await BuildService().EditOrgUnitDeclarationAsync(
            new EditOrgUnitDeclarationRequest(
                child, versionId, other, OpenFrom2020, "E6CHI", "Đổi tên", "Con", "rename", null));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.ParentMismatch");
        result.FirstError.Type.Should().Be(ErrorType.Conflict);
        (await CountRowsAsync(child)).Should().Be(rowsBefore);
    }

    // Note: the design assumes each identity carries ONE parent across its versions. The pre-0.7
    // writer did not guarantee that -- parent_id went per version on every Edit -- so this seeds the state
    // the guard cannot assume away, using the concrete repository the way that writer did.
    [Fact]
    public async Task EditOrgUnitDeclarationAsync_MixedParentHistory_ReturnsParentNotWellDefined()
    {
        SkipUnlessDbAvailable();

        var parentA = await CreateOrgUnitAsync("E7PRA", "Cha A", "Cha A", null, OpenFrom2020);
        var parentB = await CreateOrgUnitAsync("E7PRB", "Cha B", "Cha B", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync(
            "E7CHI", "Đơn vị con", "Con", parentA, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 6, 30)));

        // Adjacent, not overlapping (algebra case 2), so BOTH versions stay active -- one under parentA,
        // one under parentB. That is a mixed-parent history, and it is reachable in the tree today.
        var second = await OrgUnitRepo.UpsertAsync(
            child, new EffectivePeriod(new DateOnly(2020, 7, 1), EffectivePeriod.OpenEnd),
            "E7CHI", "Đơn vị con", "Con", parentB, VersionOperationKind.Edit, Actor, "re-parent");
        second.IsError.Should().BeFalse(DescribeErrors(second.Errors));

        // PIN THE FIXTURE, or this test can pass for the wrong reason: if the seed had left ONE active
        // version (under parentA), the echo below would mismatch and the same code would come back --
        // proving nothing about mixed history. Assert the state actually exists before acting on it.
        (await ReadDistinctActiveParentIdsAsync(child)).Should().HaveCount(
            2, "the fixture must actually produce a mixed-parent history for this test to mean anything");

        var versionId = (await OrgUnits.GetByIdentityAsync(child, Today)).Value.Id;
        var rowsBefore = await CountRowsAsync(child);

        var result = await BuildService().EditOrgUnitDeclarationAsync(
            new EditOrgUnitDeclarationRequest(
                child, versionId, parentB, new EffectivePeriod(new DateOnly(2020, 7, 1), EffectivePeriod.OpenEnd),
                "E7CHI", "Đổi tên", "Con", "rename", null));

        result.IsError.Should().BeTrue();
        // Rejected even though the echo MATCHES the version the caller loaded -- so this cannot be the
        // stale-echo branch. Until 2026-08-21 both branches returned OrgUnit.ParentMismatch and this
        // assertion passed with the well-definedness guard deleted, because GetActiveParentIdsAsync has no
        // ORDER BY and storedParents[0] may be the OTHER parent. Its own code now.
        result.FirstError.Code.Should().Be("OrgUnit.ParentNotWellDefined");
        (await CountRowsAsync(child)).Should().Be(rowsBefore);
    }

    [Fact]
    public async Task EditOrgUnitDeclarationAsync_AuditWriteFails_RollsBackTheVersionWrite()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("E8ORG", "Đơn vị", "E8ORG", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;
        var rowsBefore = await CountRowsAsync(org);

        var result = await BuildService(auditLog: new FailingAuditLogWriter()).EditOrgUnitDeclarationAsync(
            new EditOrgUnitDeclarationRequest(
                org, versionId, null, Year2021, "E8ORG", "Đổi tên", "E8ORG", "rename", null));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("AuditLog.Injected");
        (await CountRowsAsync(org)).Should().Be(rowsBefore, "the version write and its audit row share one transaction");
        (await CountAllAuditRowsAsync()).Should().Be(0);
    }

    // =========================================================================================
    // R -- Root close (backlog 0.8): a root org unit may NOT be closed. The one carve-out is the
    // break-glass rescuer, who performs the close under the unit's NORMAL rules -- without it a
    // mis-declared root would have no in-app remedy at all (requester ruling 2026-08-21).
    //
    // Both sides are tested. A test that only proves the block is the shape that lets a carve-out
    // ship unreachable.
    // =========================================================================================

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_RootOrgUnit_OrdinaryActor_IsRejected_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R1ROOT", "Đơn vị gốc", "R1ROOT", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(root, Today)).Value.Id;
        var rowsBefore = await CountRowsAsync(root);

        var result = await BuildService().CloseOrgUnitDeclarationAsync(
            new CloseOrgUnitDeclarationRequest(root, versionId, Today, "retire"));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.RootNotClosable");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRowsAsync(root)).Should().Be(rowsBefore);
        (await CountAllAuditRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_RootOrgUnit_BreakGlassActor_Succeeds_WritesTwoAuditRows()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R2ROOT", "Đơn vị gốc", "R2ROOT", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(root, Today)).Value.Id;

        var result = await BuildService(breakGlass: new FakeBreakGlassPolicy(Actor))
            .CloseOrgUnitDeclarationAsync(new CloseOrgUnitDeclarationRequest(root, versionId, Today, "rescue"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var closed = await OrgUnits.GetByIdentityAsync(root, Today);
        closed.Value.EffectiveTo.Should().Be(Today);

        // TWO rows under the CURRENT sequencing: the ordinary retire row, plus a
        // security-specific one recording that a normally-forbidden operation was performed under
        // break-glass. Both on the same transaction as the version write.
        var target = $"org_unit_version:{versionId}";
        (await CountAuditRowsForTargetAsync(target)).Should().Be(2);
        (await ReadAuditEventTypesForTargetAsync(target)).Should().BeEquivalentTo(
            ["orgunit-close", "orgunit-root-close-breakglass"]);
    }

    // F-57. The gate and the break-glass audit row USED to be two independent
    // IBreakGlassPolicy reads, and RealBreakGlassPolicy re-reads File B on every call (it holds no cached
    // answer -- AST.Core/Iam/RealBreakGlassPolicy.cs). So a store that changed, was tampered with, or
    // simply failed to read between the two calls let the close COMMIT on the first `true` while the audit
    // selection saw `false`: the normally-forbidden operation succeeded and the one row that records it
    // was silently omitted. A security review would then have to infer break-glass from the unit's parent
    // instead of finding the row by querying for it.
    //
    // The policy here answers `true` once and `false` forever after, which is the exact sequence that used
    // to produce the missing row. TWO assertions, deliberately:
    //   - the CALL COUNT is the direct discriminator -- one authorization outcome must govern both uses,
    //     and a re-read that happened to agree would still be the defect;
    //   - the audit rows are the OUTCOME the count exists to protect, and would go on failing if the fix
    //     were ever undone by caching in the wrong place.
    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_RootBreakGlass_ReadsThePolicyOnce_SoAChangingStoreCannotDropTheAuditRow()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R7ROOT", "Đơn vị gốc", "R7ROOT", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(root, Today)).Value.Id;

        var policy = new SequenceChangingBreakGlassPolicy(Actor);

        var result = await BuildService(breakGlass: policy)
            .CloseOrgUnitDeclarationAsync(new CloseOrgUnitDeclarationRequest(root, versionId, Today, "rescue"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        policy.CallCount.Should().Be(
            1,
            "the gate and the break-glass audit selection must rest on ONE authorization outcome; a second "
            + "read can disagree with the first and drop the orgunit-root-close-breakglass row");

        var target = $"org_unit_version:{versionId}";
        (await ReadAuditEventTypesForTargetAsync(target)).Should().BeEquivalentTo(
            ["orgunit-close", "orgunit-root-close-breakglass"],
            "the break-glass row must survive a policy store that stops recognising the actor mid-operation");
    }

    // G-24: every root test above uses the RETIRE branch. The cancel-plan branch is the one the VM half's
    // argument rests on -- a bare `!IsRoot` on CanClose disables the only button that reaches this service,
    // and the service derives close-vs-cancel itself, so it blocks cancelling a root plan too. Nothing
    // exercised that branch until these two.
    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_CancelRootPlan_OrdinaryActor_IsRejected_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R5ROOT", "Đơn vị gốc", "R5ROOT", null, OpenFrom2020);
        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var future = await OrgUnitRepo.UpsertAsync(
            root, futurePlan, "R5ROOT", "Kế hoạch gốc 2027", "R5ROOT", null, VersionOperationKind.Edit, Actor, "plan");
        future.IsError.Should().BeFalse(DescribeErrors(future.Errors));
        var rowsBefore = await CountRowsAsync(root);

        var result = await BuildService().CloseOrgUnitDeclarationAsync(
            new CloseOrgUnitDeclarationRequest(root, future.Value.NewVersionId, EffectiveThrough: null, "cancel"));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.RootNotClosable");
        (await CountRowsAsync(root)).Should().Be(rowsBefore);
        (await CountAllAuditRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_CancelRootPlan_BreakGlassActor_Succeeds_WritesTwoAuditRows()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R6ROOT", "Đơn vị gốc", "R6ROOT", null, OpenFrom2020);
        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var future = await OrgUnitRepo.UpsertAsync(
            root, futurePlan, "R6ROOT", "Kế hoạch gốc 2027", "R6ROOT", null, VersionOperationKind.Edit, Actor, "plan");
        future.IsError.Should().BeFalse(DescribeErrors(future.Errors));
        var versionId = future.Value.NewVersionId;

        var result = await BuildService(breakGlass: new FakeBreakGlassPolicy(Actor))
            .CloseOrgUnitDeclarationAsync(
                new CloseOrgUnitDeclarationRequest(root, versionId, EffectiveThrough: null, "rescue"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(new DataScope(ScopeLevel.Global, null, Actor), root);
        var cancelled = history.Should().ContainSingle(h => h.Id == versionId).Subject;
        cancelled.Cancelled.Should().BeTrue();

        var target = $"org_unit_version:{versionId}";
        (await ReadAuditEventTypesForTargetAsync(target)).Should().BeEquivalentTo(
            ["orgunit-close", "orgunit-root-close-breakglass"],
            "the security row rides the cancel branch exactly as it does the retire branch");
    }

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_NonRootOrgUnit_OrdinaryActor_StillSucceeds()
    {
        SkipUnlessDbAvailable();

        // THE DISCRIMINATOR: proves the new gate keys on root-ness, not on closing in general -- a
        // blanket block would pass every other assertion in this section.
        var parent = await CreateOrgUnitAsync("R3PAR", "Đơn vị cha", "R3PAR", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync("R3CHI", "Đơn vị con", "R3CHI", parent, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(child, Today)).Value.Id;

        var result = await BuildService().CloseOrgUnitDeclarationAsync(
            new CloseOrgUnitDeclarationRequest(child, versionId, Today, "retire"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
    }

    [Fact]
    public async Task CloseOrgUnitDeclarationAsync_RootOrgUnit_BreakGlassActor_IsStillSubjectToVersionCloseRules()
    {
        SkipUnlessDbAvailable();

        // The ruling is that break-glass performs the close under the unit's NORMAL rules -- it is a
        // carve-out from "a root may not be closed", NOT a carve-out from the close-date rules.
        var root = await CreateOrgUnitAsync("R4ROOT", "Đơn vị gốc", "R4ROOT", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(root, Today)).Value.Id;
        var rowsBefore = await CountRowsAsync(root);

        var result = await BuildService(breakGlass: new FakeBreakGlassPolicy(Actor))
            .CloseOrgUnitDeclarationAsync(
                new CloseOrgUnitDeclarationRequest(root, versionId, Today.AddDays(-5), "rescue"));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateInPast);
        (await CountRowsAsync(root)).Should().Be(rowsBefore);
    }

    // ---------------------------------------------------------------------------------------

    private async Task<long> CountHeaderRowsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM org_unit WHERE id = @orgUnitId", new { orgUnitId });
    }

    // The §7 assertion is about ORPHANS, which have no version row to find them by -- so the only way to see
    // one is to count the header table itself, before and after.
    private async Task<long> CountAllHeaderRowsAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM org_unit");
    }

    private async Task<long> CountActiveRootRowsAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM org_unit_version WHERE isactive = 1 AND parent_id IS NULL");
    }

    private async Task<OrgUnitVersionRow> ReadFullVersionRowAsync(long versionId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<OrgUnitVersionRow>(
            """
            SELECT org_code AS OrgCode, org_name_full_vn AS OrgNameFullVn, org_name_short_vn AS OrgNameShortVn,
                   parent_id AS ParentId, effective_from AS EffectiveFrom, effective_to AS EffectiveTo,
                   recorded_by AS RecordedBy, reason AS Reason, operation_kind AS OperationKind,
                   org_business_number AS BusinessNumber, org_addr_line_vn AS AddrLineVn,
                   org_admin_division_level AS AdminDivisionLevel, org_name_full_en AS NameFullEn,
                   org_phone AS Phone, org_email AS Email
            FROM org_unit_version WHERE id = @versionId
            """,
            new { versionId });
    }

    // Settable properties, not a positional record: this raw MySqlConnection has none of AST.Infrastructure's
    // Dapper type handlers registered, so the column CLR types are the driver's own (UInt64 for BIGINT
    // UNSIGNED, DateTime for DATE, SByte for TINYINT) and a positional record cannot be materialized.
    private sealed class OrgUnitVersionRow
    {
        public string OrgCode { get; set; } = "";
        public string OrgNameFullVn { get; set; } = "";
        public string OrgNameShortVn { get; set; } = "";
        public ulong? ParentId { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public DateTime EffectiveTo { get; set; }
        public string RecordedBy { get; set; } = "";
        public string? Reason { get; set; }
        public string? OperationKind { get; set; }
        public string? BusinessNumber { get; set; }
        public string? AddrLineVn { get; set; }
        public sbyte AdminDivisionLevel { get; set; }
        public string? NameFullEn { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
    }

    private async Task<long> CountInactiveRootRowsAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM org_unit_version WHERE isactive = 0 AND parent_id IS NULL");
    }

    private async Task<(string OrgCode, DateOnly EffectiveFrom, DateOnly EffectiveTo)> ReadVersionRowAsync(long versionId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<(string, DateOnly, DateOnly)>(
            "SELECT org_code, effective_from, effective_to FROM org_unit_version WHERE id = @versionId",
            new { versionId });
    }

    private async Task<long> CountVersionRowsByCodeAsync(string orgCode)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM org_unit_version WHERE org_code = @orgCode", new { orgCode });
    }

    private sealed class FakeAuthorizationService(ErrorOr<DataScope> outcome) : IAuthorizationService
    {
        public Task<ErrorOr<DataScope>> AuthorizeAsync(string username, string functionKey) => Task.FromResult(outcome);
        public Task<bool> IsFunctionOpenAsync(string username, string functionKey) => Task.FromResult(!outcome.IsError);
    }

    private sealed class FakeCurrentWindowsUser(string? username) : ICurrentWindowsUser
    {
        public string? Username => username;
    }

    // Same hand-rolled shape as AuthorizationServiceTests/RoleDeclarationServiceTests use, per class --
    // IBreakGlassPolicy is a non-persistence seam, so a fake is allowed here (rule-testing invariant 1)
    // and no shared fixture is extracted for a four-line type.
    private sealed class FakeBreakGlassPolicy(params string[] admins) : IBreakGlassPolicy
    {
        private readonly HashSet<string> _admins = new(admins, StringComparer.Ordinal);
        public bool IsBreakGlassAdmin(string username) => _admins.Contains(username);
    }

    // THE DISCRIMINATOR for F-57: recognises the actor on the FIRST call only, then stops -- standing in
    // for a File B that is edited, tampered with, or becomes unreadable mid-operation (RealBreakGlassPolicy
    // fails closed to `false` on a store error, so "stops recognising" is the realistic failure, not an
    // exotic one). CallCount is what the test asserts on; the flip is what makes a second read visible in
    // the audit rows too, so the test fails twice over rather than on a bare count.
    private sealed class SequenceChangingBreakGlassPolicy(params string[] admins) : IBreakGlassPolicy
    {
        private readonly HashSet<string> _admins = new(admins, StringComparer.Ordinal);

        public int CallCount { get; private set; }

        public bool IsBreakGlassAdmin(string username)
        {
            CallCount++;
            return CallCount == 1 && _admins.Contains(username);
        }
    }

    // THE DISCRIMINATOR for C13: an audit writer that always fails, without touching persistence itself --
    // proves the composite's rollback (not this fake) is what leaves the version row unchanged.
    private sealed class FailingAuditLogWriter : IAuditLogWriter
    {
        public Task<ErrorOr<Success>> WriteAsync(
            AuditLogEntry entry, System.Data.IDbTransaction transaction, CancellationToken cancellationToken = default) =>
            Task.FromResult<ErrorOr<Success>>(Error.Failure("AuditLog.Injected", "Simulated audit write failure."));
    }

    private async Task<long> CountRowsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM org_unit_version WHERE org_unit_id = @orgUnitId", new { orgUnitId });
    }

    // Reads the same set the service's own guard reads, but on its own connection -- so a fixture can be
    // pinned without borrowing the code under test to describe the state it was seeded into.
    private async Task<IReadOnlyList<ulong?>> ReadDistinctActiveParentIdsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<ulong?>(
            "SELECT DISTINCT parent_id FROM org_unit_version WHERE org_unit_id = @orgUnitId AND isactive = 1",
            new { orgUnitId });
        return rows.ToList();
    }

    private async Task<long> CountAllAuditRowsAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM audit_log");
    }

    private async Task<long> CountAuditRowsForTargetAsync(string target)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM audit_log WHERE target = @target", new { target });
    }

    // ReadAuditRowAsync below takes LIMIT 1, which cannot see a second row -- and "a second row exists"
    // is exactly what the break-glass branch has to prove.
    private async Task<IReadOnlyList<string>> ReadAuditEventTypesForTargetAsync(string target)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<string>(
            "SELECT event_type FROM audit_log WHERE target = @target", new { target });
        return rows.ToList();
    }

    private async Task<(string EventType, string? Username, string? Detail)> ReadAuditRowAsync(string target)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<(string, string?, string?)>(
            "SELECT event_type, username, detail FROM audit_log WHERE target = @target LIMIT 1", new { target });
    }
}
