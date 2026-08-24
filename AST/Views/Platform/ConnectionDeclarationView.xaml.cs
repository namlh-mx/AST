using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AST.Controls;
using AST.Core.Presentation;
using AST.Shell.ViewModels.Platform;
using Prism.Commands;
using Prism.Navigation.Regions;
using Serilog;
using Wpf.Ui;

namespace AST.Views.Platform;

// Leave-confirmation + form wipe on navigate-away come from DeclarationFormView (the VM implements
// IDeclarationForm) — do not re-implement them here.
public partial class ConnectionDeclarationView : DeclarationFormView
{
    private const string PlaceholderMessage = "Chức năng chờ xây dựng hoặc tạm đóng.";
    private readonly IRegionManager _regionManager;

    // Back affordance for the drill-in screen. Navigation stays in the view (Prism authority, §1c); set before
    // InitializeComponent so the AstScreen RelativeSource binding reads it (AstScreen forwards it to the header,
    // which never navigates itself). Still gated by the base's ConfirmNavigationRequest leave-confirm.
    public ICommand BackCommand { get; }

    public ConnectionDeclarationView(IRegionManager regionManager, IContentDialogService dialogService)
        : base(dialogService)
    {
        _regionManager = regionManager;
        BackCommand = new DelegateCommand(() =>
            _regionManager.RequestNavigate(MainWindowViewModel.ContentRegionName, "ConfigurationStationView"));
        InitializeComponent();
    }

    // View-lifecycle only (not business logic): load the history once the Prism-wired DataContext is
    // available. Silent when the log is absent/corrupt (handled inside the VM).
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionDeclarationViewModel vm)
        {
            vm.Load();
        }
    }

    // Shell-only deferred feature (disconnect card): a notice dialog. (The save-path folder is now a
    // decorative icon, no longer wired here.) Do not write into StatusMessage — that band stays
    // command-result only. No DataContext access.
    private async void OnPlaceholderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await AstDialog.NoticeAsync(Dialogs, PlaceholderMessage, StatusSeverity.Info);
        }
        catch (Exception ex)
        {
            // Outer-ring callback: a dialog failure must fail clearly in the log, not take the app down.
            Log.Error(ex, "Failed to show the notice dialog: {Notice}", PlaceholderMessage);
        }
    }
}
