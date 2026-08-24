using Serilog;

namespace AST.Core.Iam;

// In-memory, idempotent by FunctionKey (a module may re-register on Prism re-init — §11).
// See IFunctionRegistry.Register for the pure-in-memory invariant this class must uphold.
public sealed class FunctionRegistry : IFunctionRegistry
{
    private readonly Dictionary<string, FunctionDescriptor> _byKey = [];
    private bool _sealed;

    public void Register(FunctionDescriptor descriptor)
    {
        _byKey[descriptor.FunctionKey] = descriptor;

        if (_sealed)
        {
            // Edge/bootstrap diagnostic (rule-platform-infra §1): the sidebar has already been read/built by
            // the time Seal() is called, so a registration reaching this branch will NOT appear on the sidebar
            // this run. This is a cosmetic gap, not a crash -- log clearly and keep going, never throw.
            Log.Error(
                "Function '{FunctionKey}' registered after the sidebar was built; it will NOT appear on the " +
                "sidebar this session.",
                descriptor.FunctionKey);
        }
    }

    // Marks "no more registrations are expected" so a late Register() (a startup-ordering bug, or a module
    // that registers on some deferred/async path) gets a loud diagnostic instead of a silently-missing menu
    // item. Called once by the composition root (AST/App.xaml.cs OnInitialized) after all modules have loaded
    // AND the app's own Register calls are done -- see that call site for the ordering evidence. Not part of
    // IFunctionRegistry (rule-module-boundary/shared-component gate): callers that only need the public
    // contract are unaffected; only the composition root needs the concrete type to seal it.
    public void Seal() => _sealed = true;

    public IReadOnlyList<FunctionDescriptor> All => _byKey.Values.ToList();

    public FunctionDescriptor? Find(string functionKey) =>
        _byKey.TryGetValue(functionKey, out var descriptor) ? descriptor : null;
}
