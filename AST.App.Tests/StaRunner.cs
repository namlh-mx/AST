using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace AST.App.Tests;

// Runs a test body on a dedicated STA thread. Constructing a WPF FrameworkElement (StackPanel, Border, …)
// requires STA — its ctor spins up InputManager. xUnit's default worker thread is MTA, so any test that
// instantiates a visual element wraps its body in Sta.Run and the original assertion failure is rethrown
// with its stack intact.
internal static class Sta
{
    // Single home for the shared-thread name. OffscreenHost.EnsureApplication and the FR-4 pin read this
    // rather than duplicating the literal.
    internal const string SharedStaThreadName = "AST.App.Tests shared STA UI thread";

    internal static Dispatcher SharedStaUiDispatcher => SharedStaDispatcher.Value;

    public static void Run(Action body)
    {
        ExceptionDispatchInfo? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                captured = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        captured?.Throw();
    }

    /// <summary>
    /// Runs <paramref name="body"/> on the ONE long-lived STA thread shared by every test that needs a
    /// process-wide WPF singleton (currently: <see cref="OffscreenHost"/>'s <see cref="System.Windows.Application"/>).
    /// </summary>
    /// <remarks>
    /// This is the same STA mechanism as <see cref="Run"/>, only with the thread outliving the call. It has to
    /// be shared, not per-test: <c>System.Windows.Application</c> is a per-process singleton with thread
    /// affinity (a second <c>new Application()</c> throws, and its dispatcher dies with its thread), so every
    /// test needing it must land on the same thread. Bodies are serialised by the dispatcher queue, so two
    /// parallel xUnit collections can never share a window.
    /// </remarks>
    public static void RunOnSharedStaThread(Action body)
    {
        ExceptionDispatchInfo? captured = null;
        SharedStaDispatcher.Value.Invoke(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                captured = ExceptionDispatchInfo.Capture(ex);
            }
        });
        captured?.Throw();
    }

    private static readonly Lazy<Dispatcher> SharedStaDispatcher = new(StartSharedStaThread, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Dispatcher StartSharedStaThread()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Background so a still-running message loop can never keep the test host process alive.
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => ready.SetResult(dispatcher)));
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = SharedStaThreadName,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    // Classic WPF "DoEvents": queues a callback at ApplicationIdle and pumps the dispatcher until it runs,
    // draining everything queued at or above that priority first (FIFO within a priority). Needed on BOTH
    // runners: an Sta.Run thread never calls Dispatcher.Run() at all, and a RunOnSharedStaThread body executes
    // INSIDE a dispatcher callback, which blocks that thread's loop until it returns. Either way the queued
    // BeginInvoke callbacks (and the layout/template work a shown window queues) need this nested frame.
    public static void PumpToIdle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
