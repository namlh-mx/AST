using System.Reflection;
using AST.Core.Iam;
using FluentAssertions;

namespace AST.Core.Tests.Iam;

public class BreakGlassAdminRulesTests
{
    [Fact]
    public void NormalizeDistinct_strips_domain_and_dedupes()
        => Assert.Equal(
            new[] { "namlehoai4", "boss2" },
            BreakGlassAdminRules.NormalizeDistinct(new[] { "CORP\\namlehoai4", "NAMLEHOAI4", "boss2@corp.local" }));

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

    // ValidateNonEmpty's code was substituted to Codes.Empty, and the existing
    // ValidateNonEmpty_blocks_empty_list asserts only IsError + ErrorType -- never the Code, so the
    // substitution had no behaviour-preserving proof. Asserts the LITERAL, not the constant.
    [Fact]
    public void ValidateNonEmpty_emits_the_empty_code()
    {
        var r = BreakGlassAdminRules.ValidateNonEmpty(Array.Empty<string>());

        r.FirstError.Code.Should().Be("BreakGlass.Empty");
    }

    [Fact]
    public void ValidateNonEmpty_allows_non_empty_list()
        => Assert.False(BreakGlassAdminRules.ValidateNonEmpty(new[] { "a" }).IsError);

    // Pin the actual wire string VALUE, not just the symbol.
    [Fact]
    public void Codes_PinTheActualWireStringValues()
    {
        BreakGlassAdminRules.Codes.Empty.Should().Be("BreakGlass.Empty");
    }

    // Reflects independently over Codes' own public string fields and asserts All is exactly that
    // set — fails if a 2nd constant is declared without also being added to All. The filter catches
    // BOTH `const string` and `static readonly string`; only All itself is excluded by FieldType.
    [Fact]
    public void Codes_All_ContainsExactlyTheDeclaredConstants()
    {
        var declaredCodes = typeof(BreakGlassAdminRules.Codes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.FieldType == typeof(string) && (f.IsLiteral || f.IsInitOnly))
            .Select(f => (string)(f.IsLiteral ? f.GetRawConstantValue()! : f.GetValue(null)!))
            .ToArray();

        BreakGlassAdminRules.Codes.All.Should().BeEquivalentTo(declaredCodes);
    }
}
