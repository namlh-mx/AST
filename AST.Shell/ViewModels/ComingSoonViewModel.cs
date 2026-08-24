using Prism.Mvvm;
using Prism.Navigation;

namespace AST.Shell.ViewModels;

// Placeholder screen shown when a not-yet-implemented menu leaf is navigated to. It displays the leaf's title
// (passed as the navigation parameter "title"). BCL + Prism.Core only so it is headless-testable; the View
// code-behind implements INavigationAware (Prism.Wpf) and forwards the parameters to ApplyParameters.
public sealed class ComingSoonViewModel : BindableBase
{
    // Key under which the leaf title is passed in the Prism navigation parameters.
    public const string TitleParameterKey = "title";

    private string _title = string.Empty;
    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    public void ApplyParameters(INavigationParameters parameters)
    {
        Title = parameters.TryGetValue(TitleParameterKey, out string? title) && !string.IsNullOrWhiteSpace(title)
            ? title
            : string.Empty;
    }
}
