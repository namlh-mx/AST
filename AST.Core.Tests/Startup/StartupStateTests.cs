using AST.Core.Startup;

namespace AST.Core.Tests.Startup;

public class StartupStateTests
{
    [Fact]
    public void Set_updates_Status_and_raises_Changed()
    {
        var state = new StartupState();
        var raised = 0;
        state.Changed += (_, _) => raised++;

        var status = new StartupStatus(StartupMode.Connected, "Startup.Ok", "Đã kết nối.");
        state.Set(status);

        Assert.Same(status, state.Status);
        Assert.Equal(1, raised);
    }
}
