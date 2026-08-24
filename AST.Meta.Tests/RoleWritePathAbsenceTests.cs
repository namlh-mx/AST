using System.Text.RegularExpressions;
using FluentAssertions;

namespace AST.Meta.Tests;

// Guards the 089 deletion by scanning two source files. A migrated composite test stays green if a
// non-context RoleRepository.UpsertAsync returns, so this scan is the only cheap tripwire.
//
// HOW it reaches RoleRepository (internal sealed): SOURCE TEXT, not reflection. AST.Meta.Tests has
// no ProjectReference (see the csproj); InternalsVisibleTo would not help a project that does not
// compile against AST.Modules.IAM. Same shape as WritePathBusinessDateTests.
//
// COVERED:
//   - IRoleRepository.cs declaring UpsertAsync / CreateIdentityAsync / DeleteEmptyIdentityAsync
//   - RoleRepository.cs declaring UpsertAsync whose first parameter is not ICompositeWriteContext
//     and whose return type starts with the token `Task`
//   - RoleDeclarationService.cs CALLING the parameterless mint or any compensation delete. Both still
//     exist on the concrete repositories (test fixtures use them, and the backlog has callers
//     left to migrate), so removing them from the interface does not stop this one service from
//     reaching for them again — this is what keeps §7's "header and first version commit together"
//     from quietly regressing on the path that already regressed once.
//
// NOT COVERED: comments, the surviving composite overload, the same mistake made in ANOTHER file
// (this scan reads three named files);
//   - a declaration in another file (the scan reads RoleRepository.cs only — if that class ever
//     becomes partial, the guard does not follow it);
//   - a write method whose return type is not `Task…` (e.g. ValueTask<…>, or a non-async
//     ErrorOr<UpsertResult>);
//   - a write method added to IRoleRepository under a different name — the interface Facts name
//     three members, they are not a general read-only proof.
public sealed class RoleWritePathAbsenceTests
{
    private const string VersionCreationClaim =
        "role version creation goes only through IRoleDeclarationService";

    [Theory]
    [InlineData("Task<ErrorOr<UpsertResult>> UpsertAsync(\n        long roleId, Period period,")]
    [InlineData("public async Task<ErrorOr<UpsertResult>> UpsertAsync(\n        long roleId, EffectivePeriod period,")]
    public void Detects_non_context_UpsertAsync_declaration(string source)
    {
        RoleWritePathAbsenceDetector.HasNonContextUpsertAsync(source).Should().BeTrue();
    }

    [Theory]
    [InlineData("public async Task<ErrorOr<UpsertResult>> UpsertAsync(\n        ICompositeWriteContext context, long roleId,")]
    [InlineData("await roleRepo.UpsertAsync(\n                context, id, period,")]
    [InlineData("public async Task<long> CreateIdentityAsync()")]
    public void Ignores_composite_UpsertAsync_and_non_declarations(string source)
    {
        RoleWritePathAbsenceDetector.HasNonContextUpsertAsync(source).Should().BeFalse();
    }

    [Theory]
    [InlineData("UpsertAsync", "Task<ErrorOr<UpsertResult>> UpsertAsync(\n        long roleId, Period period,")]
    [InlineData("CreateIdentityAsync", "Task<long> CreateIdentityAsync();")]
    [InlineData("DeleteEmptyIdentityAsync", "Task DeleteEmptyIdentityAsync(long roleId);")]
    public void Detects_banned_IRoleRepository_members(string methodName, string source)
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(source, methodName).Should().BeTrue();
    }

    [Theory]
    [InlineData("UpsertAsync", "Task<IReadOnlyList<RoleVersionDto>> GetHistoryAsync(long? roleId = null);")]
    [InlineData("CreateIdentityAsync", "// Task<long> CreateIdentityAsync();")]
    public void Ignores_reads_and_commented_interface_members(string methodName, string source)
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(source, methodName).Should().BeFalse();
    }

    [Fact]
    public void IRoleRepository_does_not_declare_UpsertAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIRoleRepository(), "UpsertAsync").Should().BeFalse(
            "IRoleRepository must not declare UpsertAsync; " + VersionCreationClaim + ".");
    }

    [Fact]
    public void IRoleRepository_does_not_declare_CreateIdentityAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIRoleRepository(), "CreateIdentityAsync").Should().BeFalse(
            "IRoleRepository must not declare CreateIdentityAsync; " + VersionCreationClaim + ".");
    }

    [Fact]
    public void IRoleRepository_does_not_declare_DeleteEmptyIdentityAsync()
    {
        RoleWritePathAbsenceDetector.InterfaceDeclares(ReadIRoleRepository(), "DeleteEmptyIdentityAsync").Should().BeFalse(
            "IRoleRepository must not declare DeleteEmptyIdentityAsync; " + VersionCreationClaim + ".");
    }

    [Fact]
    public void RoleRepository_does_not_declare_a_non_context_UpsertAsync()
    {
        RoleWritePathAbsenceDetector.HasNonContextUpsertAsync(ReadRoleRepository()).Should().BeFalse(
            "RoleRepository must not declare a non-context UpsertAsync overload; " + VersionCreationClaim + ".");
    }

    [Theory]
    [InlineData("mintedGrantIds.Add(await rolePermissionRepository.CreateIdentityAsync());")]
    [InlineData("var newId = await roleRepository.CreateIdentityAsync( );")]
    [InlineData("Func<Task<long>> mint = roleRepository.CreateIdentityAsync;")]
    public void Detects_parameterless_mint_calls(string source)
    {
        RoleWritePathAbsenceDetector.CallsParameterlessMint(source).Should().BeTrue();
    }

    [Theory]
    [InlineData("var grantId = await rolePermissionRepository.CreateIdentityAsync(context);")]
    [InlineData("// var newId = await roleRepository.CreateIdentityAsync();")]
    public void Ignores_in_transaction_mint_calls(string source)
    {
        RoleWritePathAbsenceDetector.CallsParameterlessMint(source).Should().BeFalse();
    }

    [Fact]
    public void RoleDeclarationService_does_not_mint_outside_the_transaction()
    {
        RoleWritePathAbsenceDetector.CallsParameterlessMint(ReadRoleDeclarationService()).Should().BeFalse(
            "RoleDeclarationService must mint through the ICompositeWriteContext overload; a header minted " +
            "on its own connection can outlive the failure of its first version (design-effective-period.md §7).");
    }

    [Fact]
    public void RoleDeclarationService_does_not_compensate_a_minted_identity()
    {
        RoleWritePathAbsenceDetector.CallsCompensationDelete(ReadRoleDeclarationService()).Should().BeFalse(
            "a compensation delete here would mean an identity is again being minted outside the " +
            "transaction — the rollback is what removes it now (design-effective-period.md §7).");
    }

    private static string ReadIRoleRepository()
    {
        var root = MetaTest.RepoRoot();
        var path = Path.Combine(root, "AST.Core", "Iam", "Repositories", "IRoleRepository.cs");
        return File.ReadAllText(path);
    }

    private static string ReadRoleRepository()
    {
        var root = MetaTest.RepoRoot();
        var path = Path.Combine(root, "AST.Modules.IAM", "Data", "Repositories", "RoleRepository.cs");
        return File.ReadAllText(path);
    }

    private static string ReadRoleDeclarationService()
    {
        var root = MetaTest.RepoRoot();
        var path = Path.Combine(root, "AST.Modules.IAM", "RoleDeclarationService.cs");
        return File.ReadAllText(path);
    }
}

// Source-text detector. AST.Meta.Tests does not reference production assemblies.
internal static class RoleWritePathAbsenceDetector
{
    // Nested generics (`Task<ErrorOr<UpsertResult>>`) are allowed; `[^>]+` is not.
    private const string TaskReturn = @"Task(?:<[\w.<>,\s]+>)?";

    private static readonly Regex NonContextUpsertDecl = new(
        TaskReturn + @"\s+UpsertAsync\s*\(\s*(?!ICompositeWriteContext\b)(?=\S)",
        RegexOptions.Compiled);

    // Matches EVERY mention of the mint that is not immediately applied to `context` — the empty argument
    // list, but also a method-group reference (`Func<Task<long>> mint = repo.CreateIdentityAsync;`), which
    // selects the out-of-transaction overload while containing no `()` at all.
    // Deliberately strict about the argument's NAME: this service's composite delegate parameter is
    // `context`, so anything else is either a different overload or a rename that should re-read this guard.
    private static readonly Regex ParameterlessMintCall = new(
        @"\.CreateIdentityAsync\b(?!\s*\(\s*context\b)",
        RegexOptions.Compiled);

    public static bool CallsParameterlessMint(string source) =>
        ParameterlessMintCall.IsMatch(StripLineComments(source));

    // Any mention at all: this service has no legitimate reason to delete an empty identity now.
    public static bool CallsCompensationDelete(string source) =>
        StripLineComments(source).Contains("DeleteEmptyIdentityAsync", StringComparison.Ordinal);

    public static bool HasNonContextUpsertAsync(string source) =>
        NonContextUpsertDecl.IsMatch(StripLineComments(source));

    public static bool InterfaceDeclares(string source, string methodName)
    {
        var stripped = StripLineComments(source);
        var pattern = TaskReturn + $@"\s+{Regex.Escape(methodName)}\s*\(";
        return Regex.IsMatch(stripped, pattern);
    }

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
