using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using AST.Shell.ViewModels.Iam.Role;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Serilog;
using Wpf.Ui;

namespace AST.Views.Iam.Role;

public partial class RoleDeclarationView : DeclarationFormView
{
    public LocalChrome Chrome { get; } = new();

    public RoleDeclarationView(IContentDialogService dialogService)
        : base(dialogService)
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is RoleDeclarationViewModel vm)
            {
                vm.PropertyChanged -= OnViewModelPropertyChanged;
                vm.PropertyChanged += OnViewModelPropertyChanged;
                Chrome.FormInEditing = vm.Mode is RoleCardMode.Editing or RoleCardMode.Adding;
                RebuildHistoryRowsView(vm);
                await vm.LoadAllHistoryAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load Screen B (Role)");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is RoleDeclarationViewModel vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not RoleDeclarationViewModel vm)
            return;

        if (e.PropertyName is nameof(RoleDeclarationViewModel.Mode))
        {
            Chrome.FormInEditing = vm.Mode is RoleCardMode.Editing or RoleCardMode.Adding;
            if (vm.Mode == RoleCardMode.Adding)
                Chrome.ClearHistoryViewConsumed();
        }

        if (e.PropertyName is nameof(RoleDeclarationViewModel.HistoryRows))
            RebuildHistoryRowsView(vm);
        else if (e.PropertyName is nameof(RoleDeclarationViewModel.HistoryFilterText))
            Chrome.HistoryRowsView?.Refresh();
    }

    private void RebuildHistoryRowsView(RoleDeclarationViewModel vm)
    {
        var view = CollectionViewSource.GetDefaultView(vm.HistoryRows);
        // F7: single predicate home on the VM — View delegates so Shell.Tests can discriminate.
        view.Filter = item =>
            RoleDeclarationViewModel.MatchesHistoryFilter(item as RoleHistoryRow, vm.HistoryFilterText);
        Chrome.HistoryRowsView = view;
    }

    protected override void OnLeaving(NavigationContext navigationContext)
    {
        Chrome.ClearHistoryViewConsumed();
        Chrome.SelectedHistoryRow = null;
        if (DataContext is RoleDeclarationViewModel vm)
        {
            vm.HistoryFilterText = string.Empty;
            vm.ClearHistory();
        }
    }

    private void OnHistoryShowAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is RoleDeclarationViewModel vm)
                vm.HistoryFilterText = string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear Role history filter");
        }
    }

    private async void OnHistoryRefreshClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is RoleDeclarationViewModel vm)
                await vm.RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh Role history");
        }
    }

    private async void OnHistoryViewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Chrome.SelectedHistoryRow is not RoleHistoryRow row || !Chrome.CanViewHistory)
                return;

            if (!await ConfirmLeaveAsync())
                return;

            if (DataContext is not RoleDeclarationViewModel vm)
                return;

            if (await vm.LoadFromHistoryRowAsync(row) == CardLoadOutcome.Loaded)
                Chrome.MarkHistoryViewConsumed(row);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to view Role history row");
        }
    }

    public sealed class LocalChrome : BindableBase
    {
        private ICollectionView? _historyRowsView;
        public ICollectionView? HistoryRowsView
        {
            get => _historyRowsView;
            set => SetProperty(ref _historyRowsView, value);
        }

        private RoleHistoryRow? _selectedHistoryRow;
        public RoleHistoryRow? SelectedHistoryRow
        {
            get => _selectedHistoryRow;
            set
            {
                if (SetProperty(ref _selectedHistoryRow, value))
                {
                    _viewedHistoryRow = null;
                    RaisePropertyChanged(nameof(CanViewHistory));
                }
            }
        }

        private RoleHistoryRow? _viewedHistoryRow;

        private bool _formInEditing;
        public bool FormInEditing
        {
            get => _formInEditing;
            set
            {
                if (SetProperty(ref _formInEditing, value))
                    RaisePropertyChanged(nameof(CanViewHistory));
            }
        }

        public bool CanViewHistory =>
            SelectedHistoryRow is not null
            && (SelectedHistoryRow != _viewedHistoryRow || FormInEditing);

        public void MarkHistoryViewConsumed(RoleHistoryRow row)
        {
            _viewedHistoryRow = row;
            RaisePropertyChanged(nameof(CanViewHistory));
        }

        public void ClearHistoryViewConsumed()
        {
            if (_viewedHistoryRow is null)
                return;
            _viewedHistoryRow = null;
            RaisePropertyChanged(nameof(CanViewHistory));
        }
    }
}
