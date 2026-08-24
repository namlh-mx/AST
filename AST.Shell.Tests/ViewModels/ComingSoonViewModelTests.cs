using AST.Shell.ViewModels;
using Prism.Navigation;

namespace AST.Shell.Tests.ViewModels;

public class ComingSoonViewModelTests
{
    [Fact]
    public void ApplyParameters_SetsTitle_FromTitleParameter()
    {
        var vm = new ComingSoonViewModel();

        vm.ApplyParameters(new NavigationParameters { { ComingSoonViewModel.TitleParameterKey, "Menu 1.1" } });

        Assert.Equal("Menu 1.1", vm.Title);
    }

    [Fact]
    public void ApplyParameters_LeavesTitleEmpty_WhenParameterMissing()
    {
        var vm = new ComingSoonViewModel();

        vm.ApplyParameters(new NavigationParameters());

        Assert.Equal(string.Empty, vm.Title);
    }

    [Fact]
    public void ApplyParameters_LeavesTitleEmpty_WhenParameterBlank()
    {
        var vm = new ComingSoonViewModel();

        vm.ApplyParameters(new NavigationParameters { { ComingSoonViewModel.TitleParameterKey, "   " } });

        Assert.Equal(string.Empty, vm.Title);
    }

    // Regression guard for instance reuse (IsNavigationTarget => true): repeated navigations must fully overwrite Title.
    [Fact]
    public void ApplyParameters_OverwritesTitle_OnRepeatedCalls()
    {
        var vm = new ComingSoonViewModel();

        vm.ApplyParameters(new NavigationParameters { { ComingSoonViewModel.TitleParameterKey, "Menu 1.1" } });
        Assert.Equal("Menu 1.1", vm.Title);

        vm.ApplyParameters(new NavigationParameters { { ComingSoonViewModel.TitleParameterKey, "Menu 2.1" } });
        Assert.Equal("Menu 2.1", vm.Title);

        vm.ApplyParameters(new NavigationParameters());
        Assert.Equal(string.Empty, vm.Title);
    }
}
