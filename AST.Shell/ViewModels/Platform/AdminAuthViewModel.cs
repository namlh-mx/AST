using System;
using System.ComponentModel;
using AST.Core.Iam;
using AST.Core.Security;
using AST.Core.Presentation;
using AST.Shell.Presentation;
using AST.Shell.Services;
using AST.Shell.Session;
using Prism.Commands;
using Prism.Mvvm;

namespace AST.Shell.ViewModels.Platform;

public sealed class AdminAuthViewModel : BindableBase, IDeclarationForm, IStatusBanner
{
    private readonly IAdminKeyVerifier _verifier;
    private readonly IAdminSession _session;
    private readonly IFilePickerService _picker;
    private readonly ICurrentWindowsUser _currentUser;

    public AdminAuthViewModel(
        IAdminKeyVerifier verifier, IAdminSession session, IFilePickerService picker,
        ICurrentWindowsUser currentUser,
        BreakGlassAdminViewModel breakGlass, ConfigAuditHistoryViewModel history,
        bool allowDebugSkip)
    {
        _verifier = verifier;
        _session = session;
        _picker = picker;
        _currentUser = currentUser;
        BreakGlass = breakGlass;
        History = history;
        CanSkipDebug = allowDebugSkip;

        // Aggregate this VM's own auth-result status + the two child VMs' status into the single
        // screen-level banner (IStatusBanner). Self writes StatusMessage/Severity directly; the two
        // children funnel through their PropertyChanged handlers below. Last-writer-wins.
        BreakGlass.PropertyChanged += OnBreakGlassStatusChanged;
        History.PropertyChanged += OnHistoryStatusChanged;

        // A Sign & Save appends an audit record -> refresh the history view.
        BreakGlass.Saved += (_, _) => History.Refresh();
    }

    public BreakGlassAdminViewModel BreakGlass { get; }
    public ConfigAuditHistoryViewModel History { get; }

    // IStatusBanner: the screen-level aggregate the AstStatusBand binds. Fed by three sources
    // (this VM's auth result, BreakGlass status, History integrity), last-writer-wins.
    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private StatusSeverity _severity = StatusSeverity.None;
    public StatusSeverity Severity { get => _severity; private set => SetProperty(ref _severity, value); }

    private void OnBreakGlassStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BreakGlassAdminViewModel.StatusMessage) or nameof(BreakGlassAdminViewModel.Severity))
        { StatusMessage = BreakGlass.StatusMessage; Severity = BreakGlass.Severity; }
    }

    private void OnHistoryStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConfigAuditHistoryViewModel.IntegrityMessage) or nameof(ConfigAuditHistoryViewModel.Severity))
        { StatusMessage = History.IntegrityMessage; Severity = History.Severity; }
    }

    private PickedFile? _picked;

    private string? _keyFilePath;
    public string? KeyFilePath { get => _keyFilePath; private set => SetProperty(ref _keyFilePath, value); }

    // Bound TwoWay to the passphrase field; a plain string keeps the VM BCL-only. It MUST raise change
    // notification: without it the VM can never clear the field on screen (the box would keep a typed
    // passphrase, and a clear-form/leave would silently do nothing to it).
    private string _passphrase = string.Empty;
    public string Passphrase { get => _passphrase; set => SetProperty(ref _passphrase, value); }

    // IDeclarationForm: leaving is worth a confirmation while the key form still holds something, or the
    // break-glass list has a typed/unsaved change.
    public bool HasUnsavedInput =>
        KeyFilePath is not null || !string.IsNullOrEmpty(Passphrase) || BreakGlass.HasUnsavedInput;

    // IDeclarationForm: reset every field the operator typed into on this screen. The authenticated session
    // keeps its own copy of the key + passphrase for signing, so this does NOT sign them out.
    public void Clear()
    {
        ExecuteClearForm();
        BreakGlass.ClearForm();
    }

    private bool _isAuthenticated;
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            if (SetProperty(ref _isAuthenticated, value))
            {
                RaisePropertyChanged(nameof(AuthenticatedKeyInfo));
                ClearFormCommand.RaiseCanExecuteChanged();
                EndSessionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanSkipDebug { get; }

    private string? _operator;
    public string? Operator { get => _operator; private set => SetProperty(ref _operator, value); }

    private DateTimeOffset? _authenticatedAt;
    public DateTimeOffset? AuthenticatedAt { get => _authenticatedAt; private set => SetProperty(ref _authenticatedAt, value); }

    public string? AuthenticatedKeyInfo =>
        IsAuthenticated ? $"{Operator ?? "—"} | {AuthenticatedAt:yyyy-MM-dd HH:mm}" : null;

    private DelegateCommand? _browseKeyCommand;
    public DelegateCommand BrowseKeyCommand => _browseKeyCommand ??= new DelegateCommand(ExecuteBrowseKey);

    private DelegateCommand? _authenticateCommand;
    public DelegateCommand AuthenticateCommand =>
        _authenticateCommand ??= new DelegateCommand(ExecuteAuthenticate, () => _picked is not null)
            .ObservesProperty(() => KeyFilePath);

    private DelegateCommand? _skipDebugCommand;
    public DelegateCommand SkipDebugCommand =>
        _skipDebugCommand ??= new DelegateCommand(ExecuteSkipDebug, () => CanSkipDebug);

    private DelegateCommand? _clearFormCommand;
    public DelegateCommand ClearFormCommand =>
        _clearFormCommand ??= new DelegateCommand(ExecuteClearForm, () => !IsAuthenticated);

    private DelegateCommand? _endSessionCommand;
    public DelegateCommand EndSessionCommand =>
        _endSessionCommand ??= new DelegateCommand(ExecuteEndSession, () => IsAuthenticated);

    private void ExecuteBrowseKey()
    {
        var picked = _picker.PickPrivateKey();
        if (picked is null) return;
        _picked = picked;
        KeyFilePath = picked.Path;
    }

    private void ExecuteAuthenticate()
    {
        if (_picked is null) return;
        var result = _verifier.Verify(_picked.Content, Passphrase);
        if (result.IsError) { StatusMessage = PlatformErrorDescriber.Describe(result.FirstError); Severity = StatusSeverity.Error; return; }
        _session.Authenticate(_picked.Content, Passphrase);
        Operator = WindowsUsernameNormalizer.Normalize(_currentUser.Username);
        AuthenticatedAt = DateTimeOffset.Now;
        IsAuthenticated = true;
        StatusMessage = null;
        Severity = StatusSeverity.None;
        // The session now holds the key and passphrase; leaving them in the form only keeps a secret and a
        // private-key path on screen for no purpose.
        ExecuteClearForm();
    }

    private void ExecuteSkipDebug()
    {
        _session.Authenticate(null, null);
        Operator = WindowsUsernameNormalizer.Normalize(_currentUser.Username);
        AuthenticatedAt = DateTimeOffset.Now;
        IsAuthenticated = true;
        StatusMessage = null;
        Severity = StatusSeverity.None;
    }

    private void ExecuteClearForm()
    {
        _picked = null;
        KeyFilePath = null;
        Passphrase = string.Empty;
        AuthenticateCommand.RaiseCanExecuteChanged();
    }

    private void ExecuteEndSession()
    {
        _session.Clear();
        IsAuthenticated = false;
        Operator = null;
        AuthenticatedAt = null;
        StatusMessage = null;
        Severity = StatusSeverity.None;
    }

    // Called by the View's Loaded handler (Prism.Core VM cannot implement INavigationAware). If the screen
    // is re-entered while already authenticated, IAdminSession.Changed will not fire, so load both here.
    // (While unauthenticated nothing loads — the grids stay empty but keep their fixed frame.)
    public void OnViewLoaded()
    {
        if (!_session.IsAuthenticated) return;
        BreakGlass.LoadCommand.Execute();
        History.Refresh();
    }
}
