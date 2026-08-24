using AST.Core.Iam;

namespace AST.Shell.Navigation;

public sealed class MenuGroupCatalog : IMenuGroupCatalog
{
    public IReadOnlyList<MenuGroup> Groups { get; } =
    [
        new(MenuGroupCodes.TransactionAccounting, "Kế toán giao dịch", "ReceiptMoney24", 1),
        new(MenuGroupCodes.InternalAccounting, "Kế toán nội bộ", "ClipboardTaskListLtr24", 2),
        new(MenuGroupCodes.Treasury, "Tiền tệ - kho quỹ", "Money24", 3),
        new(MenuGroupCodes.ManagementReport, "Báo cáo quản trị", "DataTrending24", 4),
        new(MenuGroupCodes.Inspection, "Kiểm tra - giám sát", "ShieldTask24", 5),
    ];
}
