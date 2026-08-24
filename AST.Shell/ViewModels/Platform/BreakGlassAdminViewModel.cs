using System.Collections.ObjectModel;
using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Security;
using AST.Core.Presentation;
using AST.Shell.Session;
using Prism.Commands;
using Prism.Mvvm;

namespace AST.Shell.ViewModels.Platform;

public sealed record BreakGlassAdminRow(string User, string CreatedDisplay);

public sealed class BreakGlassAdminViewModel : BindableBase
{
    private readonly IBreakGlassAdminService _service;
    private readonly IAdminSession _session;
    private readonly ICurrentWindowsUser _currentUser;

    public BreakGlassAdminViewModel(
        IBreakGlassAdminService service, IAdminSession session, ICurrentWindowsUser currentUser)
    {
        _service = service;
        _session = session;
        _currentUser = currentUser;
        _isAuthenticated = session.IsAuthenticated;
        session.Changed += OnSessionChanged;
    }

    public ObservableCollection<BreakGlassAdminRow> Admins { get; } = new();

    private string _newAdmin = string.Empty;
    public string NewAdmin { get => _newAdmin; set => SetProperty(ref _newAdmin, value); }

    // Health drives the corruption warning; it is no longer shown as a field (file-info block removed in v2).
    private BreakGlassHealth _health;
    public BreakGlassHealth Health { get => _health; private set => SetProperty(ref _health, value); }

    private string _filePath = string.Empty;
    public string FilePath { get => _filePath; private set => SetProperty(ref _filePath, value); }

    private bool _isAuthenticated;
    public bool IsAuthenticated { get => _isAuthenticated; private set => SetProperty(ref _isAuthenticated, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private StatusSeverity _severity = StatusSeverity.None;
    public StatusSeverity Severity { get => _severity; private set => SetProperty(ref _severity, value); }

    // Tracks whether the admin list has a real Add/Remove pending since the last load/save.
    // A Sign & Save with no pending change must not write a new signed version / audit record (#10).
    private bool _isDirty;

    // Surfaces the pending-change flag above so the screen can gate leaving on it: a typed-but-unadded name
    // or an Add/Remove not yet signed & saved is work the operator would lose.
    public bool HasUnsavedInput => _isDirty || !string.IsNullOrWhiteSpace(NewAdmin);

    // Drop the entry field and any unsaved Add/Remove. Clears in memory rather than reloading: the reload
    // happens on re-entry (AdminAuthViewModel.OnViewLoaded), and reloading HERE would both re-populate the
    // list while leaving the screen and — worse — silently keep the pending edits whenever File B could not
    // be read (a share hiccup), so a later Sign & Save would sign a list the operator believes they abandoned.
    public void ClearForm()
    {
        NewAdmin = string.Empty;
        ClearData();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private DelegateCommand? _loadCommand;
    public DelegateCommand LoadCommand => _loadCommand ??= new DelegateCommand(ExecuteLoad);

    private DelegateCommand? _addCommand;
    public DelegateCommand AddCommand => _addCommand ??= new DelegateCommand(ExecuteAdd);

    private DelegateCommand<string>? _removeCommand;
    public DelegateCommand<string> RemoveCommand => _removeCommand ??= new DelegateCommand<string>(ExecuteRemove);

    private DelegateCommand? _saveCommand;
    public DelegateCommand SaveCommand =>
        _saveCommand ??= new DelegateCommand(ExecuteSave, () => IsAuthenticated && Admins.Count >= 1);

    // Data is gated by auth (the grid frame stays a fixed size in the View; only the rows appear/clear).
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        IsAuthenticated = _session.IsAuthenticated;
        if (IsAuthenticated) ExecuteLoad();
        else ClearData();
        _saveCommand?.RaiseCanExecuteChanged();
    }

    private void ClearData()
    {
        Admins.Clear();
        FilePath = string.Empty;
        StatusMessage = null;
        Severity = StatusSeverity.None;
        // Dropping the rows drops what could be pending on them: leaving the flag set would make a visibly
        // empty screen still claim unsaved work and pop the leave-confirmation with nothing to lose.
        _isDirty = false;
    }

    private void ExecuteLoad()
    {
        var result = _service.Load();
        if (result.IsError)
        {
            Severity = StatusSeverity.Error;
            StatusMessage = result.FirstError.Description;
            return;
        }

        var view = result.Value;
        Admins.Clear();
        foreach (var a in view.Admins)
            Admins.Add(new BreakGlassAdminRow(a.User, a.CreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—"));
        Health = view.Health;
        FilePath = view.FilePath;
        SaveCommand.RaiseCanExecuteChanged();
        _isDirty = false;

        if (Health is BreakGlassHealth.Tampered or BreakGlassHealth.Unreadable)
        {
            Severity = StatusSeverity.Error;
            StatusMessage = Health == BreakGlassHealth.Tampered
                ? "Danh sách bị sửa đổi."
                : "Không đọc được danh sách.";
        }
    }

    private void ExecuteAdd()
    {
        var n = WindowsUsernameNormalizer.Normalize(NewAdmin);
        if (n is null) return;
        if (!Admins.Any(x => x.User == n)) { Admins.Add(new BreakGlassAdminRow(n, "—")); _isDirty = true; }
        NewAdmin = string.Empty;
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void ExecuteRemove(string? user)
    {
        if (user is null) return;
        if (Admins.Count <= 1)
        {
            Severity = StatusSeverity.Warning;
            StatusMessage = "Duy trì ít nhất một người cứu hộ.";
            return;
        }
        var row = Admins.FirstOrDefault(x => x.User == user);
        if (row is null) return;
        Admins.Remove(row);
        _isDirty = true;
        if (WindowsUsernameNormalizer.Normalize(user) == WindowsUsernameNormalizer.Normalize(_currentUser.Username))
        {
            Severity = StatusSeverity.Warning;
            StatusMessage = "Bạn vừa xóa user của mình.";
        }
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void ExecuteSave()
    {
        if (!_isDirty)
        {
            Severity = StatusSeverity.Warning;
            StatusMessage = "Không có thông tin để ký và lưu.";
            return;
        }

        var result = _service.Save(Admins.Select(x => x.User).ToList(), _session.PrivateKey, _session.Passphrase);
        if (result.IsError)
        {
            Severity = StatusSeverity.Error;
            StatusMessage = result.FirstError.Description;
            return;
        }
        ExecuteLoad(); // refresh rows so the new user's derived created-date shows without leaving the screen
        Severity = StatusSeverity.Success;
        StatusMessage = "Đã lưu.";
        Saved?.Invoke(this, EventArgs.Empty);
    }

    // Raised after a successful Sign & Save so the history view can refresh (a new audit record was appended).
    public event EventHandler? Saved;
}
