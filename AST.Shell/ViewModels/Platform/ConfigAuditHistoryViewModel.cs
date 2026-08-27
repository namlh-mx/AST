using System.Collections.ObjectModel;
using System.Globalization;
using AST.Core.Security;
using AST.Core.Presentation;
using AST.Shell.Session;
using Prism.Commands;
using Prism.Mvvm;

namespace AST.Shell.ViewModels.Platform;

// One row per user change (a Save that added/removed several users expands to several rows);
// records without a user diff (File A create, signature-verify failure) render as a single row.
public sealed record ConfigAuditRow(
    string TimeLocal, string Actor, string Target, string Operation, string User, bool Signed);

public sealed class ConfigAuditHistoryViewModel : BindableBase
{
    private readonly IConfigAuditLog _log;
    private readonly IAdminSession _session;

    public ConfigAuditHistoryViewModel(IConfigAuditLog log, IAdminSession session)
    {
        _log = log;
        _session = session;
        session.Changed += OnSessionChanged;
    }

    public ObservableCollection<ConfigAuditRow> Rows { get; } = new();

    private string? _integrityMessage;
    public string? IntegrityMessage { get => _integrityMessage; private set => SetProperty(ref _integrityMessage, value); }

    private StatusSeverity _severity = StatusSeverity.None;
    public StatusSeverity Severity { get => _severity; private set => SetProperty(ref _severity, value); }

    private DelegateCommand? _loadCommand;
    public DelegateCommand LoadCommand => _loadCommand ??= new DelegateCommand(ExecuteLoad);

    private DelegateCommand? _verifyCommand;
    public DelegateCommand VerifyCommand => _verifyCommand ??= new DelegateCommand(ExecuteVerify);

    // Reload rows + re-verify integrity (e.g. after a Sign & Save appended a new audit record).
    public void Refresh()
    {
        ExecuteLoad();
        ExecuteVerify();
    }

    // History auto-loads once the admin authenticates and clears when the session ends (no reveal button).
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_session.IsAuthenticated)
        {
            ExecuteLoad();
            ExecuteVerify();
        }
        else
        {
            Rows.Clear();
            IntegrityMessage = null;
            Severity = StatusSeverity.None;
        }
    }

    private void ExecuteLoad()
    {
        var result = _log.Read();
        if (result.IsError)
        {
            Severity = StatusSeverity.Error;
            IntegrityMessage = result.FirstError.Description;
            return;
        }

        Rows.Clear();
        foreach (var r in result.Value)
        {
            var time = FormatTsLocal(r.Content.TsUtc);
            var actor = r.Content.Actor.User;
            var target = MapTarget(r.Content.Target);
            var signed = r.TipSig is not null;
            var diff = r.Content.Diff;

            if (diff is not null && (diff.Added.Count > 0 || diff.Removed.Count > 0))
            {
                foreach (var u in diff.Added)
                    Rows.Add(new ConfigAuditRow(time, actor, target, "Thêm", u, signed));
                foreach (var u in diff.Removed)
                    Rows.Add(new ConfigAuditRow(time, actor, target, "Xóa", u, signed));
            }
            else
            {
                Rows.Add(new ConfigAuditRow(time, actor, target, MapAction(r.Content.Action), "—", signed));
            }
        }
    }

    private void ExecuteVerify()
    {
        var result = _log.VerifyIntegrity();
        if (result.IsError)
        {
            Severity = StatusSeverity.Error;
            IntegrityMessage = result.FirstError.Description;
            return;
        }

        var integrity = result.Value;
        if (!integrity.ChainValid)
        {
            Severity = StatusSeverity.Error;
            IntegrityMessage = "Nhật ký cấu hình không toàn vẹn.";
            return;
        }
        if (!integrity.TipSignatureValid)
        {
            Severity = StatusSeverity.Error;
            IntegrityMessage = "Chữ ký không hợp lệ.";
            return;
        }

        Severity = StatusSeverity.Success;
        IntegrityMessage = "Nhật ký nguyên vẹn, chữ ký hợp lệ.";
    }

    private static string MapTarget(string target) => target switch
    {
        "FileB" => "Người cứu hộ",
        "FileA" => "Thông số kết nối",
        _ => target,
    };

    private static string MapAction(string action) => action switch
    {
        "Create" => "Tạo",
        "Update" => "Sửa",
        "Restore" => "Khôi phục",
        "SignatureVerifyFailed" => "Lỗi xác minh chữ ký",
        _ => action,
    };

    private static string FormatTsLocal(string tsUtc)
    {
        // yyyy-MM-dd HH:mm (16 chars): matches BreakGlass CreatedDisplay + the authenticated-key info,
        // and is locale-stable (unlike "g") so the Thời gian column has a fixed max width.
        if (DateTimeOffset.TryParse(tsUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dto))
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (DateTime.TryParse(tsUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return tsUtc;
    }
}
