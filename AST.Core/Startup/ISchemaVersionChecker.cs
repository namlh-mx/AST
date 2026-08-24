namespace AST.Core.Startup;

// §⑧.1 — the app checks at startup: the DB schema version (MAX(version) in table schema_version)
// must match the app's expected version. Mismatch -> BLOCKS startup (D: the app blocks startup).
// SharedKernel contract (AST.Core), concrete impl in the module that owns the migrations
// (AST.Modules.IAM.Data.SchemaVersionChecker) -- same shape as IEffectivePeriodResolver/ITemporalFkRegistry.
[SharedComponent]
public interface ISchemaVersionChecker
{
    Task<SchemaVersionCheckResult> CheckAsync(int expectedVersion, CancellationToken cancellationToken = default);
}

public sealed record SchemaVersionCheckResult(int ExpectedVersion, int? ActualVersion)
{
    public bool IsMatch => ActualVersion.HasValue && ActualVersion.Value == ExpectedVersion;

    public string BlockMessage => IsMatch
        ? string.Empty
        : $"Schema version DB ({(ActualVersion?.ToString() ?? "chưa có")}) không khớp version app cần ({ExpectedVersion}). App CHẶN khởi động.";
}
