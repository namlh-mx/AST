using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AST.Shell.ViewModels.Platform;
using Prism.Commands;
using Prism.Navigation.Regions;
using Wpf.Ui;

namespace AST.Views.Platform;

// Leave-confirmation + form wipe on navigate-away come from DeclarationFormView (the VM implements
// IDeclarationForm) — do not re-implement them here.
public partial class AdminAuthView : DeclarationFormView
{
    private readonly IRegionManager _regionManager;

    // Back affordance for the drill-in AstScreenHeader. Navigation stays in the view (Prism authority, §1c);
    // set before InitializeComponent so the header's RelativeSource binding reads it. The RequestNavigate is
    // still gated by the base's ConfirmNavigationRequest leave-confirm.
    public ICommand BackCommand { get; }

    public AdminAuthView(IRegionManager regionManager, IContentDialogService dialogService)
        : base(dialogService)
    {
        _regionManager = regionManager;
        BackCommand = new DelegateCommand(() =>
            _regionManager.RequestNavigate(MainWindowViewModel.ContentRegionName, "ConfigurationStationView"));
        InitializeComponent();
        HookAutoScroll(AdminsGrid);
        HookAutoScroll(HistoryGrid);
    }

    // View-lifecycle only: load File B health/meta once the Prism-wired DataContext is available.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AdminAuthViewModel vm) vm.OnViewLoaded();
    }

    // View-only: keep a DataGrid scrolled to its last row whenever its items change (newest stays visible).
    // Hooked once from the constructor, so the handler lives exactly as long as this view instance.
    private static void HookAutoScroll(DataGrid grid)
    {
        if (grid.Items is not INotifyCollectionChanged incc) return;
        incc.CollectionChanged += (_, _) =>
        {
            if (grid.Items.Count > 0) grid.ScrollIntoView(grid.Items[^1]!);
        };
    }
}
