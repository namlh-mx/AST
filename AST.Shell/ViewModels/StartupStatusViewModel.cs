using AST.Core.Startup;
using Prism.Mvvm;

namespace AST.Shell.ViewModels;

public sealed class StartupStatusViewModel : BindableBase
{
    private readonly IStartupState _state;

    public StartupStatusViewModel(IStartupState state)
    {
        _state = state;
        _state.Changed += (_, _) => Refresh();
    }

    public bool IsConnected => _state.Status.Mode == StartupMode.Connected;
    public StartupMode Mode => _state.Status.Mode;
    public string Message => _state.Status.Message;

    private void Refresh()
    {
        RaisePropertyChanged(nameof(IsConnected));
        RaisePropertyChanged(nameof(Mode));
        RaisePropertyChanged(nameof(Message));
    }
}
