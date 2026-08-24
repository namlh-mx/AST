using AST.Core.Data;

namespace AST.Core.Presentation;

// Single home of the version-operation-kind → VN label mapping (mirrors VersionStatusPresentation). Labels
// are product UI copy (the one allowed Vietnamese), confirmed against the Screen A history-row labels
// (Add/Edit) plus Close/Cancel following the same terse noun-form style.
[SharedComponent]
public static class VersionOperationKindPresentation
{
    public static string ToVietnameseText(VersionOperationKind kind) => kind switch
    {
        VersionOperationKind.Add => "Thêm",
        VersionOperationKind.Edit => "Sửa",
        VersionOperationKind.Close => "Đóng",
        VersionOperationKind.Cancel => "Hủy",
        _ => string.Empty,
    };
}
