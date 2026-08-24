using FluentAssertions;

namespace AST.Meta.Tests;

// Guards "one operation, one business date" (docs/design-effective-period.md §3) at the only place it can
// be guarded cheaply: the shape of the source.
//
// COVERED by this file:
//   - AST.Modules.IAM/Data/Repositories/*.cs — subclasses must not declare their own captured
//     IBusinessDateProvider (field or auto-property), including modifierless fields and type-alias forms.
//
// NOT COVERED (locked elsewhere):
//   - AST.Infrastructure/VersionedRepository.cs — the base engine legitimately holds `_scopeToday` for the
//     READ path only. TASK 0 (2026-08-11) forbids write-path reads via that field in a comment on the field
//     itself; this meta-test deliberately does not scan the engine base.
//
// NOT COVERED (accepted gap):
//   - Type-alias obfuscation beyond a single `using Alias = …IBusinessDateProvider` line in the same file
//     is not scanned; an alias declared in another file would not be resolved. The directory scan still
//     catches the common `IBusinessDateProvider` spelling.
//
// Behavioural tests (OperationDateGuardTests, etc.) catch a specific midnight rollover; this catches the
// declaration SHAPE that makes the defect easy to re-introduce.
public sealed class WritePathBusinessDateTests
{
    // Positive samples — each form the legacy regex missed (brief 066 §1).
    [Theory]
    [InlineData("IBusinessDateProvider _dates = dates;")]
    [InlineData("private IBusinessDateProvider Dates { get; }")]
    [InlineData(
        """
        using Clock = AST.Core.Time.IBusinessDateProvider;
        Clock _d = dates;
        """)]
    public void Detects_captured_provider_forms_the_legacy_regex_missed(string source)
    {
        WritePathClockDetector.Detects(source).Should().BeTrue();
    }

    // Negative samples — must stay green; a ctor/method parameter or namespace import is not a capture.
    [Theory]
    [InlineData("internal sealed class RoleRepository(\n    IBusinessDateProvider dates)\n{ }")]
    [InlineData("void M(IBusinessDateProvider dates) { }")]
    [InlineData("using AST.Core.Time;")]
    public void Ignores_legitimate_non_capture_uses_of_the_provider_type(string source)
    {
        WritePathClockDetector.Detects(source).Should().BeFalse();
    }

    [Fact]
    public void No_versioned_repository_subclass_keeps_its_own_business_date_provider()
    {
        var root = MetaTest.RepoRoot();
        var repositoryDir = Path.Combine(root, "AST.Modules.IAM", "Data", "Repositories");

        var offenders = Directory
            .EnumerateFiles(repositoryDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !MetaTest.IsGenerated(root, path))
            .Where(path => WritePathClockDetector.Detects(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        // Asserted as one joined string, not as a collection: BeEmpty's failure message reports only the
        // first item, and when this guard fires it is usually because the SAME field was added to several
        // repositories at once — the reader needs all of them named.
        string.Join(", ", offenders).Should().BeEmpty(
            "a repository that captures IBusinessDateProvider can read a clock on the write path, which is how "
            + "one operation ends up running on two business dates (docs/design-effective-period.md §3). A guard "
            + "that needs today takes an OperationDate parameter instead — see IRolePermissionRepository.UpsertAsync. "
            + "The base engine's READ-path field is VersionedRepository._scopeToday — see that file's TASK 0 comment; "
            + "this scan covers IAM repository subclasses only.");
    }
}
