using AST.Core.Data;
using AST.Core.Startup;
using AST.Core.Presentation;
using AST.Shell.Presentation;
using AST.Shell.Session;
using Prism.Commands;
using Prism.Mvvm;

namespace AST.Shell.ViewModels.Platform;

public sealed class ConnectionDeclarationViewModel : BindableBase, IDeclarationForm, IStatusBanner
{
    private readonly IConfigDeclarationService _declaration;
    private readonly IConnectionTester _tester;
    private readonly IAdminSession _session;
    private readonly IStartupRunner _startupRunner;

    public ConnectionDeclarationViewModel(
        IConfigDeclarationService declaration, IConnectionTester tester,
        IAdminSession session, IStartupRunner startupRunner)
    {
        _declaration = declaration;
        _tester = tester;
        _session = session;
        _startupRunner = startupRunner;
    }

    private string _host = string.Empty;
    public string Host { get => _host; set { if (SetProperty(ref _host, value)) InvalidateTestGate(); } }

    // Held as the operator's raw text like every other field: any entry — even a half-typed or invalid one —
    // commits on each keystroke and marks the form dirty, so the leave-confirm treats the port like the rest.
    // A typed int? would silently drop invalid text on the failed binding conversion and the leave-confirm
    // would miss it. Parsed at the test/save gate (IsValidPort); a blank form is an empty string.
    private string _port = string.Empty;
    public string Port { get => _port; set { if (SetProperty(ref _port, value)) InvalidateTestGate(); } }

    private string _database = string.Empty;
    public string Database { get => _database; set { if (SetProperty(ref _database, value)) InvalidateTestGate(); } }

    private string _user = string.Empty;
    public string User { get => _user; set { if (SetProperty(ref _user, value)) InvalidateTestGate(); } }

    private string _password = string.Empty;
    public string Password { get => _password; set { if (SetProperty(ref _password, value)) InvalidateTestGate(); } }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private StatusSeverity _severity = StatusSeverity.None;
    public StatusSeverity Severity { get => _severity; private set => SetProperty(ref _severity, value); }

    private bool _isTestPassed;
    public bool IsTestPassed { get => _isTestPassed; private set => SetProperty(ref _isTestPassed, value); }

    private bool _isDirty;
    public bool IsDirty { get => _isDirty; private set => SetProperty(ref _isDirty, value); }

    private IReadOnlyList<ConnectionHistoryEntry> _history = Array.Empty<ConnectionHistoryEntry>();
    public IReadOnlyList<ConnectionHistoryEntry> History { get => _history; private set => SetProperty(ref _history, value); }

    private void InvalidateTestGate() { IsTestPassed = false; IsDirty = true; }

    // Deliberately does NOT prefill the connection fields from File A: the declaration form always opens
    // blank. The configuration in force is the newest row of the history below, which the operator brings
    // back explicitly with Reuse.
    public void Load()
    {
        var hist = _declaration.GetHistory();
        History = hist.IsError ? Array.Empty<ConnectionHistoryEntry>() : hist.Value;
        IsDirty = false;
    }

    private AsyncDelegateCommand? _testCommand;
    public AsyncDelegateCommand TestCommand =>
        _testCommand ??= new AsyncDelegateCommand(ExecuteTestAsync, CanTest)
            .ObservesProperty(() => Host).ObservesProperty(() => Port).ObservesProperty(() => Database)
            .ObservesProperty(() => User).ObservesProperty(() => Password);

    private AsyncDelegateCommand? _saveCommand;
    public AsyncDelegateCommand SaveCommand =>
        _saveCommand ??= new AsyncDelegateCommand(ExecuteSaveAsync, CanSave)
            .ObservesProperty(() => Host).ObservesProperty(() => Port).ObservesProperty(() => Database)
            .ObservesProperty(() => User).ObservesProperty(() => Password)
            .ObservesProperty(() => IsTestPassed);

    private DelegateCommand<ConnectionHistoryEntry>? _reuseCommand;
    public DelegateCommand<ConnectionHistoryEntry> ReuseCommand =>
        _reuseCommand ??= new DelegateCommand<ConnectionHistoryEntry>(Reuse);

    private DelegateCommand? _clearCommand;
    public DelegateCommand ClearCommand =>
        _clearCommand ??= new DelegateCommand(Clear, () => HasAnyInput())
            .ObservesProperty(() => Host).ObservesProperty(() => Port).ObservesProperty(() => Database)
            .ObservesProperty(() => User).ObservesProperty(() => Password);

    private bool HasAnyInput() =>
        !string.IsNullOrEmpty(Host) || !string.IsNullOrEmpty(Database) ||
        !string.IsNullOrEmpty(User) || !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(Port);

    private void Reuse(ConnectionHistoryEntry e)
    {
        // Form User = DbUser (DB account). e.User is the audit actor, not the connection field.
        Host = e.Host; Port = e.Port.ToString(); Database = e.Database; User = e.DbUser; Password = string.Empty;
        // field setters already call InvalidateTestGate(); IsDirty set below.
        IsDirty = true;
    }

    // Also the leave-reset: the view and this VM are reused across navigation, so nothing the operator
    // typed — least of all the password — may outlive the screen.
    public void Clear()
    {
        Host = string.Empty; Port = string.Empty; Database = string.Empty; User = string.Empty; Password = string.Empty;
        StatusMessage = null; Severity = StatusSeverity.None;
        IsDirty = true;
    }

    // Leaving is only worth a confirmation when there is work to lose: the operator touched the form AND
    // something is still in it. Clearing the form leaves it touched but empty, and a saved form is not dirty.
    public bool HasUnsavedInput => IsDirty && HasAnyInput();

    private bool CanTest() =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Database) &&
        !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password) &&
        IsValidPort(Port, out _);

    // The port field is free text; it is a valid port only when it parses to 1..65535.
    private static bool IsValidPort(string? value, out int port) =>
        int.TryParse(value, out port) && port is > 0 and <= 65535;

    private bool CanSave() => CanTest() && IsTestPassed;

    private ConnectionFields BuildFields() =>
        new(Host.Trim(), NormalizePort(Port), Database.Trim(), User.Trim(), Password);

    // Unreachable via the UI (CanSave implies CanTest, which already rejects an unparseable/out-of-range port);
    // the fallback keeps BuildFields total rather than throwing on a caller that skips the gate.
    private static int NormalizePort(string? port) => IsValidPort(port, out var p) ? p : 3306;

    private async Task ExecuteTestAsync()
    {
        var fields = BuildFields();
        var result = await Task.Run(() => _tester.Test(fields));
        if (result.IsError)
        {
            IsTestPassed = false;
            StatusMessage = PlatformErrorDescriber.Describe(result.FirstError);
            Severity = StatusSeverity.Error;
            return;
        }
        IsTestPassed = true;
        StatusMessage = "Kết nối database thành công.";
        Severity = StatusSeverity.Success;
    }

    private async Task ExecuteSaveAsync()
    {
        var fields = BuildFields();
        var test = await Task.Run(() => _tester.Test(fields));
        if (test.IsError)
        {
            IsTestPassed = false;
            StatusMessage = PlatformErrorDescriber.Describe(test.FirstError);
            Severity = StatusSeverity.Error;
            return;
        }
        var result = _declaration.SaveConnection(fields, _session.PrivateKey, _session.Passphrase);
        if (result.IsError) { StatusMessage = PlatformErrorDescriber.Describe(result.FirstError); Severity = StatusSeverity.Error; return; }
        var status = _startupRunner.Rerun();
        StatusMessage = status.Message;
        Severity = status.Mode == StartupMode.Connected ? StatusSeverity.Success : StatusSeverity.Error;
        // Once the saved declaration is proven to connect, the secret is in File A and has no reason to
        // stay on screen. Keep it when the connection failed so the operator can correct and retry.
        if (status.Mode == StartupMode.Connected) Password = string.Empty;
        IsTestPassed = false;
        IsDirty = false;
        var hist = _declaration.GetHistory();
        if (!hist.IsError) History = hist.Value;
    }
}
