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
        VersionOperationKind.Replace => "Thay thế",
        // A label map is called at render time — throwing would turn a missing label into a crashed screen.
        // The reflection test in VersionOperationKindPresentationTests is the mechanical guard that catches
        // a missing label at test time instead; a guard, not a promise.
        _ => string.Empty,
    };
}
