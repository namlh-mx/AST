using System.Windows.Controls;
using AST.Shell.ViewModels;
using Prism.Navigation.Regions;

namespace AST.Views;

public partial class ComingSoonView : UserControl, INavigationAware
{
    public ComingSoonView()
    {
        InitializeComponent();
    }

    // Forward the Prism navigation parameters (the leaf title) to the headless VM, which lives in AST.Shell
    // (Prism.Core only) and therefore cannot implement INavigationAware itself.
    public void OnNavigatedTo(NavigationContext navigationContext)
    {
        if (DataContext is ComingSoonViewModel vm)
        {
            vm.ApplyParameters(navigationContext.Parameters);
        }
    }

    // Reuse one ComingSoonView/ComingSoonViewModel in the region across all placeholder leaves.
    // Title still updates on every navigation: OnNavigatedTo re-applies ApplyParameters regardless of reuse.
    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public void OnNavigatedFrom(NavigationContext navigationContext) { }
}
