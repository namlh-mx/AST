namespace AST.Shell.Presentation.Iam;

// The single home of the nine IAM declaration-screen operator sentences. A screen may not author
// prose: it references these constants. Sentences are requester-settled (the operator-message rules)
// -- an agent may propose message content and may never decide it.
//
// Routing stays in each screen's ViewModel. This type does not map codes to sentences.
//
// The ninth sentence, the system catch-all, already lives at PlatformErrorDescriber.CatchAll and
// is not repeated here.
public static class IamDeclarationMessages
{
    public const string StaleDataReload = "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.";
    public const string PeriodOverlap = "Kỳ hiệu lực bị trùng lặp một phần hoặc toàn phần.";
    public const string CloseDateOutsideDeclaredPeriod = "Ngày kết thúc hiệu lực không nằm trong kỳ hiệu lực đã khai báo.";
    public const string SavedDisplayNotUpdated = "Đã lưu. Dữ liệu hiển thị chưa cập nhật.";
    public const string ConcurrentDeclaration = "Dữ liệu đang được người dùng khác khai báo.";
    public const string CloseDateBeforeStart = "Ngày kết thúc hiệu lực không được trước ngày bắt đầu hiệu lực.";
    public const string PermissionDenied = "Người dùng không được cấp quyền.";
    public const string HistoryLoadFailed = "Ứng dụng không tải được dữ liệu lịch sử.";
}
