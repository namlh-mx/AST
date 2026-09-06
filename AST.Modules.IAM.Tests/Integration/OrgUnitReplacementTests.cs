using System.Text.Json;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using ErrorOr;
using FluentAssertions;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// Pins ReplaceOrgUnitDeclarationAsync (card 232 / plan 2026-09-04). Real MySQL; non-persistence seams
// are hand-rolled fakes, never Moq. Until Task 4 replaces the stub, every service-path test fails with
// NotImplementedException — that is the deliverable, not a behavioural defect. Test 19 asserts the
// database CHECK and must pass already.
public sealed class OrgUnitReplacementTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod Year2030 =
        new(new DateOnly(2030, 1, 1), new DateOnly(2030, 12, 31));

    // R1 — OrgUnit.PredecessorNotEmpty description for BOTH reverse-FK edges. No numeric count.
    private const string PredecessorNotEmptyDescription =
        "Đơn vị còn tham số phụ thuộc, người dùng cần xử lý tham số phụ thuộc trước khi thực hiện thao tác.";

    private const string Actor = "tester";

    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private OrgUnitRepository OrgUnitRepo => (OrgUnitRepository)OrgUnits;

    private OrgUnitDeclarationService BuildService(
        IAuthorizationService? authorization = null,
        IAuditLogWriter? auditLog = null,
        string actor = Actor,
        IBreakGlassPolicy? breakGlass = null) =>
        new(
            OrgUnitRepo,
            Connections,
            auditLog ?? new AST.Infrastructure.AuditLogWriter(),
            authorization ?? new FakeAuthorizationService(new DataScope(ScopeLevel.Global, null, actor)),
            new FakeCurrentWindowsUser(actor),
            new FixedBusinessDateProvider(Today),
            breakGlass ?? new FakeBreakGlassPolicy(),
            new PeriodEditor());

    private OrgUnitDeclarationService BuildBreakGlassService(IAuditLogWriter? auditLog = null) =>
        BuildService(auditLog: auditLog, breakGlass: new FakeBreakGlassPolicy(Actor));

    private ReplaceOrgUnitDeclarationRequest ReplaceRequest(
        long predecessorId,
        string orgCode,
        long? parentId,
        EffectivePeriod? period = null,
        string? reason = "thay thế",
        OrgUnitSupplementalDto? supplemental = null,
        string fullVn = "Đơn vị kế thừa",
        string shortVn = "KT") =>
        new(
            predecessorId,
            period ?? OpenFrom2020,
            orgCode,
            parentId,
            fullVn,
            shortVn,
            reason,
            supplemental);

    // =========================================================================================
    // Pre-transaction refusals — authz / Global scope (card 241 / F-240-01). Same shape as Add.
    // =========================================================================================

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_AuthorizationDenied_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var parent = await AddRootAsync("RAUTHPAR", OpenFrom2020);
        var predecessor = await AddChildAsync("RAUTHOLD", parent, OpenFrom2020);
        var before = await ReadAllVersionRowsAsync(predecessor);
        var headersBefore = await CountAllHeaderRowsAsync();
        var auditsBefore = await SnapshotAuditAsync();

        var denied = new FakeAuthorizationService(Error.Forbidden("Authz.NotGranted", "Không được cấp quyền."));

        var result = await BuildService(authorization: denied).ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "RAUTHNEW", parent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Authz.NotGranted");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        denied.LastFunctionKey.Should().Be("Iam.OrgUnit.Declare");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            before, opts => opts.WithStrictOrdering());
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore);
        (await AuditDeltaAsync(auditsBefore)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ScopeLevel.OwnOrgUnit, "OU")]
    [InlineData(ScopeLevel.OwnOrgUnitAndDescendants, "OD")]
    [InlineData(ScopeLevel.Self, "SF")]
    public async Task ReplaceOrgUnitDeclarationAsync_NonGlobalScope_WritesNothing(ScopeLevel level, string tag)
    {
        SkipUnlessDbAvailable();

        var parent = await AddRootAsync($"RS{tag}PAR", OpenFrom2020);
        var predecessor = await AddChildAsync($"RS{tag}OLD", parent, OpenFrom2020);
        var before = await ReadAllVersionRowsAsync(predecessor);
        var headersBefore = await CountAllHeaderRowsAsync();
        var auditsBefore = await SnapshotAuditAsync();

        var narrow = new FakeAuthorizationService(new DataScope(level, predecessor, Actor));

        var result = await BuildService(authorization: narrow).ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, $"RS{tag}NEW", parent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.ReplaceRequiresGlobalScope");
        narrow.LastFunctionKey.Should().Be("Iam.OrgUnit.Declare");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            before, opts => opts.WithStrictOrdering());
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore);
        (await AuditDeltaAsync(auditsBefore)).Should().BeEmpty();
    }

    // =========================================================================================
    // Shape tests (1–7, 14, 22) — succeed under a correct implementation; stub → NIE until Task 4.
    // Parent coverage is stated in each fixture (R4). Audit assertions use deltas (R5).
    // =========================================================================================

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_OneActiveRow_WritesExactReplacementShape()
    {
        SkipUnlessDbAvailable();

        // Parent covers OpenFrom2020 (successor period).
        var parent = await AddRootAsync("R1PAR", OpenFrom2020);
        var supplemental = new OrgUnitSupplementalDto(
            BusinessNumber: "0101999001",
            AddrLineVn: "1 Lý Thường Kiệt",
            AddrLineEn: "1 Ly Thuong Kiet",
            AddrWardVn: "Phường Cửa Nam",
            AddrWardEn: "Cua Nam Ward",
            AddrDistrictVn: "Quận Hoàn Kiếm",
            AddrDistrictEn: "Hoan Kiem District",
            AddrProvinceVn: "Hà Nội",
            AddrProvinceEn: "Hanoi",
            AdminDivisionLevel: 3,
            NameFullEn: "Unit One",
            NameShortEn: "U1",
            Phone: "02411110001",
            Fax: "02411119901",
            Email: "r1@example.test");
        var predecessor = await AddChildAsync(
            "R1OLD", parent, OpenFrom2020, "Đơn vị cũ một", "Cũ1", "khai báo", supplemental);

        var before = (await ReadAllVersionRowsAsync(predecessor)).Should().ContainSingle().Subject;

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(
                predecessor,
                "R1NEW",
                parent,
                reason: "đổi sang đơn vị mới",
                supplemental: new OrgUnitSupplementalDto(
                    BusinessNumber: "0101999002",
                    AddrLineVn: "2 Lý Thường Kiệt",
                    AdminDivisionLevel: 2,
                    NameFullEn: "Unit New",
                    Phone: "02411110002",
                    Email: "r1new@example.test"),
                fullVn: "Đơn vị mới một",
                shortVn: "Mới1"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var predRows = await ReadAllVersionRowsAsync(predecessor);
        var marked = predRows.Should().ContainSingle().Subject;
        marked.IsActive.Should().BeFalse();
        marked.Status.Should().Be("replaced");
        marked.ReplacedByOrgUnitId.Should().Be((ulong)result.Value.OrgUnitId);
        marked.OperationKind.Should().Be(before.OperationKind);
        marked.OrgCode.Should().Be(before.OrgCode);
        marked.OrgNameFullVn.Should().Be(before.OrgNameFullVn);
        marked.OrgNameShortVn.Should().Be(before.OrgNameShortVn);
        marked.ParentId.Should().Be(before.ParentId);
        DateOnly.FromDateTime(marked.EffectiveFrom).Should().Be(DateOnly.FromDateTime(before.EffectiveFrom));
        DateOnly.FromDateTime(marked.EffectiveTo).Should().Be(DateOnly.FromDateTime(before.EffectiveTo));
        marked.BusinessNumber.Should().Be(before.BusinessNumber);
        marked.AddrLineVn.Should().Be(before.AddrLineVn);
        marked.AddrLineEn.Should().Be(before.AddrLineEn);
        marked.AddrWardVn.Should().Be(before.AddrWardVn);
        marked.AddrWardEn.Should().Be(before.AddrWardEn);
        marked.AddrDistrictVn.Should().Be(before.AddrDistrictVn);
        marked.AddrDistrictEn.Should().Be(before.AddrDistrictEn);
        marked.AddrProvinceVn.Should().Be(before.AddrProvinceVn);
        marked.AddrProvinceEn.Should().Be(before.AddrProvinceEn);
        marked.AdminDivisionLevel.Should().Be(before.AdminDivisionLevel);
        marked.NameFullEn.Should().Be(before.NameFullEn);
        marked.NameShortEn.Should().Be(before.NameShortEn);
        marked.Phone.Should().Be(before.Phone);
        marked.Fax.Should().Be(before.Fax);
        marked.Email.Should().Be(before.Email);
        marked.RecordedBy.Should().Be(before.RecordedBy);
        marked.Reason.Should().Be(before.Reason);

        var succRows = await ReadAllVersionRowsAsync(result.Value.OrgUnitId);
        var succ = succRows.Should().ContainSingle().Subject;
        succ.IsActive.Should().BeTrue();
        succ.Status.Should().Be("normal");
        succ.ReplacedByOrgUnitId.Should().BeNull();
        succ.OperationKind.Should().Be("Replace");
        succ.OrgCode.Should().Be("R1NEW");
        succ.OrgNameFullVn.Should().Be("Đơn vị mới một");
        succ.OrgNameShortVn.Should().Be("Mới1");
        succ.ParentId.Should().Be((ulong)parent);
        succ.BusinessNumber.Should().Be("0101999002");
        succ.AddrLineVn.Should().Be("2 Lý Thường Kiệt");
        succ.AdminDivisionLevel.Should().Be((sbyte)2);
        succ.NameFullEn.Should().Be("Unit New");
        succ.Phone.Should().Be("02411110002");
        succ.Email.Should().Be("r1new@example.test");
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_ThreeActiveRows_MarksEveryRowAndLeavesNoActivePredecessor()
    {
        SkipUnlessDbAvailable();

        // Parent covers [2020-01-01, open] — all three predecessor slices and the successor.
        var parent = await AddRootAsync("R2PAR", OpenFrom2020);
        var predecessor = await SeedThreeActiveNonOverlappingAsync(parent, "R2OLD");

        var before = await ReadAllVersionRowsAsync(predecessor);
        var activeIdsBefore = before.Where(r => r.IsActive).Select(r => r.Id).ToList();
        activeIdsBefore.Should().HaveCount(3);
        // The helper soft-deactivates its own Add row, so the predecessor also carries one ordinary
        // superseded remnant. It must survive untouched — the same rule test 22 pins.
        var remnantId = before.Should().ContainSingle(r => !r.IsActive).Subject.Id;

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R2NEW", parent));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var rows = await ReadAllVersionRowsAsync(predecessor);
        rows.Count(r => r.IsActive).Should().Be(0);

        var marked = rows.Where(r => activeIdsBefore.Contains(r.Id)).ToList();
        marked.Should().HaveCount(3);
        marked.Should().OnlyContain(r =>
            r.Status == "replaced" && r.ReplacedByOrgUnitId == (ulong)result.Value.OrgUnitId);

        var remnant = rows.Single(r => r.Id == remnantId);
        remnant.Status.Should().Be("normal");
        remnant.ReplacedByOrgUnitId.Should().BeNull();
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_PredecessorKindsAndBusinessColumns_AreNeverRewritten()
    {
        SkipUnlessDbAvailable();

        // Parent covers [2020-01-01, open].
        var parent = await AddRootAsync("R3PAR", OpenFrom2020);
        var predecessor = await SeedAddEditCloseActiveTrioAsync(parent, "R3OLD");
        var before = (await ReadAllVersionRowsAsync(predecessor))
            .OrderBy(r => r.Id)
            .ToDictionary(r => r.Id);

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R3NEW", parent));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        foreach (var after in await ReadAllVersionRowsAsync(predecessor))
        {
            var snap = before[after.Id];
            after.OperationKind.Should().Be(snap.OperationKind);
            after.OrgCode.Should().Be(snap.OrgCode);
            after.OrgNameFullVn.Should().Be(snap.OrgNameFullVn);
            after.OrgNameShortVn.Should().Be(snap.OrgNameShortVn);
            after.ParentId.Should().Be(snap.ParentId);
            after.BusinessNumber.Should().Be(snap.BusinessNumber);
            after.AddrLineVn.Should().Be(snap.AddrLineVn);
            after.AddrLineEn.Should().Be(snap.AddrLineEn);
            after.AddrWardVn.Should().Be(snap.AddrWardVn);
            after.AddrWardEn.Should().Be(snap.AddrWardEn);
            after.AddrDistrictVn.Should().Be(snap.AddrDistrictVn);
            after.AddrDistrictEn.Should().Be(snap.AddrDistrictEn);
            after.AddrProvinceVn.Should().Be(snap.AddrProvinceVn);
            after.AddrProvinceEn.Should().Be(snap.AddrProvinceEn);
            after.AdminDivisionLevel.Should().Be(snap.AdminDivisionLevel);
            after.NameFullEn.Should().Be(snap.NameFullEn);
            after.NameShortEn.Should().Be(snap.NameShortEn);
            after.Phone.Should().Be(snap.Phone);
            after.Fax.Should().Be(snap.Fax);
            after.Email.Should().Be(snap.Email);
            after.RecordedBy.Should().Be(snap.RecordedBy);
            after.Reason.Should().Be(snap.Reason);
            DateOnly.FromDateTime(after.EffectiveFrom).Should().Be(DateOnly.FromDateTime(snap.EffectiveFrom));
            DateOnly.FromDateTime(after.EffectiveTo).Should().Be(DateOnly.FromDateTime(snap.EffectiveTo));
        }
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_DifferentSuppliedParent_DeclaresSuccessorUnderNewParent()
    {
        SkipUnlessDbAvailable();

        var parentA = await AddRootAsync("R4PA", OpenFrom2020);
        // Both parents are legal and reachable: parentB is a sibling of the predecessor under parentA, and
        // covers OpenFrom2020 (R4).
        var parentB = await AddChildAsync("R4PB", parentA, OpenFrom2020);
        var predecessor = await AddChildAsync("R4OLD", parentA, OpenFrom2020);

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R4NEW", parentB));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var pred = (await ReadAllVersionRowsAsync(predecessor)).Should().ContainSingle().Subject;
        pred.ParentId.Should().Be((ulong)parentA);

        var succ = (await ReadAllVersionRowsAsync(result.Value.OrgUnitId)).Should().ContainSingle().Subject;
        succ.ParentId.Should().Be((ulong)parentB);
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_SameSuppliedParent_KeepsSuccessorUnderThatParent()
    {
        SkipUnlessDbAvailable();

        // Parent A covers OpenFrom2020.
        var parentA = await AddRootAsync("R5PA", OpenFrom2020);
        var predecessor = await AddChildAsync("R5OLD", parentA, OpenFrom2020);

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R5NEW", parentA));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var pred = (await ReadAllVersionRowsAsync(predecessor)).Should().ContainSingle().Subject;
        pred.ParentId.Should().Be((ulong)parentA);
        var succ = (await ReadAllVersionRowsAsync(result.Value.OrgUnitId)).Should().ContainSingle().Subject;
        succ.ParentId.Should().Be((ulong)parentA);
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_DifferentCodeAndUnrelatedPeriod_Succeeds()
    {
        SkipUnlessDbAvailable();

        // Parent must cover the successor's 2030 span (R4) — OpenFrom2020 does.
        var parent = await AddRootAsync("R6PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("OLD100", parent, OpenFrom2020, "Cũ 100", "OLD100");

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "NEW100", parent, Year2030, fullVn: "Mới 100", shortVn: "NEW100"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var pred = (await ReadAllVersionRowsAsync(predecessor)).Should().ContainSingle().Subject;
        pred.OrgCode.Should().Be("OLD100");
        DateOnly.FromDateTime(pred.EffectiveFrom).Should().Be(new DateOnly(2020, 1, 1));
        DateOnly.FromDateTime(pred.EffectiveTo).Should().Be(EffectivePeriod.OpenEnd);

        var succ = (await ReadAllVersionRowsAsync(result.Value.OrgUnitId)).Should().ContainSingle().Subject;
        succ.OrgCode.Should().Be("NEW100");
        DateOnly.FromDateTime(succ.EffectiveFrom).Should().Be(new DateOnly(2030, 1, 1));
        DateOnly.FromDateTime(succ.EffectiveTo).Should().Be(new DateOnly(2030, 12, 31));
        succ.OperationKind.Should().Be("Replace");
    }

    // Criterion 6 only (R6). Criterion 12 cannot be discharged by a green same-code success — an
    // exemption would also pass. Task 4 proves FindCodeInUseAsync unchanged by diff.
    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_SameCodeWithoutThirdHolder_SucceedsAndIsTheP6OrderingControl()
    {
        SkipUnlessDbAvailable();

        // Parent covers OpenFrom2020 (overlapping same-code successor period).
        var parent = await AddRootAsync("R7PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("SAME07", parent, OpenFrom2020);

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "SAME07", parent));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        result.Errors.Should().NotContain(e => e.Code == "OrgUnit.CodeInUse");

        var pred = (await ReadAllVersionRowsAsync(predecessor)).Should().ContainSingle().Subject;
        pred.IsActive.Should().BeFalse();
        pred.Status.Should().Be("replaced");

        var succ = (await ReadAllVersionRowsAsync(result.Value.OrgUnitId)).Should().ContainSingle().Subject;
        succ.OrgCode.Should().Be("SAME07");
        succ.OperationKind.Should().Be("Replace");
        succ.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_OnlyInactiveDependents_DoNotBlock()
    {
        SkipUnlessDbAvailable();

        // Parent covers OpenFrom2020.
        var parent = await AddRootAsync("R14PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("R14OLD", parent, OpenFrom2020);
        var child = await AddChildAsync("R14CHI", predecessor, OpenFrom2020);
        await SoftDeactivateAllVersionsAsync("org_unit_version", "org_unit_id", child);

        var role = await CreateRoleAsync("R14ROLE", "Vai trò R14", OpenFrom2020);
        var user = await CreateUserHeaderAsync();
        var seeded = await Users.UpsertAsync(
            user, OpenFrom2020, "r14user", "Người dùng R14", predecessor, role, Actor, "seed");
        seeded.IsError.Should().BeFalse(DescribeErrors(seeded.Errors));
        await SoftDeactivateAllVersionsAsync("user_version", "user_id", user);

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R14NEW", parent));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var pred = (await ReadAllVersionRowsAsync(predecessor)).Should().ContainSingle().Subject;
        pred.IsActive.Should().BeFalse();
        pred.Status.Should().Be("replaced");
        pred.ReplacedByOrgUnitId.Should().Be((ulong)result.Value.OrgUnitId);

        var succ = (await ReadAllVersionRowsAsync(result.Value.OrgUnitId)).Should().ContainSingle().Subject;
        succ.OperationKind.Should().Be("Replace");
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_PredecessorHasAlreadyInactiveRows_LeavesThemUntouched()
    {
        SkipUnlessDbAvailable();

        // Parent covers [2020-01-01, open]. Two active slices + one ordinary superseded (prior Sửa) row.
        var parent = await AddRootAsync("R22PAR", OpenFrom2020);
        var p1 = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));
        var p2 = new EffectivePeriod(new DateOnly(2021, 1, 1), EffectivePeriod.OpenEnd);

        var add = await BuildService().AddOrgUnitDeclarationAsync(
            new AddOrgUnitDeclarationRequest(
                p1, "R22OLD", "Đơn vị R22", "R22OLD", parent, "khai báo", null));
        add.IsError.Should().BeFalse(DescribeErrors(add.Errors));
        var predecessor = add.Value.OrgUnitId;
        var firstVersionId = add.Value.Write.NewVersionId;

        // Same-period edit soft-deactivates the Add row (status stays normal, no successor link).
        var edited = await BuildService().EditOrgUnitDeclarationAsync(
            new EditOrgUnitDeclarationRequest(
                predecessor, firstVersionId, parent, p1, null,
                "R22OLD", "Đơn vị R22 đổi tên", "R22OLD", "sửa tên", null));
        edited.IsError.Should().BeFalse(DescribeErrors(edited.Errors));

        // Adjacent second active slice (algebra case 2) — keeps the prior inactive row untouched.
        var second = await OrgUnitRepo.UpsertAsync(
            predecessor, p2, "R22OLD", "Đơn vị R22 giai đoạn 2", "R22B", parent,
            VersionOperationKind.Edit, Actor, "giai đoạn 2",
            new OrgUnitSupplementalDto(BusinessNumber: "0222000002", Phone: "02422220002"));
        second.IsError.Should().BeFalse(DescribeErrors(second.Errors));

        var before = await ReadAllVersionRowsAsync(predecessor);
        before.Count(r => r.IsActive).Should().Be(2);
        var inactiveBefore = before.Should().ContainSingle(r => !r.IsActive).Subject;
        inactiveBefore.Status.Should().Be("normal");
        inactiveBefore.ReplacedByOrgUnitId.Should().BeNull();

        var auditsBefore = await SnapshotAuditAsync();
        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R22NEW", parent));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var after = await ReadAllVersionRowsAsync(predecessor);
        var stillInactive = after.Should().ContainSingle(r => r.Id == inactiveBefore.Id).Subject;
        stillInactive.IsActive.Should().BeFalse();
        stillInactive.Status.Should().Be("normal");
        stillInactive.ReplacedByOrgUnitId.Should().BeNull();
        stillInactive.OperationKind.Should().Be(inactiveBefore.OperationKind);
        stillInactive.OrgCode.Should().Be(inactiveBefore.OrgCode);
        stillInactive.OrgNameFullVn.Should().Be(inactiveBefore.OrgNameFullVn);
        stillInactive.Reason.Should().Be(inactiveBefore.Reason);

        var marked = after.Where(r => r.Id != inactiveBefore.Id).ToList();
        marked.Should().HaveCount(2);
        marked.Should().OnlyContain(r => r.Status == "replaced" && r.ReplacedByOrgUnitId == (ulong)result.Value.OrgUnitId);

        var delta = await AuditDeltaAsync(auditsBefore);
        var replaceRow = delta.Should().ContainSingle(a => a.EventType == "orgunit-replace").Subject;
        var detail = JsonSerializer.Deserialize<OrgUnitReplaceAuditDetailDto>(replaceRow.Detail!, AuditJsonOptions)!;
        detail.MarkedVersionIds.Should().BeEquivalentTo(marked.Select(r => (long)r.Id));
        detail.MarkedVersionIds.Should().NotContain((long)inactiveBefore.Id);
    }

    // =========================================================================================
    // Replacement-guard tests (8–13, 15) and inherited-check / CHECK / rollback / audit (16–21).
    // =========================================================================================

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_RootPredecessor_OrdinaryGlobalActor_ReturnsRootNotReplaceableBeforeMark()
    {
        SkipUnlessDbAvailable();

        // Forced unreachable fixture through Add (N1): a root predecessor plus an independent covering
        // parent for a non-root successor. Every legal org unit hangs off the single active root, so an
        // independent parent requires a second overlapping root. The legal alternative — a covering
        // parent that is a child of the root predecessor — was considered and is rejected: it makes the
        // predecessor non-empty and conflates the root gate with OrgUnit.PredecessorNotEmpty.
        var coveringParent = await CreateOrgUnitAsync("R8PAR", "Cha phủ", "R8PAR", null, OpenFrom2020);
        var predecessor = await CreateOrgUnitAsync("R8ROOT", "Gốc R8", "R8ROOT", null, OpenFrom2020);
        var before = await ReadAllVersionRowsAsync(predecessor);

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R8NEW", coveringParent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.RootNotReplaceable");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            before, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_RootSuccessor_OrdinaryGlobalActor_ReturnsRootNotReplaceableBeforeMark()
    {
        SkipUnlessDbAvailable();

        // Covering parent for the non-root predecessor; successor requested as root (ParentId null).
        var parent = await AddRootAsync("R9PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("R9OLD", parent, OpenFrom2020);
        var before = await ReadAllVersionRowsAsync(predecessor);

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R9NEW", parentId: null));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.RootNotReplaceable");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            before, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_MixedNullAndNonNullParents_ReturnsParentNotWellDefinedBeforeRootGate()
    {
        SkipUnlessDbAvailable();

        // Parent A covers both slices. Mixed {NULL, parentA} cannot be produced by Edit (parent locked);
        // direct SQL / Upsert with parent change — use UpsertAsync like Edit's mixed-parent fixture, then
        // force one row's parent to NULL via SQL so NULL is a set element (R7).
        var parentA = await AddRootAsync("R10PA", OpenFrom2020);
        var firstPeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 6, 30));
        var predecessor = await AddChildAsync("R10OLD", parentA, firstPeriod);
        var second = await OrgUnitRepo.UpsertAsync(
            predecessor,
            new EffectivePeriod(new DateOnly(2020, 7, 1), EffectivePeriod.OpenEnd),
            "R10OLD", "Đơn vị R10", "R10OLD", parentA, VersionOperationKind.Edit, Actor, "giai đoạn 2");
        second.IsError.Should().BeFalse(DescribeErrors(second.Errors));

        await using (var connection = new MySqlConnection(ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await connection.ExecuteAsync(
                """
                UPDATE org_unit_version
                SET parent_id = NULL
                WHERE org_unit_id = @id AND isactive = 1 AND effective_from = '2020-01-01'
                """,
                new { id = predecessor });
        }

        (await ReadDistinctActiveParentIdsAsync(predecessor)).Should().BeEquivalentTo(
            new ulong?[] { null, (ulong)parentA });

        var before = await ReadAllVersionRowsAsync(predecessor);

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R10NEW", parentA));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.ParentNotWellDefined");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            before, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_ClosedPredecessor_ReturnsMarksNothingBeforeParentGuard()
    {
        SkipUnlessDbAvailable();

        // Parent covers OpenFrom2020. Predecessor ends with zero active rows (empty parent set) so the
        // neighbouring ParentNotWellDefined is reachable if precedence is wrong. Close always leaves an
        // active remnant; Cancel stamps cancelled — so soft-deactivate the only row via SQL (R7).
        var parent = await AddRootAsync("R11PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("R11OLD", parent, OpenFrom2020);
        await SoftDeactivateAllVersionsAsync("org_unit_version", "org_unit_id", predecessor);
        (await ReadAllVersionRowsAsync(predecessor)).Should().OnlyContain(r => !r.IsActive);

        var headersBefore = await CountAllHeaderRowsAsync();
        var auditsBefore = await SnapshotAuditAsync();

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R11NEW", parent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.PredecessorMarksNothing");
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore);
        (await AuditDeltaAsync(auditsBefore)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_ChildWithTwoActiveVersionPeriods_ReturnsPredecessorNotEmpty()
    {
        SkipUnlessDbAvailable();

        // Parent covers both child slices and the would-be successor.
        var parent = await AddRootAsync("R12PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("R12OLD", parent, OpenFrom2020);
        var childFirst = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 6, 30));
        var child = await AddChildAsync("R12CHI", predecessor, childFirst, "Con R12", "C12");
        var childSecond = await OrgUnitRepo.UpsertAsync(
            child,
            new EffectivePeriod(new DateOnly(2020, 7, 1), EffectivePeriod.OpenEnd),
            "R12CHI", "Con R12 b", "C12b", predecessor, VersionOperationKind.Edit, Actor, "giai đoạn 2");
        childSecond.IsError.Should().BeFalse(DescribeErrors(childSecond.Errors));

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R12NEW", parent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.PredecessorNotEmpty");
        result.FirstError.Description.Should().Be(PredecessorNotEmptyDescription);
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_ActiveUserDependency_ReturnsPredecessorNotEmpty()
    {
        SkipUnlessDbAvailable();

        // Parent covers OpenFrom2020. User edge only — no child org unit.
        var parent = await AddRootAsync("R13PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("R13OLD", parent, OpenFrom2020);
        var role = await CreateRoleAsync("R13ROLE", "Vai trò R13", OpenFrom2020);
        var user = await CreateUserHeaderAsync();
        var seeded = await Users.UpsertAsync(
            user, OpenFrom2020, "r13user", "Người dùng R13", predecessor, role, Actor, "seed");
        seeded.IsError.Should().BeFalse(DescribeErrors(seeded.Errors));

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R13NEW", parent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.PredecessorNotEmpty");
        result.FirstError.Description.Should().Be(PredecessorNotEmptyDescription);
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_RootSuccessorOverlapsSeparateActiveRoot_ReturnsRootPeriodOverlaps()
    {
        SkipUnlessDbAvailable();

        // Separate active root overlaps the requested root-successor period; predecessor is non-root.
        // Parent of predecessor covers OpenFrom2020. Break-glass so RootNotReplaceable does not fire first.
        var parent = await AddRootAsync("R15PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("R15OLD", parent, OpenFrom2020);
        // Second overlapping root — CreateOrgUnitAsync (Add cannot mint a second overlapping root);
        // deliberately unreachable through Add — that is the state the guard exists to refuse.
        await CreateOrgUnitAsync("R15OTH", "Gốc khác", "R15OTH", null, OpenFrom2020);

        var result = await BuildBreakGlassService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R15NEW", parentId: null));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.RootPeriodOverlaps");
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_RootPredecessorToOverlappingRootSuccessor_BreakGlassSucceedsAndRecordsBothAudits()
    {
        SkipUnlessDbAvailable();

        // Successor is a root — no parent coverage to state (R4). No other active root. Self-overlap
        // of predecessor/successor periods is permitted after mark under break-glass.
        var predecessor = await AddRootAsync("R16ROOT", OpenFrom2020);
        var auditsBefore = await SnapshotAuditAsync();

        var result = await BuildBreakGlassService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R16NEW", parentId: null, fullVn: "Gốc mới R16", shortVn: "R16N"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var pred = (await ReadAllVersionRowsAsync(predecessor)).Should().ContainSingle().Subject;
        pred.IsActive.Should().BeFalse();
        pred.Status.Should().Be("replaced");
        pred.ReplacedByOrgUnitId.Should().Be((ulong)result.Value.OrgUnitId);

        var succ = (await ReadAllVersionRowsAsync(result.Value.OrgUnitId)).Should().ContainSingle().Subject;
        succ.ParentId.Should().BeNull();
        succ.OperationKind.Should().Be("Replace");
        succ.Status.Should().Be("normal");
        succ.IsActive.Should().BeTrue();

        var delta = await AuditDeltaAsync(auditsBefore);
        var target = $"org_unit_version:{result.Value.Write.NewVersionId}";
        var markedIds = new[] { (long)pred.Id };
        const string note = "thay thế";

        // ContainSingle below proves each expected row's fields; this set proves there is no third
        // security event on the same successor target.
        delta.Where(a => a.Target == target).Select(a => a.EventType).Should().BeEquivalentTo(
            ["orgunit-replace", "orgunit-root-replace-breakglass"]);

        var ordinary = delta.Should().ContainSingle(a => a.EventType == "orgunit-replace").Subject;
        ordinary.Target.Should().Be(target);
        ordinary.Actor.Should().Be(Actor);
        var ordinaryDetail = JsonSerializer.Deserialize<OrgUnitReplaceAuditDetailDto>(ordinary.Detail!, AuditJsonOptions)!;
        ordinaryDetail.PredecessorOrgUnitId.Should().Be(predecessor);
        ordinaryDetail.SuccessorOrgUnitId.Should().Be(result.Value.OrgUnitId);
        ordinaryDetail.MarkedVersionIds.Should().BeEquivalentTo(markedIds);
        ordinaryDetail.Note.Should().Be(note);

        var breakGlass = delta.Should()
            .ContainSingle(a => a.EventType == "orgunit-root-replace-breakglass").Subject;
        breakGlass.Target.Should().Be(target);
        breakGlass.Actor.Should().Be(Actor);
        var breakGlassDetail = JsonSerializer.Deserialize<OrgUnitReplaceAuditDetailDto>(
            breakGlass.Detail!, AuditJsonOptions)!;
        breakGlassDetail.PredecessorOrgUnitId.Should().Be(predecessor);
        breakGlassDetail.SuccessorOrgUnitId.Should().Be(result.Value.OrgUnitId);
        breakGlassDetail.MarkedVersionIds.Should().BeEquivalentTo(markedIds);
        breakGlassDetail.Note.Should().Be(note);
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_ThirdIdentityOwnsRequestedCode_ReturnsCodeInUseAndRollsBack()
    {
        SkipUnlessDbAvailable();

        // Parent covers OpenFrom2020. C holds the requested code; A is the predecessor.
        var parent = await AddRootAsync("R17PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("R17A", parent, OpenFrom2020);
        var holderC = await AddChildAsync("R17CODE", parent, OpenFrom2020);
        var beforeA = await ReadAllVersionRowsAsync(predecessor);
        var beforeC = await ReadAllVersionRowsAsync(holderC);
        var headersBefore = await CountAllHeaderRowsAsync();
        var auditsBefore = await SnapshotAuditAsync();

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R17CODE", parent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.CodeInUse");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            beforeA, opts => opts.WithStrictOrdering());
        (await ReadAllVersionRowsAsync(holderC)).Should().BeEquivalentTo(
            beforeC, opts => opts.WithStrictOrdering());
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore);
        (await AuditDeltaAsync(auditsBefore)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_NewParentHasCoverageGap_ReturnsParentGapAndLeavesNoSuccessor()
    {
        SkipUnlessDbAvailable();

        // Old parent covers OpenFrom2020; new parent covers only [2020-01-01, 2020-06-30] — gap vs successor.
        var oldParent = await AddRootAsync("R18OLD", OpenFrom2020);
        var newParentPeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 6, 30));
        // Legal and reachable: a child of oldParent covering only [2020-01-01, 2020-06-30], so the successor's
        // OpenFrom2020 period runs past its coverage and D8 fires.
        var newParent = await AddChildAsync("R18NEWP", oldParent, newParentPeriod, "Cha hẹp", "R18NEWP");
        var predecessor = await AddChildAsync("R18A", oldParent, OpenFrom2020);
        var before = await ReadAllVersionRowsAsync(predecessor);
        var headersBefore = await CountAllHeaderRowsAsync();
        var auditsBefore = await SnapshotAuditAsync();

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R18SUCC", newParent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("TemporalFk.ParentGap");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            before, opts => opts.WithStrictOrdering());
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore);
        (await AuditDeltaAsync(auditsBefore)).Should().BeEmpty();
    }

    [Fact]
    public async Task OrgUnitVersion_ReplacedStatusContradictions_AreRejectedByChkOuvStatus()
    {
        SkipUnlessDbAvailable();

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var orgUnitId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO org_unit () VALUES (); SELECT LAST_INSERT_ID();");
        var successorId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO org_unit () VALUES (); SELECT LAST_INSERT_ID();");
        var versionId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO org_unit_version
                (org_unit_id, org_code, org_name_full_vn, org_name_short_vn, status, replaced_by_org_unit_id,
                 effective_from, effective_to, isactive, recorded_by)
            VALUES
                (@orgUnitId, 'CHK19', 'A', 'A', 'normal', NULL, '2020-01-01', '9999-12-31', 1, 'tester');
            SELECT LAST_INSERT_ID();
            """,
            new { orgUnitId });

        var activeReplaced = async () => await connection.ExecuteAsync(
            """
            UPDATE org_unit_version
            SET status = 'replaced', isactive = 1, replaced_by_org_unit_id = @successorId
            WHERE id = @versionId
            """,
            new { versionId, successorId });
        (await activeReplaced.Should().ThrowAsync<MySqlException>()).Which.Number.Should().Be(
            3819, "chk_ouv_status must reject replaced+active");

        var replacedWithoutLink = async () => await connection.ExecuteAsync(
            """
            UPDATE org_unit_version
            SET status = 'replaced', isactive = 0, replaced_by_org_unit_id = NULL
            WHERE id = @versionId
            """,
            new { versionId });
        (await replacedWithoutLink.Should().ThrowAsync<MySqlException>()).Which.Number.Should().Be(
            3819, "chk_ouv_status must reject replaced without successor link");
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_OrdinaryAuditWriteFails_RollsBackWholeComposite()
    {
        SkipUnlessDbAvailable();

        var parent = await AddRootAsync("RAUDPAR", OpenFrom2020);
        var predecessor = await AddChildAsync("RAUDOLD", parent, OpenFrom2020);
        var before = await ReadAllVersionRowsAsync(predecessor);
        var headersBefore = await CountAllHeaderRowsAsync();
        var auditsBefore = await SnapshotAuditAsync();

        var result = await BuildService(auditLog: new FailingAuditLogWriter()).ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "RAUDNEW", parent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("AuditLog.Injected");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            before, opts => opts.WithStrictOrdering());
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore, "an unaudited successor must not survive");
        (await AuditDeltaAsync(auditsBefore)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_RootSecondAuditWriteFails_RollsBackWholeComposite()
    {
        SkipUnlessDbAvailable();

        // Root path writes two audit rows; fail on the second after the first succeeds inside the TX.
        var predecessor = await AddRootAsync("RA2ROOT", OpenFrom2020);
        var before = await ReadAllVersionRowsAsync(predecessor);
        var headersBefore = await CountAllHeaderRowsAsync();
        var auditsBefore = await SnapshotAuditAsync();

        var result = await BuildBreakGlassService(auditLog: new FailOnSecondAuditLogWriter())
            .ReplaceOrgUnitDeclarationAsync(
                ReplaceRequest(predecessor, "RA2NEW", parentId: null, fullVn: "Gốc RA2", shortVn: "RA2N"));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("AuditLog.Injected");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            before, opts => opts.WithStrictOrdering());
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore, "an unaudited successor must not survive");
        (await AuditDeltaAsync(auditsBefore)).Should().BeEmpty(
            "a first audit row that committed early would leak here");
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_PostMarkReplacementGuardFailure_RollsBackMarkAndConsumesNoIdentity()
    {
        SkipUnlessDbAvailable();

        // Parent covers OpenFrom2020. Predecessor looks empty but has an active child — empty probe runs
        // after mark (design [4f]); failure must roll back the mark and consume no org_unit id.
        var parent = await AddRootAsync("R20PAR", OpenFrom2020);
        var predecessor = await AddChildAsync("R20OLD", parent, OpenFrom2020);
        await AddChildAsync("R20CHI", predecessor, OpenFrom2020);

        var before = await ReadAllVersionRowsAsync(predecessor);
        var headersBefore = await CountAllHeaderRowsAsync();
        var nextIdBefore = await PeekNextOrgUnitIdAsync();
        var auditsBefore = await SnapshotAuditAsync();

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R20NEW", parent));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("OrgUnit.PredecessorNotEmpty");
        (await ReadAllVersionRowsAsync(predecessor)).Should().BeEquivalentTo(
            before, opts => opts.WithStrictOrdering());
        (await CountAllHeaderRowsAsync()).Should().Be(headersBefore);
        (await PeekNextOrgUnitIdAsync()).Should().Be(nextIdBefore);
        (await AuditDeltaAsync(auditsBefore)).Should().BeEmpty();

        var mintedAfter = await OrgUnits.CreateIdentityAsync();
        mintedAfter.Should().Be(nextIdBefore);
    }

    [Fact]
    public async Task ReplaceOrgUnitDeclarationAsync_NonRootSuccess_WritesExactOrdinaryAuditPayload()
    {
        SkipUnlessDbAvailable();

        // Parent covers both predecessor slices and the successor. Two marked version ids in the payload.
        var parent = await AddRootAsync("R21PAR", OpenFrom2020);
        var predecessor = await SeedThreeActiveNonOverlappingAsync(parent, "R21OLD");
        // Keep exactly two active rows for the payload claim.
        var all = await ReadAllVersionRowsAsync(predecessor);
        var drop = all.OrderBy(r => r.Id).Last();
        await SoftDeactivateVersionAsync(drop.Id);
        var markedIds = (await ReadAllVersionRowsAsync(predecessor))
            .Where(r => r.IsActive)
            .Select(r => (long)r.Id)
            .OrderBy(id => id)
            .ToList();
        markedIds.Should().HaveCount(2);

        var auditsBefore = await SnapshotAuditAsync();
        const string note = "lý do thay thế đặc biệt R21";

        var result = await BuildService().ReplaceOrgUnitDeclarationAsync(
            ReplaceRequest(predecessor, "R21NEW", parent, reason: note));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var delta = await AuditDeltaAsync(auditsBefore);
        delta.Should().ContainSingle(a => a.EventType == "orgunit-replace");
        var row = delta.Single(a => a.EventType == "orgunit-replace");
        row.Target.Should().Be($"org_unit_version:{result.Value.Write.NewVersionId}");

        // No versionId on this detail — unlike Add/Edit siblings — because the audit target already is
        // org_unit_version:{successor's first version id} and markedVersionIds carries the marked set.
        var detail = JsonSerializer.Deserialize<OrgUnitReplaceAuditDetailDto>(row.Detail!, AuditJsonOptions)!;
        detail.PredecessorOrgUnitId.Should().Be(predecessor);
        detail.SuccessorOrgUnitId.Should().Be(result.Value.OrgUnitId);
        detail.MarkedVersionIds.Should().BeEquivalentTo(markedIds);
        detail.Note.Should().Be(note);
    }

    // ---------------------------------------------------------------------------------------
    // Fixture helpers
    // ---------------------------------------------------------------------------------------

    // Distinct non-null values for every BusinessColumns entry — a null seed cannot detect a null overwrite.
    private static OrgUnitSupplementalDto FullBusinessSupplemental(
        string businessNumber, string nameFullEn, string tag) =>
        new(
            BusinessNumber: businessNumber,
            AddrLineVn: $"Địa chỉ VN {tag}",
            AddrLineEn: $"Addr EN {tag}",
            AddrWardVn: $"Phường {tag}",
            AddrWardEn: $"Ward {tag}",
            AddrDistrictVn: $"Quận {tag}",
            AddrDistrictEn: $"District {tag}",
            AddrProvinceVn: $"Tỉnh {tag}",
            AddrProvinceEn: $"Province {tag}",
            AdminDivisionLevel: 2,
            NameFullEn: nameFullEn,
            NameShortEn: $"Short {tag}",
            Phone: $"024-{tag}",
            Fax: $"024-F{tag}",
            Email: $"{tag.ToLowerInvariant()}@example.test");

    private async Task<long> AddRootAsync(string code, EffectivePeriod period)
    {
        var result = await BuildBreakGlassService().AddOrgUnitDeclarationAsync(
            new AddOrgUnitDeclarationRequest(
                period, code, $"Đơn vị {code}", code, null, "khai báo gốc", null));
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        return result.Value.OrgUnitId;
    }

    private async Task<long> AddChildAsync(
        string code,
        long parentId,
        EffectivePeriod period,
        string? fullVn = null,
        string? shortVn = null,
        string? reason = "khai báo",
        OrgUnitSupplementalDto? supplemental = null)
    {
        var result = await BuildService().AddOrgUnitDeclarationAsync(
            new AddOrgUnitDeclarationRequest(
                period, code, fullVn ?? $"Đơn vị {code}", shortVn ?? code, parentId, reason, supplemental));
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        return result.Value.OrgUnitId;
    }

    // Three active non-overlapping rows (algebra case 2). Add gesture creates the identity; adjacent
    // UpsertAsync Edit slices are required because EditOrgUnitDeclarationAsync on a single open period
    // remakes coverage rather than appending a disjoint active neighbour.
    private async Task<long> SeedThreeActiveNonOverlappingAsync(long parentId, string code)
    {
        var p1 = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));
        var p2 = new EffectivePeriod(new DateOnly(2021, 1, 1), new DateOnly(2021, 12, 31));
        var p3 = new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd);

        var id = await AddChildAsync(code, parentId, p1, $"{code} a", $"{code}A", "giai đoạn 1",
            new OrgUnitSupplementalDto(BusinessNumber: "1001", Phone: "0241000001"));
        await SoftDeactivateAllVersionsAsync("org_unit_version", "org_unit_id", id);

        foreach (var (period, kind, name, shortName, biz, phone) in new[]
                 {
                     (p1, VersionOperationKind.Add, $"{code} a", $"{code}A", "1001", "0241000001"),
                     (p2, VersionOperationKind.Edit, $"{code} b", $"{code}B", "1002", "0241000002"),
                     (p3, VersionOperationKind.Edit, $"{code} c", $"{code}C", "1003", "0241000003"),
                 })
        {
            var write = await OrgUnitRepo.UpsertAsync(
                id, period, code, name, shortName, parentId, kind, Actor, name,
                new OrgUnitSupplementalDto(BusinessNumber: biz, Phone: phone));
            write.IsError.Should().BeFalse(DescribeErrors(write.Errors));
        }

        (await ReadAllVersionRowsAsync(id)).Count(r => r.IsActive).Should().Be(3);
        return id;
    }

    private async Task<long> SeedAddEditCloseActiveTrioAsync(long parentId, string code)
    {
        var p1 = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));
        var p2 = new EffectivePeriod(new DateOnly(2021, 1, 1), new DateOnly(2021, 12, 31));
        var p3 = new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd);

        var id = await AddChildAsync(code, parentId, p1, $"{code} add", $"{code}A", "add",
            FullBusinessSupplemental("3001", "Add row", "A"));
        await SoftDeactivateAllVersionsAsync("org_unit_version", "org_unit_id", id);

        var add = await OrgUnitRepo.UpsertAsync(
            id, p1, code, $"{code} add", $"{code}A", parentId, VersionOperationKind.Add, Actor, "add",
            FullBusinessSupplemental("3001", "Add row", "A"));
        add.IsError.Should().BeFalse(DescribeErrors(add.Errors));
        var edit = await OrgUnitRepo.UpsertAsync(
            id, p2, code, $"{code} edit", $"{code}E", parentId, VersionOperationKind.Edit, Actor, "edit",
            FullBusinessSupplemental("3002", "Edit row", "E"));
        edit.IsError.Should().BeFalse(DescribeErrors(edit.Errors));
        var closeSeed = await OrgUnitRepo.UpsertAsync(
            id, p3, code, $"{code} close-src", $"{code}C", parentId, VersionOperationKind.Edit, Actor, "before-close",
            FullBusinessSupplemental("3003", "Close src", "C"));
        closeSeed.IsError.Should().BeFalse(DescribeErrors(closeSeed.Errors));

        var close = await OrgUnitRepo.CloseVersionAsync(
            id, closeSeed.Value.NewVersionId, new DateOnly(2025, 12, 31), OperationDateForToday(), Actor, "close");
        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));

        var active = (await ReadAllVersionRowsAsync(id)).Where(r => r.IsActive).ToList();
        active.Select(r => r.OperationKind).Should().BeEquivalentTo(["Add", "Edit", "Close"]);
        return id;
    }

    private async Task SoftDeactivateAllVersionsAsync(string table, string identityColumn, long identityId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            $"UPDATE `{table}` SET isactive = 0 WHERE `{identityColumn}` = @identityId AND isactive = 1",
            new { identityId });
    }

    private async Task SoftDeactivateVersionAsync(ulong versionId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "UPDATE org_unit_version SET isactive = 0 WHERE id = @versionId",
            new { versionId });
    }

    private async Task<IReadOnlyList<OrgUnitVersionSnapshot>> ReadAllVersionRowsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<OrgUnitVersionSnapshot>(
            """
            SELECT id AS Id, org_code AS OrgCode, org_name_full_vn AS OrgNameFullVn,
                   org_name_short_vn AS OrgNameShortVn, parent_id AS ParentId,
                   effective_from AS EffectiveFrom, effective_to AS EffectiveTo,
                   isactive AS IsActive, status AS Status,
                   replaced_by_org_unit_id AS ReplacedByOrgUnitId,
                   recorded_by AS RecordedBy, reason AS Reason, operation_kind AS OperationKind,
                   org_business_number AS BusinessNumber, org_addr_line_vn AS AddrLineVn,
                   org_addr_line_en AS AddrLineEn, org_addr_ward_vn AS AddrWardVn,
                   org_addr_ward_en AS AddrWardEn, org_addr_district_vn AS AddrDistrictVn,
                   org_addr_district_en AS AddrDistrictEn, org_addr_province_vn AS AddrProvinceVn,
                   org_addr_province_en AS AddrProvinceEn,
                   org_admin_division_level AS AdminDivisionLevel, org_name_full_en AS NameFullEn,
                   org_name_short_en AS NameShortEn, org_phone AS Phone, org_fax AS Fax,
                   org_email AS Email
            FROM org_unit_version
            WHERE org_unit_id = @orgUnitId
            ORDER BY id
            """,
            new { orgUnitId });
        return rows.ToList();
    }

    private async Task<IReadOnlyList<ulong?>> ReadDistinctActiveParentIdsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<ulong?>(
            "SELECT DISTINCT parent_id FROM org_unit_version WHERE org_unit_id = @orgUnitId AND isactive = 1",
            new { orgUnitId });
        return rows.ToList();
    }

    private async Task<long> CountAllHeaderRowsAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM org_unit");
    }

    private async Task<long> PeekNextOrgUnitIdAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            """
            SELECT AUTO_INCREMENT
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'org_unit'
            """);
    }

    private async Task<IReadOnlyList<AuditSnapshot>> SnapshotAuditAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<AuditSnapshot>(
            "SELECT id AS Id, event_type AS EventType, target AS Target, detail AS Detail, username AS Actor FROM audit_log ORDER BY id");
        return rows.ToList();
    }

    private async Task<IReadOnlyList<AuditSnapshot>> AuditDeltaAsync(IReadOnlyList<AuditSnapshot> before)
    {
        var after = await SnapshotAuditAsync();
        var beforeIds = before.Select(b => b.Id).ToHashSet();
        return after.Where(a => !beforeIds.Contains(a.Id)).ToList();
    }

    private sealed class OrgUnitVersionSnapshot
    {
        public ulong Id { get; set; }
        public string OrgCode { get; set; } = "";
        public string OrgNameFullVn { get; set; } = "";
        public string OrgNameShortVn { get; set; } = "";
        public ulong? ParentId { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public DateTime EffectiveTo { get; set; }
        public bool IsActive { get; set; }
        public string Status { get; set; } = "";
        public ulong? ReplacedByOrgUnitId { get; set; }
        public string RecordedBy { get; set; } = "";
        public string? Reason { get; set; }
        public string? OperationKind { get; set; }
        public string? BusinessNumber { get; set; }
        public string? AddrLineVn { get; set; }
        public string? AddrLineEn { get; set; }
        public string? AddrWardVn { get; set; }
        public string? AddrWardEn { get; set; }
        public string? AddrDistrictVn { get; set; }
        public string? AddrDistrictEn { get; set; }
        public string? AddrProvinceVn { get; set; }
        public string? AddrProvinceEn { get; set; }
        public sbyte AdminDivisionLevel { get; set; }
        public string? NameFullEn { get; set; }
        public string? NameShortEn { get; set; }
        public string? Phone { get; set; }
        public string? Fax { get; set; }
        public string? Email { get; set; }
    }

    private sealed class AuditSnapshot
    {
        public ulong Id { get; set; }
        public string EventType { get; set; } = "";
        public string Target { get; set; } = "";
        public string? Detail { get; set; }
        public string? Actor { get; set; }
    }

    // Mirrors the production OrgUnitReplaceAuditDetail shape Task 4 must implement (R2).
    private sealed record OrgUnitReplaceAuditDetailDto(
        long PredecessorOrgUnitId,
        long SuccessorOrgUnitId,
        IReadOnlyList<long> MarkedVersionIds,
        string? Note);

    private sealed class FakeAuthorizationService(ErrorOr<DataScope> outcome) : IAuthorizationService
    {
        public string? LastFunctionKey { get; private set; }

        public Task<ErrorOr<DataScope>> AuthorizeAsync(string username, string functionKey)
        {
            LastFunctionKey = functionKey;
            return Task.FromResult(outcome);
        }

        public Task<bool> IsFunctionOpenAsync(string username, string functionKey) =>
            Task.FromResult(!outcome.IsError);
    }

    private sealed class FailingAuditLogWriter : IAuditLogWriter
    {
        public Task<ErrorOr<Success>> WriteAsync(
            AuditLogEntry entry, System.Data.IDbTransaction transaction, CancellationToken cancellationToken = default) =>
            Task.FromResult<ErrorOr<Success>>(Error.Failure("AuditLog.Injected", "Simulated audit write failure."));
    }

    // Succeeds on the first audit row (real write inside the ambient transaction) and fails on the second,
    // so a leak of the first committed row reddens the root-path rollback claim.
    private sealed class FailOnSecondAuditLogWriter : IAuditLogWriter
    {
        private readonly IAuditLogWriter _inner = new AST.Infrastructure.AuditLogWriter();
        private int _calls;

        public async Task<ErrorOr<Success>> WriteAsync(
            AuditLogEntry entry, System.Data.IDbTransaction transaction, CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                return await _inner.WriteAsync(entry, transaction, cancellationToken);
            }

            return Error.Failure("AuditLog.Injected", "Simulated audit write failure on second row.");
        }
    }

    private sealed class FakeCurrentWindowsUser(string? username) : ICurrentWindowsUser
    {
        public string? Username => username;
    }

    private sealed class FakeBreakGlassPolicy(params string[] admins) : IBreakGlassPolicy
    {
        private readonly HashSet<string> _admins = new(admins, StringComparer.Ordinal);

        public bool IsBreakGlassAdmin(string username) => _admins.Contains(username);
    }
}
