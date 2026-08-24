using AST.Core.Iam;

namespace AST.Core.Tests.Iam;

public class BreakGlassAdminRulesTests
{
    [Fact]
    public void NormalizeDistinct_strips_domain_and_dedupes()
        => Assert.Equal(
            new[] { "alice", "bob" },
            BreakGlassAdminRules.NormalizeDistinct(new[] { "EXAMPLE\\alice", "ALICE", "bob@example.local" }));

    [Fact]
    public void NormalizeDistinct_drops_blank_and_whitespace_entries()
        => Assert.Equal(
            new[] { "a" },
            BreakGlassAdminRules.NormalizeDistinct(new[] { "  ", "", "A", "a" }));

    [Fact]
    public void Diff_reports_added_and_removed()
    {
        var d = BreakGlassAdminRules.Diff(new[] { "a", "b" }, new[] { "b", "c" });
        Assert.Equal(new[] { "c" }, d.Added);
        Assert.Equal(new[] { "a" }, d.Removed);
    }

    [Fact]
    public void Diff_of_identical_lists_is_empty()
    {
        var d = BreakGlassAdminRules.Diff(new[] { "a", "b" }, new[] { "a", "b" });
        Assert.Empty(d.Added);
        Assert.Empty(d.Removed);
    }

    [Fact]
    public void ValidateNonEmpty_blocks_empty_list()
    {
        var r = BreakGlassAdminRules.ValidateNonEmpty(Array.Empty<string>());
        Assert.True(r.IsError);
        Assert.Equal(ErrorOr.ErrorType.Validation, r.FirstError.Type);
    }

    [Fact]
    public void ValidateNonEmpty_allows_non_empty_list()
        => Assert.False(BreakGlassAdminRules.ValidateNonEmpty(new[] { "a" }).IsError);
}
