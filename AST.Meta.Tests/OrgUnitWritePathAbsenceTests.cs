using FluentAssertions;

namespace AST.Meta.Tests;

// Guards backlog 0.4b's org_unit half (2026-08-17) by scanning four source files. A migrated test stays
// green if the ViewModel quietly mints again, so this scan is the only cheap tripwire.
//
// HOW it reaches internal types: SOURCE TEXT, not reflection. AST.Meta.Tests has no ProjectReference (see
// the csproj), so InternalsVisibleTo would not help. The detector is RoleWritePathAbsenceDetector, reused
// rather than re-derived — its regexes already survived an independent review (the
// method-group case).
//
// COVERED:
//   - IOrgUnitRepository.cs declaring CreateIdentityAsync / DeleteEmptyIdentityAsync / UpsertAsync
//   - OrgUnitDeclarationService.cs calling the parameterless mint, or any compensation delete. Both still
//     exist on the CONCRETE OrgUnitRepository (test fixtures seed headers with them), so removing them from
//     the interface does not stop this one service from reaching for them again.
//   - OrgUnitDeclarationViewModel.cs — the file that actually regressed — mentioning either, naming any
//     VersionOperationKind member, or calling ANY org-unit writer (widened from UpsertAsync-only on
//     2026-08-21: the interface still declares Close/Cancel/Delete, so the narrow leg left
//     this very screen able to close a root without the service's gate and stay green).
//   - LEG 4 (2026-08-21): a PRODUCTION-WIDE directory scan for a receiver-qualified org-unit
//     writer call anywhere outside OrgUnitDeclarationService.cs. Legs 2/3 read named files, so a NEW
//     in-module caller of the concrete internal OrgUnitRepository could re-parent or close a root with all
//     three legs green — that is the hole leg 4 exists to close.
//
// THE BOUNDARY IS FOUR-LEGGED (three legs from one review round, the fourth from the next), because
// no single assertion states it: the service must DECLARE Edit (leg 1), the repository interface must NOT
// declare an UpsertAsync (leg 2), the screen must neither call a writer nor label one (legs 3a/3b), and no
// other production file may name a writer on an org-unit receiver (leg 4). Each leg has its own falsifying
// mutation recorded in the plan's execution log — a four-legged guard where only one leg can redden is a
// one-legged guard wearing a costume.
//
// ⚠️ WHAT THIS FILE DOES NOT PROVE — read before citing it as "the boundary is enforced":
//   - It is a CONVENTION guard, not a compiler-enforced boundary. IOrgUnitRepository still declares
//     CloseVersionAsync / CancelPlanAsync / DeleteVersionAsync; anyone resolving that interface can call
//     them and skip the service's root gate, authorization and BOTH audit rows. Leg 4 makes such a caller
//     redden this suite; it does not make one impossible.
//   - Leg 4 matches by RECEIVER NAME (`orgUnit…` / `_orgUnits` / `OrgUnitRepository`, see the detector).
//     A caller that names the variable `repo` or `_r` is NOT caught. Widening the regex to every receiver
//     would collide with RoleRepository/RolePermissionRepository, which legitimately declare and call the
//     same method NAMES — that collision, not oversight, is why the scan is receiver-anchored.
//   - Comments (stripped before matching); the surviving composite overloads on the CONCRETE class, which
//     is where every write now lives; a declaration that moves into a partial class; a write method whose
//     return type is not `Task…`; a NEW write member added to IOrgUnitRepository under a different name —
//     the interface Facts name three members and are not a general read-only proof.
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

    // LEG 3a (negative): the screen does not reach a writer directly. PreviewUpsertAsync is a READ and
    // stays on the interface — the detector's leading `.` is what keeps it from matching.
    //
    // WIDENED 2026-08-21 from UpsertAsync-only to EVERY writer the interface still declares.
    // This screen holds IOrgUnitRepository for its reads and is the ONLY production holder of it, so a
    // `_orgUnits.CloseVersionAsync(...)` here would close a root with no root gate, no authorization and
    // neither audit row — and the narrow leg would have stayed green through all of it.
    [Fact]
    public void OrgUnitDeclarationViewModel_does_not_call_any_org_unit_writer()
    {
        OrgUnitWritePathAbsenceDetector.CallsAnyWriter(ReadOrgUnitDeclarationViewModel()).Should().BeFalse(
            "the declaration screen must not write a version itself, by ANY of the writers "
            + "IOrgUnitRepository still declares; " + WriteClaim + ".");
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

    // LEG 4 (negative, production-wide) — the leg the three named-file legs could not supply.
    // Shape borrowed from AST.Meta.Tests/WritePathBusinessDateTests.cs:52-77 (directory scan → named
    // offender list), not invented here.
    //
    // The concrete OrgUnitRepository is `internal sealed`, so an in-module caller is the realistic breach:
    // it needs no interface, no DI change and no new file. Measured 2026-08-21 before this leg existed:
    // 64 direct callers of the concrete writers, ALL of them tests — so this starts as a guard hole, not a
    // live defect. Tests are excluded on purpose: fixtures legitimately seed versions through the writers.
    [Fact]
    public void No_production_file_outside_the_service_calls_an_org_unit_writer()
    {
        var root = MetaTest.RepoRoot();

        var offenders = ProductionDirectories
            .Select(dir => Path.Combine(root, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(path => !MetaTest.IsGenerated(root, path))
            .Where(path => !Path.GetFileName(path).Equals("OrgUnitDeclarationService.cs", StringComparison.Ordinal))
            .Where(path => OrgUnitWritePathAbsenceDetector.CallsOrgUnitWriterOnAnOrgUnitReceiver(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        // Joined, not BeEmpty on the collection: BeEmpty names only the first offender, and a boundary that
        // slips usually slips in more than one file at once (same reasoning as WritePathBusinessDateTests).
        string.Join(", ", offenders).Should().BeEmpty(
            "these production files call an org-unit version writer directly, skipping the root-close gate, "
            + "the scope check and the audit rows that live in OrgUnitDeclarationService; " + WriteClaim
            + ". If a new legitimate caller is intended, that is an architecture-boundary decision "
            + "(rule-module-boundary §3) — widen this list deliberately, never to make a red test green.");
    }

    // AST.UI is absent by design: it references no repository. AST.Meta.Tests scans SOURCE, so adding a
    // directory here costs a file read, not a project reference.
    private static readonly string[] ProductionDirectories =
    [
        "AST.Core", "AST.Infrastructure", "AST.Modules.IAM", "AST.Shell", "AST.App", "AST",
    ];

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
    public void Detects_every_writer_the_interface_still_declares(string source)
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

    // Leg 4's receiver anchor. The NEGATIVE cases are the point: they are what stops the production-wide
    // scan from reddening on the role side, which legitimately calls the identical method names.
    [Theory]
    [InlineData("await orgUnitRepository.CloseVersionAsync(context, id, versionId, newTo, date, user, note);")]
    [InlineData("await _orgUnits.UpsertAsync(orgUnitId, period, code, full, short, parentId, kind, user, reason);")]
    [InlineData("await _orgUnitRepository.CancelPlanAsync(id, versionId, today, user, \"\");")]
    public void Leg4_detects_a_writer_on_an_org_unit_receiver(string source)
    {
        OrgUnitWritePathAbsenceDetector.CallsOrgUnitWriterOnAnOrgUnitReceiver(source).Should().BeTrue();
    }

    [Theory]
    [InlineData("await roleRepository.CloseVersionAsync(context, roleId, versionId, newTo, date, user, note);")]
    [InlineData("await rolePermissionRepository.CancelPlanAsync(context, id, versionId, today, user, reason);")]
    [InlineData("base.CloseVersionAsync(context, orgUnitId, versionId, newTo, operationDate, recordedBy, reason);")]
    [InlineData("var affected = await _orgUnits.PreviewUpsertAsync(orgUnitId, period);")]
    [InlineData("public async Task<ErrorOr<UpsertResult>> UpsertAsync(")]
    public void Leg4_ignores_other_receivers_reads_and_declarations(string source)
    {
        OrgUnitWritePathAbsenceDetector.CallsOrgUnitWriterOnAnOrgUnitReceiver(source).Should().BeFalse();
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

    // Every writer IOrgUnitRepository still declares, not just UpsertAsync. Receiver-free, so
    // it is only safe on a file that touches ONE repository — OrgUnitDeclarationViewModel does. Do NOT
    // reuse it for a directory scan: RoleRepository declares the same method NAMES.
    // The leading `\.` still carries PreviewUpsertAsync's exclusion.
    public static bool CallsAnyWriter(string source) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            StripLineComments(source),
            @"\.(?:UpsertAsync|CloseVersionAsync|CancelPlanAsync|DeleteVersionAsync)\s*\(");

    // Receiver-ANCHORED variant for leg 4's production-wide scan. `roleRepository.CloseVersionAsync(...)`
    // is legitimate and must not match, so the receiver — not the method name — is what identifies an
    // org-unit write. Covers the spellings this codebase actually uses: `_orgUnits`, `orgUnitRepository`,
    // `OrgUnitRepository`. `base.CloseVersionAsync(...)` inside OrgUnitRepository itself does not match
    // (receiver is `base`), which is why that file needs no exclusion.
    // KNOWN GAP, stated in the file header too: a receiver named `repo` slips through.
    private static readonly System.Text.RegularExpressions.Regex OrgUnitWriterOnOrgUnitReceiver = new(
        @"\b_?[Oo]rg[Uu]nits?(?:Repository|Repo)?\s*\.\s*(?:UpsertAsync|CloseVersionAsync|CancelPlanAsync|DeleteVersionAsync)\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool CallsOrgUnitWriterOnAnOrgUnitReceiver(string source) =>
        OrgUnitWriterOnOrgUnitReceiver.IsMatch(StripLineComments(source));

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
