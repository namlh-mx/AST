using FluentAssertions;

namespace AST.Meta.Tests;

// Claim: in AST.Infrastructure, AST.Modules.IAM, AST.ConfigKeyGen and AST, no IL use (call, callvirt,
// newobj, ldftn, ldvirtftn, ldtoken) of a pinned org-unit version writer resolves to OrgUnitRepository,
// VersionedRepository<OrgUnitVersionEntity> or open VersionedRepository<T>, except three callers, each
// matched by defining assembly and full type name:
//   AST.Modules.IAM.OrgUnitDeclarationService
//   AST.Modules.IAM.Data.Repositories.OrgUnitRepository (the whole type)
//   AST.Infrastructure.VersionedRepository`1
//
// Exclusions: reflection; dynamic; a delegate built inside an allowed caller and handed out; test
// projects; raw SQL and a VersionedRepository<OtherRow> subclass whose table is org_unit_version; the
// identity writers; a new writer under a new name; a .cs file brought in from outside the project, which
// is covered by build-first only.
// The guard assumes the current production inputs were built first; a change that keeps an older
// timestamp than the DLL is not detected by freshness.
public sealed class OrgUnitWritePathAbsenceTests
{
    private const string IdentityCreationClaim =
        "org-unit identity creation goes only through IOrgUnitDeclarationService";

    // WIDENED 2026-08-21 (backlog 0.7). Until then this file could only claim the narrow identity-creation
    // half, because UpsertAsync stayed on IOrgUnitRepository for Edit. Edit moved behind the service, so the
    // claim is now the same as the role side's.
    private const string WriteClaim =
        "every org-unit version write goes only through IOrgUnitDeclarationService in production code";

    [Fact]
    public void IOrgUnitRepository_does_not_declare_CreateIdentityAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIOrgUnitRepository(), "CreateIdentityAsync").Should().BeFalse(
            "IOrgUnitRepository must not declare CreateIdentityAsync; " + IdentityCreationClaim + ".");
    }

    [Fact]
    public void IOrgUnitRepository_does_not_declare_DeleteEmptyIdentityAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIOrgUnitRepository(), "DeleteEmptyIdentityAsync").Should().BeFalse(
            "IOrgUnitRepository must not declare DeleteEmptyIdentityAsync; a zero-version header is not a " +
            "state it can produce, so it has nothing to compensate (design-effective-period.md §7).");
    }

    // LEG 1 (positive) — and THE DISCRIMINATOR for every negative Fact in this file: they must fail because
    // a member was REMOVED, not because a file was renamed, moved or emptied and the scan silently found
    // nothing to match. Replaces the old "IOrgUnitRepository still declares UpsertAsync" discriminator,
    // which this slice made false BY DESIGN on 2026-08-21.
    [Fact]
    public void IOrgUnitDeclarationService_declares_EditOrgUnitDeclarationAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(
            ReadIOrgUnitDeclarationService(), "EditOrgUnitDeclarationAsync").Should().BeTrue(
            "org-unit Edit goes through IOrgUnitDeclarationService; " + WriteClaim + ". If this goes false " +
            "the scan is reading the wrong file, or the boundary moved without this guard being revisited.");
    }

    // LEG 2 (negative): the repository interface exposes no version writer at all.
    [Fact]
    public void IOrgUnitRepository_does_not_declare_UpsertAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIOrgUnitRepository(), "UpsertAsync").Should().BeFalse(
            "UpsertAsync moved behind IOrgUnitDeclarationService on 2026-08-21 (backlog 0.7); " + WriteClaim + ".");
    }

    [Fact]
    public void IOrgUnitRepository_does_not_declare_CloseVersionAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIOrgUnitRepository(), "CloseVersionAsync").Should().BeFalse(
            "IOrgUnitRepository declares no version writer; Close goes through OrgUnitDeclarationService.");
    }

    [Fact]
    public void IOrgUnitRepository_does_not_declare_DeleteVersionAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIOrgUnitRepository(), "DeleteVersionAsync").Should().BeFalse(
            "IOrgUnitRepository declares no version writer; there is no Delete business operation.");
    }

    [Fact]
    public void IOrgUnitRepository_does_not_declare_CancelPlanAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIOrgUnitRepository(), "CancelPlanAsync").Should().BeFalse(
            "IOrgUnitRepository declares no version writer; Cancel goes through OrgUnitDeclarationService.");
    }

    [Fact]
    public void IOrgUnitRepository_declares_GetByIdentityAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIOrgUnitRepository(), "GetByIdentityAsync").Should().BeTrue(
            "the interface file was read and still declares GetByIdentityAsync.");
    }

    // LEG 3a (negative): the screen does not reach a writer directly. PreviewUpsertAsync is a READ and
    // stays on the interface — the detector's leading `.` is what keeps it from matching.
    //
    // WIDENED 2026-08-21 from UpsertAsync-only to every org-unit writer name.
    // This screen holds IOrgUnitRepository for its reads and is the ONLY production holder of it, so a
    // `_orgUnits.CloseVersionAsync(...)` here would close a root with no root gate, no authorization and
    // neither audit row — and the narrow leg would have stayed green through all of it.
    [Fact]
    public void OrgUnitDeclarationViewModel_does_not_call_any_org_unit_writer()
    {
        OrgUnitWritePathAbsenceDetector.CallsAnyWriter(ReadOrgUnitDeclarationViewModel()).Should().BeFalse(
            "the declaration screen must not write a version itself, by any org-unit writer name; " + WriteClaim + ".");
    }

    [Fact]
    public void OrgUnitDeclarationService_does_not_mint_outside_the_transaction()
    {
        RoleWritePathAbsenceDetector.CallsParameterlessMint(ReadOrgUnitDeclarationService()).Should().BeFalse(
            "OrgUnitDeclarationService must mint through the ICompositeWriteContext overload; a header minted " +
            "on its own connection can outlive the failure of its first version (design-effective-period.md §7).");
    }

    [Fact]
    public void OrgUnitDeclarationService_does_not_compensate_a_minted_identity()
    {
        RoleWritePathAbsenceDetector.CallsCompensationDelete(ReadOrgUnitDeclarationService()).Should().BeFalse(
            "a compensation delete here would mean an identity is again being minted outside the " +
            "transaction — the rollback is what removes it now (design-effective-period.md §7).");
    }

    [Fact]
    public void OrgUnitDeclarationViewModel_does_not_mint_an_identity()
    {
        RoleWritePathAbsenceDetector.CallsParameterlessMint(ReadOrgUnitDeclarationViewModel()).Should().BeFalse(
            "the org-unit declaration screen must not mint at all; " + IdentityCreationClaim + ".");
    }

    [Fact]
    public void OrgUnitDeclarationViewModel_does_not_compensate_a_minted_identity()
    {
        RoleWritePathAbsenceDetector.CallsCompensationDelete(ReadOrgUnitDeclarationViewModel()).Should().BeFalse(
            "this screen's hand-written compensation is exactly what backlog 0.4b removed; the service's " +
            "transaction rolls both rows back instead.");
    }

    // LEG 3b (negative). WIDENED 2026-08-21 from "does not name Add" to "does not name ANY kind": while
    // UpsertAsync survived for Edit, only a mislabelled Add bypassed a gate. Now that every write is behind
    // the service and the kind is derived server-side, the screen has no business naming a kind at all --
    // and a narrower rule would go on passing for a screen that had started labelling writes again.
    [Fact]
    public void OrgUnitDeclarationViewModel_does_not_name_a_VersionOperationKind()
    {
        OrgUnitWritePathAbsenceDetector.NamesAnyOperationKind(ReadOrgUnitDeclarationViewModel()).Should().BeFalse(
            "the operation kind is derived server-side, so this screen may not label a write at all; " +
            WriteClaim + ".");
    }

    // Production scan. The file header states the claim, the three allowed callers and the exclusions.
    // Joined, not BeEmpty on the collection: BeEmpty names only the first offender.
    [Fact]
    public void No_production_file_outside_the_service_calls_an_org_unit_writer()
    {
        var report = OrgUnitWriterBoundary.Scan();
        var observed = report.Failure ?? string.Join("\n", report.Offenders);
        observed.Should().BeEmpty(
            "an org-unit version writer is used outside the three allowed callers, or the guard could not read "
            + "a current build (dotnet build AST.slnx); " + WriteClaim + ".");
    }

    private static string ReadIOrgUnitDeclarationService() =>
        File.ReadAllText(Path.Combine(MetaTest.RepoRoot(), "AST.Core", "Iam", "IOrgUnitDeclarationService.cs"));

    private static string ReadIOrgUnitRepository() =>
        File.ReadAllText(Path.Combine(MetaTest.RepoRoot(), "AST.Core", "Iam", "Repositories", "IOrgUnitRepository.cs"));

    private static string ReadOrgUnitDeclarationService() =>
        File.ReadAllText(Path.Combine(MetaTest.RepoRoot(), "AST.Modules.IAM", "OrgUnitDeclarationService.cs"));

    private static string ReadOrgUnitDeclarationViewModel() =>
        File.ReadAllText(Path.Combine(MetaTest.RepoRoot(), "AST.Shell", "ViewModels", "Iam", "OrgUnitDeclarationViewModel.cs"));
}

// Detector self-tests: a guard whose detector cannot reject a plausible mutation proves nothing.
public sealed class OrgUnitWritePathAbsenceDetectorTests
{
    [Theory]
    [InlineData("VersionOperationKind.Add, username, Reason.Trim(), Supplemental);")]
    [InlineData("var kind = VersionOperationKind.Add;")]
    // Edit is now caught too. Until 2026-08-21 this exact line was an ALLOWED case; it is the mutation the
    // widening exists for, so it is pinned here rather than left to the widened Fact alone.
    [InlineData("VersionOperationKind.Edit, username, Reason.Trim(), Supplemental);")]
    [InlineData("VersionOperationKind.Replace, username, Reason.Trim(), Supplemental);")]
    public void Detects_any_operation_kind(string source)
    {
        OrgUnitWritePathAbsenceDetector.NamesAnyOperationKind(source).Should().BeTrue();
    }

    [Theory]
    [InlineData("// VersionOperationKind.Add — removed 2026-08-17")]
    [InlineData("VersionOperationKindPresentation.ToVietnameseText(kind)")]
    public void Ignores_comments_and_presentation_helpers(string source)
    {
        OrgUnitWritePathAbsenceDetector.NamesAnyOperationKind(source).Should().BeFalse();
    }

    [Theory]
    [InlineData("var result = await _orgUnits.UpsertAsync(")]
    [InlineData("await Repository.UpsertAsync(newId, request.Period);")]
    public void Detects_a_direct_upsert_call(string source)
    {
        OrgUnitWritePathAbsenceDetector.CallsUpsert(source).Should().BeTrue();
    }

    [Theory]
    // THE case the leading `.` exists for: a read whose name merely contains the writer's.
    [InlineData("var affected = await _orgUnits.PreviewUpsertAsync(orgUnitId, period);")]
    [InlineData("// _orgUnits.UpsertAsync(...) — moved behind the service 2026-08-21")]
    public void Ignores_PreviewUpsertAsync_and_comments(string source)
    {
        OrgUnitWritePathAbsenceDetector.CallsUpsert(source).Should().BeFalse();
    }

    // The three writers the OLD UpsertAsync-only leg 3a would have let through. Each is
    // pinned individually: a widening asserted only by the widened Fact proves nothing about WHICH names
    // it actually gained.
    [Theory]
    [InlineData("await _orgUnits.CloseVersionAsync(orgUnitId, versionId, newTo, date, user, note);")]
    [InlineData("await _orgUnits.CancelPlanAsync(orgUnitId, versionId, today, user, \"\");")]
    [InlineData("await _orgUnits.DeleteVersionAsync(orgUnitId, versionId);")]
    public void Detects_every_org_unit_writer_name(string source)
    {
        OrgUnitWritePathAbsenceDetector.CallsAnyWriter(source).Should().BeTrue();
    }

    [Theory]
    [InlineData("var affected = await _orgUnits.PreviewUpsertAsync(orgUnitId, period);")]
    [InlineData("var dto = await _orgUnits.GetByIdentityAsync(orgUnitId, asOf);")]
    public void Ignores_reads_when_widened_to_every_writer(string source)
    {
        OrgUnitWritePathAbsenceDetector.CallsAnyWriter(source).Should().BeFalse();
    }
}

internal static class OrgUnitWritePathAbsenceDetector
{
    // The leading `VersionOperationKind\.` requires a literal dot immediately after the type name, so the
    // unrelated VersionOperationKindPresentation helper — which this same ViewModel legitimately uses to
    // render history rows — is not a false positive: there the next character is `P`, not `.`.
    public static bool NamesAnyOperationKind(string source) =>
        System.Text.RegularExpressions.Regex.IsMatch(StripLineComments(source), @"\bVersionOperationKind\.\w");

    // The leading `\.` is load-bearing: it is what makes PreviewUpsertAsync — a READ this screen
    // legitimately calls, and which merely CONTAINS the writer's name — not a match.
    public static bool CallsUpsert(string source) =>
        System.Text.RegularExpressions.Regex.IsMatch(StripLineComments(source), @"\.UpsertAsync\s*\(");

    // Every org-unit writer name, not just UpsertAsync. Receiver-free, so
    // it is only safe on a file that touches ONE repository — OrgUnitDeclarationViewModel does. Do NOT
    // reuse it for a directory scan: RoleRepository declares the same method NAMES.
    // The leading `\.` still carries PreviewUpsertAsync's exclusion.
    public static bool CallsAnyWriter(string source) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            StripLineComments(source),
            @"\.(?:UpsertAsync|CloseVersionAsync|CancelPlanAsync|DeleteVersionAsync)\s*\(");

    // Same stripping rule the role detector uses, so both guards agree on what a "mention" is.
    private static string StripLineComments(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0)
            {
                lines[i] = lines[i][..idx];
            }
        }

        return string.Join('\n', lines);
    }
}
