using AST.Core.Data;

namespace AST.Core.Presentation;

// Single home of the version-operation-kind → VN label mapping (mirrors VersionStatusPresentation). Labels
// are product UI copy (the one allowed Vietnamese). Provenance, all five: `Add`/`Edit` were confirmed
// against the Screen A history-row labels; `Close`/`Cancel` followed the same terse noun-form style; and
// `Replace` → "Thay thế" is the requester's own wording for the gesture, settled 2026-09-04 and pinned
// by name in VersionOperationKindPresentationTests. ⚠ This list is the WHOLE of VersionOperationKind and
// must stay that way — the four-value version of this sentence outlived the fifth arm from 2026-08-24
// (`8198a67`) to 2026-09-05 (F-244-03, Assurance Advisor review round 6).
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
