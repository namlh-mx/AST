using AST.Core.Data;
using AST.Core.Presentation;
using FluentAssertions;

namespace AST.Shell.Tests.Presentation;

// Headless mapping tests for the version operation kind. Text is product UI copy (Vietnamese),
// single-homed here (mirrors VersionStatusPresentationTests).
public class VersionOperationKindPresentationTests
{
    [Theory]
    [InlineData(VersionOperationKind.Add, "Thêm")]
    [InlineData(VersionOperationKind.Edit, "Sửa")]
    [InlineData(VersionOperationKind.Close, "Đóng")]
    [InlineData(VersionOperationKind.Cancel, "Hủy")]
    [InlineData(VersionOperationKind.Replace, "Thay thế")]
    public void ToVietnameseText_maps_each_kind(VersionOperationKind kind, string expected)
        => Assert.Equal(expected, VersionOperationKindPresentation.ToVietnameseText(kind));

    // Reflects over the enum rather than listing its members, because a hand-written list is exactly what
    // let a fifth value ship with a blank label, a green build and green suites (a labelling gap found in review).
    [Fact]
    public void EveryOperationKindHasANonEmptyVietnameseLabel()
    {
        foreach (var kind in Enum.GetValues<VersionOperationKind>())
        {
            VersionOperationKindPresentation.ToVietnameseText(kind)
                .Should().NotBeNullOrWhiteSpace($"{kind} has no Vietnamese label");
        }
    }
}
