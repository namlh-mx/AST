using AST.Core.Presentation;
using AST.Shell.Presentation;
using AST.Shell.Presentation.Iam;

namespace AST.Shell.Tests.Presentation.Iam;

public class TreeSelectionGateTests
{
    private static OrgUnitTreeNode Node(long id) => new(id, $"U{id}");

    [Fact]
    public async Task Clicking_the_node_already_on_the_card_does_nothing_and_never_asks()
    {
        var asks = 0; var commits = new List<long>();
        var gate = new TreeSelectionGate(() => 7L, () => { asks++; return Task.FromResult(true); },
                                         n => { commits.Add(n.Id); return Task.CompletedTask; });

        Assert.False(await gate.RequestAsync(Node(7)));
        Assert.Equal(0, asks);
        Assert.Empty(commits);
    }

    [Fact]
    public async Task Clean_form_commits_without_asking()
    {
        var asks = 0; var commits = new List<long>();
        var gate = new TreeSelectionGate(() => 7L, () => { asks++; return Task.FromResult(true); },
                                         n => { commits.Add(n.Id); return Task.CompletedTask; });

        Assert.True(await gate.RequestAsync(Node(9)));
        Assert.Equal(new[] { 9L }, commits);
    }

    [Fact]
    public async Task Staying_commits_nothing()
    {
        var commits = new List<long>();
        var gate = new TreeSelectionGate(() => 7L, () => Task.FromResult(false),
                                         n => { commits.Add(n.Id); return Task.CompletedTask; });

        Assert.False(await gate.RequestAsync(Node(9)));
        Assert.Empty(commits);
    }

    [Fact]
    public async Task Two_requests_while_the_dialog_is_open_produce_one_question()
    {
        // Production callers are the WPF dispatcher (single-threaded). Unit tests have no
        // SynchronizationContext by default, so force a serial one so await-continuations of the
        // shared LeaveGate answer match that model (and the ordering assertion is not a flake).
        var prior = SynchronizationContext.Current;
        var serial = new SerialSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(serial);
        try
        {
            var asks = 0; var commits = new List<long>();
            var answer = new TaskCompletionSource<bool>();
            var leave = new LeaveGate(() => true, () => { asks++; return answer.Task; });
            var gate = new TreeSelectionGate(() => 7L, leave.ConfirmAsync,
                                             n => { commits.Add(n.Id); return Task.CompletedTask; });

            var first = gate.RequestAsync(Node(9));
            var second = gate.RequestAsync(Node(11));
            answer.SetResult(true);
            serial.RunUntilIdle();

            Assert.True(await first);
            Assert.True(await second);
            Assert.Equal(1, asks);                        // ONE dialog for the burst
            Assert.Equal(new[] { 9L, 11L }, commits);     // both proceed, in order
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }

    private sealed class SerialSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunUntilIdle()
        {
            while (_queue.Count > 0)
            {
                var (callback, state) = _queue.Dequeue();
                callback(state);
            }
        }
    }

    [Fact]
    public async Task No_selection_on_the_card_yet_still_commits()
    {
        var commits = new List<long>();
        var gate = new TreeSelectionGate(() => null, () => Task.FromResult(true),
                                         n => { commits.Add(n.Id); return Task.CompletedTask; });

        Assert.True(await gate.RequestAsync(Node(3)));
        Assert.Equal(new[] { 3L }, commits);
    }

    [Fact]
    public async Task A_card_loaded_from_history_is_not_the_trees_selection_so_the_same_unit_still_commits()
    {
        // History-Xem clears the tree selection while the card keeps showing that unit at a PAST date.
        // Clicking it in the tree must reload it at the tree as-of, not be swallowed as "already there".
        var commits = new List<long>();
        var gate = new TreeSelectionGate(() => null, () => Task.FromResult(true),
                                         n => { commits.Add(n.Id); return Task.CompletedTask; });

        Assert.True(await gate.RequestAsync(Node(7)));
        Assert.Equal(new[] { 7L }, commits);
    }

    [Fact]
    public async Task A_faulting_confirmLeave_propagates_and_commits_nothing()
    {
        var commits = new List<long>();
        var gate = new TreeSelectionGate(
            () => 7L,
            () => Task.FromException<bool>(new InvalidOperationException("dialog")),
            n => { commits.Add(n.Id); return Task.CompletedTask; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RequestAsync(Node(9)));
        Assert.Empty(commits);
    }

    [Fact]
    public async Task A_faulting_commit_propagates_and_does_not_report_success()
    {
        var gate = new TreeSelectionGate(
            () => 7L,
            () => Task.FromResult(true),
            _ => Task.FromException(new InvalidOperationException("commit")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RequestAsync(Node(9)));
        Assert.Equal("commit", ex.Message);
    }
}
