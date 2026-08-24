namespace AST.Core.Iam;

[SharedComponent]
public interface IFunctionRegistry
{
    // MUST stay a pure in-memory operation (no DB/network calls) -- the sidebar (specifically the
    // "Cấu hình" configuration-station entry, the rescuer's only path to fix a broken DB connection) must be
    // fully buildable even when the database is unreachable.
    void Register(FunctionDescriptor descriptor);   // each module registers itself once per function
    IReadOnlyList<FunctionDescriptor> All { get; }
    FunctionDescriptor? Find(string functionKey);
}
