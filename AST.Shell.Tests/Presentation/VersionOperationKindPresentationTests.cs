using AST.Core.Data;
using AST.Core.Presentation;

namespace AST.Shell.Tests.Presentation;

// Headless mapping tests for the 4-state version operation kind. Text is product UI copy (Vietnamese),
// single-homed here (mirrors VersionStatusPresentationTests).
public class VersionOperationKindPresentationTests
{
    [Theory]
    [InlineData(VersionOperationKind.Add, "Thêm")]
    [InlineData(VersionOperationKind.Edit, "Sửa")]
    [InlineData(VersionOperationKind.Close, "Đóng")]
    [InlineData(VersionOperationKind.Cancel, "Hủy")]
    public void ToVietnameseText_maps_each_kind(VersionOperationKind kind, string expected)
        => Assert.Equal(expected, VersionOperationKindPresentation.ToVietnameseText(kind));
}
