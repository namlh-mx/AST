namespace AST.Core.Startup;

// Holds the startup-chain result for the UI slice to read (spec §2.1). Singleton, registered at the composition root.
[SharedComponent]
public interface IStartupState
{
    StartupStatus Status { get; }
    void Set(StartupStatus status);
    // Raised after Set stores a new status, so a bound UI (banner) can refresh (spec §⑤#3 §3.2).
    event EventHandler? Changed;
}

public sealed class StartupState : IStartupState
{
    public StartupStatus Status { get; private set; } =
        new(StartupMode.NotConnected, StartupCodes.Pending, "Đang khởi động.");

    public event EventHandler? Changed;

    public void Set(StartupStatus status)
    {
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
