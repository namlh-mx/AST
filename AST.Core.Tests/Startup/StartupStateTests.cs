using AST.Core.Startup;
using FluentAssertions;

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

    // The DEFAULT initializer's code was substituted to StartupCodes.Pending, and no test asserted
    // it -- Set() was the only path exercised. Asserts the LITERAL, not the constant: comparing the
    // symbol against itself would pass even if the value drifted.
    [Fact]
    public void Initial_Status_carries_the_pending_code_and_NotConnected_mode()
    {
        var state = new StartupState();

        state.Status.Reason.Should().Be("Startup.Pending");
        state.Status.Mode.Should().Be(StartupMode.NotConnected);
    }
}
