using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Security;
using AST.Core.Startup;
using AST.Infrastructure.Security;
using Serilog;

namespace AST.Startup;

// Composition-root impl of IStartupRunner (AST.Core.Startup) -- reuses StartupOrchestrator (rule-prefer-existing,
// spec §9) instead of duplicating the resolve logic. Lives here (not AST.Core) because it needs the RESOLVED
// ISchemaVersionChecker IMPLEMENTATION, which DryIoc can only resolve after Prism has loaded modules
// (IamModule.RegisterTypes registers the concrete SchemaVersionChecker) -- ISchemaVersionChecker itself is a
// plain AST.Core.Startup contract, not a compile-time boundary (rule-platform-infra §2).
public sealed class StartupRunner(
    IConnectionConfigStore configStore,
    IConnectionTester connectionTester,
    ISchemaVersionChecker schemaChecker,
    IStartupState state,
    IConfigAuditLog auditLog,
    IFunctionCatalogSync catalogSync,
    int expectedSchemaVersion) : IStartupRunner
{
    public StartupStatus Rerun()
    {
        var status = Resolve();
        state.Set(status);
        Log.Information("Chế độ khởi động: {Mode} — {Reason}: {Message}", status.Mode, status.Reason, status.Message);

        // Every successful (re)connect (first launch OR the operator just declared/fixed File A) syncs the
        // function catalog -- SyncAsync only ADDS+UPDATES, so re-running it on repeat Reruns is safe.
        // That is a claim about SEQUENTIAL re-runs only. Concurrency is a separate matter and is NOT
        // carried by this line: every workstation reaches here on its own connect, so a deployment adding
        // a function key has many machines creating it at once. What makes that safe is the catalog-create
        // lock inside FunctionRepository.CreateAsync, not the idempotence noted here (2026-08-17 -- until
        // then this comment was the only thing standing where that lock belonged).
        // Fire-and-forget with a fail-clear log, not a blocking call: a sync failure must never stop
        // the app from starting (rule-platform-infra #1) -- the menu/permission gate simply stays closed
        // until the next successful sync.
        if (status.Mode == StartupMode.Connected)
        {
            _ = SyncFunctionCatalogSafeAsync();
        }

        return status;
    }

    private async Task SyncFunctionCatalogSafeAsync()
    {
        try
        {
            var result = await catalogSync.SyncAsync();
            if (result.IsError)
            {
                Log.Error("Đồng bộ danh mục chức năng thất bại: {Errors}",
                    string.Join("; ", result.Errors.Select(e => e.Description)));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Lỗi không mong đợi khi đồng bộ danh mục chức năng.");
        }
    }

    private StartupStatus Resolve()
    {
        var guard = ConfigSecurity.EnsureKeyConfigured(RequireConfigSignature.Value, RootPublicKey.IsPlaceholder);
        if (guard.IsError)
        {
            Log.Error("Khởi động bị chặn: {Message}", guard.FirstError.Description);
            return new StartupStatus(StartupMode.NotConnected, guard.FirstError.Code, guard.FirstError.Description);
        }

        // F3 (qa §⑤ #2): checkSchema wraps the actually-async schema check in a dispatcher-safe
        // Task.Run(...).GetAwaiter().GetResult() (knowledge preventing-dispatcher-deadlock) because
        // StartupOrchestrator's delegate is synchronous by design (stays in pure AST.Core).
        var orchestrator = new StartupOrchestrator(
            configStore,
            connectionTester,
            () =>
            {
                var result = Task.Run(() => schemaChecker.CheckAsync(expectedSchemaVersion)).GetAwaiter().GetResult();
                return new SchemaCheckOutcome(result.IsMatch, result.BlockMessage);
            },
            ex => Log.Error(ex, "Lỗi không mong đợi khi khởi động: {Message}", ex.Message),
            auditLog);

        return orchestrator.Resolve();
    }
}
