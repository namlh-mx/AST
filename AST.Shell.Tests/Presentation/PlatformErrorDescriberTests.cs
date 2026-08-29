using AST.Core.Iam;
using AST.Core.Security;
using AST.Core.Startup;
using AST.Shell.Presentation;
using ErrorOr;
using FluentAssertions;
using Xunit;

namespace AST.Shell.Tests.Presentation;

public class PlatformErrorDescriberTests
{
    private static IReadOnlyList<string> AllPlatformCodes() =>
    [
        .. StartupCodes.All,
        .. ConfigErrors.Codes.All,
        .. BreakGlassAdminRules.Codes.All,
    ];

    [Fact]
    public void Catalog_and_NotDescribed_partition_the_closed_platform_code_set()
    {
        var all = AllPlatformCodes();
        var classified = PlatformErrorDescriber.Catalog.Keys
            .Concat(PlatformErrorDescriber.NotDescribed.Keys)
            .ToList();

        classified.Should().OnlyHaveUniqueItems(
            "a code classified twice means one of the two claims is unread");
        classified.Should().BeEquivalentTo(all,
            "every platform code is either answered by the catalog or declared not-described, "
            + "and the catalog may not name a code outside the three Codes.All lists");
    }

    [Theory]
    [InlineData(StartupCodes.DbAccessDenied)]
    [InlineData(StartupCodes.DbConnectFailed)]
    [InlineData(ConfigErrors.Codes.SignatureInvalid)]
    [InlineData(ConfigErrors.Codes.ContentInvalid)]
    [InlineData(ConfigErrors.Codes.IoError)]
    [InlineData(ConfigErrors.Codes.KeyMismatch)]
    [InlineData(ConfigErrors.Codes.KeyUnreadable)]
    [InlineData(ConfigErrors.Codes.KeyRequired)]
    [InlineData(ConfigErrors.Codes.CurrentUserUnknown)]
    [InlineData(BreakGlassAdminRules.Codes.Empty)]
    public void Describe_returns_the_settled_sentence_and_never_the_catch_all(string code)
    {
        var sentence = PlatformErrorDescriber.Describe(code);

        sentence.Should().Be(PlatformErrorDescriber.Catalog[code]);
        sentence.Should().NotBe(PlatformErrorDescriber.CatchAll,
            "a code with its own settled sentence must not silently fall through");
    }

    [Fact]
    public void Describe_falls_through_to_the_catch_all_for_a_code_with_no_entry()
    {
        PlatformErrorDescriber.Describe("ZZZ.NeverExists")
            .Should().Be(PlatformErrorDescriber.CatchAll);
    }

    [Fact]
    public void Describe_reads_the_code_off_the_Error_not_its_Description()
    {
        var error = Error.Validation(ConfigErrors.Codes.KeyMismatch, "raw description text");

        PlatformErrorDescriber.Describe(error)
            .Should().Be(PlatformErrorDescriber.Catalog[ConfigErrors.Codes.KeyMismatch])
            .And.NotBe("raw description text");
    }

    [Fact]
    public void Every_NotDescribed_code_falls_through_to_the_catch_all()
    {
        foreach (var code in PlatformErrorDescriber.NotDescribed.Keys)
        {
            PlatformErrorDescriber.Describe(code)
                .Should().Be(PlatformErrorDescriber.CatchAll,
                    "a not-described code has no catalog row by design");
        }
    }
}
