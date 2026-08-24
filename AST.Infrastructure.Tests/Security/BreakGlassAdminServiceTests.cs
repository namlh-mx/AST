using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Security;
using AST.Infrastructure.Security;
using ErrorOr;

namespace AST.Infrastructure.Tests.Security;

public class BreakGlassAdminServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ast-bgadmin-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigPaths _paths;

    public BreakGlassAdminServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = ConfigPathResolver.Resolve(_dir);
    }

    // Spy File B store: canned Read result; records Save calls verbatim.
    private sealed class SpyStore(ErrorOr<IReadOnlyList<string>> readResult) : IBreakGlassStore
    {
        public List<IReadOnlyList<string>> Saved { get; } = new();
        public ErrorOr<IReadOnlyList<string>> Read() => readResult;
        public ErrorOr<Success> Save(IReadOnlyList<string> admins, byte[]? privateKey, string? passphrase)
        {
            Saved.Add(admins);
            return Result.Success;
        }
    }

    private sealed class SpyAudit(IReadOnlyList<ConfigAuditRecord>? records = null) : IConfigAuditLog
    {
        public List<ConfigAuditEvent> Events { get; } = new();
        public ErrorOr<Success> Append(ConfigAuditEvent evt, byte[]? privateKey, string? passphrase)
        {
            Events.Add(evt);
            return Result.Success;
        }
        public ErrorOr<IReadOnlyList<ConfigAuditRecord>> Read()
        {
            if (records is null) return Array.Empty<ConfigAuditRecord>();
            return records.ToArray();
        }
        public ErrorOr<ConfigAuditIntegrity> VerifyIntegrity() => new ConfigAuditIntegrity(true, null, true);
    }

    private BreakGlassAdminService Service(
        ErrorOr<IReadOnlyList<string>> storeRead,
        SpyStore? store = null,
        SpyAudit? audit = null,
        bool requireSignature = true)
        => new(store ?? new SpyStore(storeRead), audit ?? new SpyAudit(), _paths, requireSignature);

    [Fact]
    public void Save_valid_list_calls_store_then_appends_FileB_Update_with_diff()
    {
        var store = new SpyStore(new[] { "a" });
        var audit = new SpyAudit();
        var svc = Service(new[] { "a" }, store, audit);

        var r = svc.Save(new[] { "EXAMPLE\\a", "b" }, privateKey: null, passphrase: null);

        Assert.False(r.IsError);
        Assert.Single(store.Saved);
        Assert.Equal(new[] { "a", "b" }, store.Saved[0]); // normalized + deduped before saving
        var evt = Assert.Single(audit.Events);
        Assert.Equal("FileB", evt.Target);
        Assert.Equal("Update", evt.Action);
        Assert.NotNull(evt.Diff);
        Assert.Equal(new[] { "b" }, evt.Diff!.Added);
        Assert.Empty(evt.Diff.Removed);
    }

    [Fact]
    public void Save_empty_list_returns_validation_error_and_does_not_touch_store_or_audit()
    {
        var store = new SpyStore(Array.Empty<string>());
        var audit = new SpyAudit();
        var svc = Service(Array.Empty<string>(), store, audit);

        var r = svc.Save(Array.Empty<string>(), null, null);

        Assert.True(r.IsError);
        Assert.Equal(ErrorOr.ErrorType.Validation, r.FirstError.Type);
        Assert.Empty(store.Saved);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public void Load_success_maps_to_Valid_when_signature_required()
    {
        var svc = Service(new[] { "a", "b" }, requireSignature: true);

        var view = svc.Load();

        Assert.False(view.IsError);
        Assert.Equal(new[] { "a", "b" }, view.Value.Admins.Select(a => a.User));
        Assert.Equal(BreakGlassHealth.Valid, view.Value.Health);
        Assert.Equal(_paths.AdminsFile, view.Value.FilePath);
    }

    [Fact]
    public void Load_success_maps_to_UnsignedDebug_when_signature_not_required()
    {
        var svc = Service(new[] { "a" }, requireSignature: false);
        Assert.Equal(BreakGlassHealth.UnsignedDebug, svc.Load().Value.Health);
    }

    [Fact]
    public void Load_SignatureInvalid_maps_to_Tampered()
    {
        var svc = Service(ConfigErrors.SignatureInvalid("File B"));
        var view = svc.Load();
        Assert.False(view.IsError);
        Assert.Equal(BreakGlassHealth.Tampered, view.Value.Health);
    }

    [Fact]
    public void Load_NotDeclared_maps_to_Missing()
    {
        var svc = Service(ConfigErrors.NotDeclared("File B"));
        var view = svc.Load();
        Assert.False(view.IsError);
        Assert.Equal(BreakGlassHealth.Missing, view.Value.Health);
    }

    [Fact]
    public void Load_IoError_propagates_as_error()
    {
        var svc = Service(ConfigErrors.IoError("File B"));
        var view = svc.Load();
        Assert.True(view.IsError);
        Assert.Equal("Config.IoError", view.FirstError.Code);
    }

    [Fact]
    public void Load_surfaces_last_signed_FileB_fingerprint_from_the_audit_log()
    {
        var signed = new ConfigAuditRecord(
            new ConfigAuditContent(3, "2026-07-12T00:00:00Z", new ConfigAuditActor("boss", "PC"),
                "FileB", "Update", null, "Success", null, "deadbeefdeadbeef", ConfigAuditChain.GenesisPrevHash),
            Hash: "hash3", TipSig: "sig3");
        var audit = new SpyAudit(new[] { signed });
        var svc = Service(new[] { "a" }, audit: audit);

        Assert.Equal("deadbeefdeadbeef", svc.Load().Value.LastSignerFingerprint);
    }

    [Fact]
    public void Load_derives_created_date_from_the_earliest_audit_record_that_added_the_user()
    {
        var r1 = new ConfigAuditRecord(
            new ConfigAuditContent(1, "2026-07-01T08:00:00Z", new ConfigAuditActor("boss", "PC"),
                "FileB", "Update", new ConfigAuditDiff(new[] { "a" }, Array.Empty<string>()),
                "Success", null, "fp", ConfigAuditChain.GenesisPrevHash),
            Hash: "h1", TipSig: "s1");
        var r2 = new ConfigAuditRecord(
            new ConfigAuditContent(2, "2026-07-02T09:00:00Z", new ConfigAuditActor("boss", "PC"),
                "FileB", "Update", new ConfigAuditDiff(new[] { "b" }, Array.Empty<string>()),
                "Success", null, "fp", "h1"),
            Hash: "h2", TipSig: "s2");
        var audit = new SpyAudit(new[] { r1, r2 });
        var svc = Service(new[] { "a", "b", "c" }, audit: audit); // "c" has no add-record

        var admins = svc.Load().Value.Admins;

        Assert.Equal(new[] { "a", "b", "c" }, admins.Select(x => x.User));
        Assert.Equal(new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc), admins[0].CreatedUtc);
        Assert.Equal(new DateTime(2026, 7, 2, 9, 0, 0, DateTimeKind.Utc), admins[1].CreatedUtc);
        Assert.Null(admins[2].CreatedUtc); // no add-record -> unknown
    }

    [Fact]
    public void Load_uses_the_most_recent_add_when_a_user_was_re_added()
    {
        var r1 = new ConfigAuditRecord(
            new ConfigAuditContent(1, "2026-07-01T08:00:00Z", new ConfigAuditActor("boss", "PC"),
                "FileB", "Update", new ConfigAuditDiff(new[] { "a" }, Array.Empty<string>()),
                "Success", null, "fp", ConfigAuditChain.GenesisPrevHash),
            Hash: "h1", TipSig: "s1");
        var r2 = new ConfigAuditRecord(
            new ConfigAuditContent(2, "2026-07-05T10:00:00Z", new ConfigAuditActor("boss", "PC"),
                "FileB", "Update", new ConfigAuditDiff(new[] { "a" }, Array.Empty<string>()),
                "Success", null, "fp", "h1"),
            Hash: "h2", TipSig: "s2");
        var audit = new SpyAudit(new[] { r1, r2 });
        var svc = Service(new[] { "a" }, audit: audit);

        var admins = svc.Load().Value.Admins;

        Assert.Equal(new DateTime(2026, 7, 5, 10, 0, 0, DateTimeKind.Utc), admins[0].CreatedUtc); // latest add
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
