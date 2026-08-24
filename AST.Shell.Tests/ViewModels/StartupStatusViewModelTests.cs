using AST.Core.Startup;
using AST.Shell.ViewModels;

namespace AST.Shell.Tests.ViewModels;

public class StartupStatusViewModelTests
{
    private sealed class FakeStartupState : IStartupState
    {
        public StartupStatus Status { get; private set; } =
            new(StartupMode.NotConnected, "Startup.Pending", "Đang khởi động.");
        public event EventHandler? Changed;
        public void Set(StartupStatus status) { Status = status; Changed?.Invoke(this, EventArgs.Empty); }
    }

    [Fact]
    public void Exposes_initial_state_from_IStartupState()
    {
        var state = new FakeStartupState();
        var vm = new StartupStatusViewModel(state);

        Assert.False(vm.IsConnected);
        Assert.Equal("Đang khởi động.", vm.Message);
        Assert.Equal(StartupMode.NotConnected, vm.Mode);
    }

    [Fact]
    public void Refreshes_and_raises_PropertyChanged_when_state_Changes()
    {
        var state = new FakeStartupState();
        var vm = new StartupStatusViewModel(state);
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        state.Set(new StartupStatus(StartupMode.Connected, "Startup.Ok", "Đã kết nối."));

        Assert.True(vm.IsConnected);
        Assert.Equal("Đã kết nối.", vm.Message);
        Assert.Contains(nameof(StartupStatusViewModel.IsConnected), changed);
        Assert.Contains(nameof(StartupStatusViewModel.Message), changed);
        Assert.Contains(nameof(StartupStatusViewModel.Mode), changed);
    }
}
