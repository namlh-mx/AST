using System.Windows;

namespace AST.AppScope.Tests;

// Sole owner of this process's System.Windows.Application, and it is the REAL AST.App rather than a
// replica: App.xaml's merge list and its ten direct converter entries are facts read off the shipping
// app instead of a list kept in step with it by inspection. The type is nameable directly as
// global::AST.App from this namespace (AST.App.Tests needs an extern alias; this project does not).
//
// ⚠️ THIS HOST NEVER PUMPS A MESSAGE LOOP, and that is a correctness requirement rather than an
// economy. MEASURED 2026-08-20: WPF's Application CONSTRUCTOR queues a callback that raises Startup ->
// PrismApplicationBase.OnStartup -> Initialize(). Application.Run() is NOT involved -- a bare
// Dispatcher.Run() is enough to start Prism, which then throws in DirectoryModuleCatalog (a test
// output has no Modules/ folder), reaches App's DispatcherUnhandledException net, and Shutdown(1)s
// the host. On every run. Without a pump that callback never executes.
//
// NoFileInThisProjectStartsAMessagePump narrows how that constraint can be broken: it matches five
// literal tokens across this project's sources. It does NOT keep pumping absent -- a pump reached
// through reflection, a delegate, a using-alias, or inside a library call this project makes is
// invisible to it, and so is a token spelled differently. The guard's own failure message carries
// the full list of what it cannot see.
//
// Nothing is marshalled and no guard body runs on the owning thread: the STA thread materialises an
// immutable snapshot -- immutable in the sense narrowed on ScopeSnapshot: the INTENT and the
// thread-safety property, not a mechanism -- and then EXITS. Every assertion runs on the calling test thread against records,
// so a guard whose assertion legitimately FAILS cannot reach the host by any path at all.
//
// ⚠️ This REPLACED an earlier containment (AppScope.RunOn plus a named refusal, shipped 899ac07).
// That fix rested on a measurement -- "a throwing guard body kills the host, 5/5 runs" -- which an
// instrumented run showed to be a coincidence of timing: the host was already dying at startup on all
// five. The lesson is worth more than the code: a reproduction is not a cause.
//
// What this host does NOT give you: a rendered visual tree. No window is shown and no template is
// instantiated, so a resource that resolves here can still fail when a real ControlTemplate is applied
// (wpf-dev-pack core policy rule 7). That question belongs to AST.App.Tests/OffscreenHost.cs.
internal static class AppScope
{
    // ExecutionAndPublication CACHES a capture failure, which is what we want: every later test then
    // reports the same named cause instead of starting another doomed thread.
    private static readonly Lazy<ScopeSnapshot> Captured =
        new(Capture, LazyThreadSafetyMode.ExecutionAndPublication);

    // Second net, independent of the first: SetException covers a thread that THREW, and this covers a
    // thread that reached neither call. A hang is worse than a failure because it has no message.
    private static readonly TimeSpan CaptureDeadline = TimeSpan.FromSeconds(60);

    internal static ScopeSnapshot Scope => Captured.Value;

    private static ScopeSnapshot Capture()
    {
        var done = new TaskCompletionSource<ScopeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Background so nothing on this thread can keep the test host process alive.
        var thread = new Thread(() =>
        {
            try
            {
                // Constructed HERE: Application is thread-affine, and this thread is the one that will
                // own it for the life of the process.
                var app = new global::AST.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                done.SetResult(MergedScope.CaptureSnapshot(app));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }

            // The thread now exits. There is no loop to leave and nothing to shut down.
        })
        {
            IsBackground = true,
            Name = "AST.AppScope.Tests App-owning STA thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // WhenAny rather than Task.Wait(timeout): Wait would wrap a failure in an AggregateException,
        // and the original exception IS the message the caller needs.
        if (Task.WhenAny(done.Task, Task.Delay(CaptureDeadline)).GetAwaiter().GetResult() != done.Task)
        {
            throw new TimeoutException(
                $"The real AST.App did not produce a scope snapshot within {CaptureDeadline}. The host "
                + "is hung rather than broken; do not raise this deadline without finding out why.");
        }

        var snapshot = done.Task.GetAwaiter().GetResult();

        // Joined, bounded, and the result is returned either way (125 COVERAGE Q3). The thread has
        // nothing left to do once SetResult ran, so this normally returns at once; a thread still alive
        // here would mean the capture kept a handle to something, which is worth knowing but is not
        // worth failing a run that already has its answer.
        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            Console.Error.WriteLine(
                "AppScope: the App-owning STA thread was still alive 5s after producing the snapshot.");
        }

        return snapshot;
    }
}
