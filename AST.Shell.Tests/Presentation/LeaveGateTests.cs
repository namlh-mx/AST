using AST.Shell.Presentation;

namespace AST.Shell.Tests.Presentation;

public class LeaveGateTests
{
    [Fact]
    public async Task Clean_form_proceeds_without_asking()
    {
        var asks = 0;
        var gate = new LeaveGate(() => false, () => { asks++; return Task.FromResult(true); });

        Assert.True(await gate.ConfirmAsync());
        Assert.Equal(0, asks);
    }

    [Fact]
    public async Task Dirty_form_asks_once_and_returns_the_answer()
    {
        var asks = 0;
        var gate = new LeaveGate(() => true, () => { asks++; return Task.FromResult(false); });

        Assert.False(await gate.ConfirmAsync());
        Assert.Equal(1, asks);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_question_and_one_answer()
    {
        var asks = 0;
        var answer = new TaskCompletionSource<bool>();
        var gate = new LeaveGate(() => true, () => { asks++; return answer.Task; });

        var first = gate.ConfirmAsync();
        var second = gate.ConfirmAsync();   // arrives while the dialog is open
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        answer.SetResult(true);

        Assert.True(await first);
        Assert.True(await second);          // the REAL answer, not a synthetic "stay"
        Assert.Equal(1, asks);
    }

    [Fact]
    public async Task A_later_gesture_asks_again()
    {
        var asks = 0;
        var gate = new LeaveGate(() => true, () => { asks++; return Task.FromResult(true); });

        await gate.ConfirmAsync();
        await gate.ConfirmAsync();

        Assert.Equal(2, asks);
    }

    [Fact]
    public async Task A_failing_dialog_surfaces_to_the_caller_and_does_not_wedge_the_gate()
    {
        // The gate deliberately does NOT translate a failure into a synthetic "stay": it cannot log, so
        // the caller must see the exception, log it, and abandon its action (which leaves the operator on
        // the screen anyway). What the gate DOES guarantee is that the failed question is released.
        var asks = 0;
        var gate = new LeaveGate(() => true, () =>
        {
            asks++;
            return asks == 1 ? Task.FromException<bool>(new InvalidOperationException("dialog")) : Task.FromResult(true);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ConfirmAsync());
        Assert.True(await gate.ConfirmAsync());    // in-flight task was released
        Assert.Equal(2, asks);
    }
}
