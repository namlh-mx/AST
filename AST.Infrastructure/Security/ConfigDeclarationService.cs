using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Security;
using ErrorOr;

namespace AST.Infrastructure.Security;

public sealed class ConfigDeclarationService(
    IConnectionConfigStore connectionStore,
    IBreakGlassStore breakGlassStore,
    ICurrentWindowsUser currentUser,
    IConfigAuditLog audit) : IConfigDeclarationService
{
    public ErrorOr<Success> SaveConnection(ConnectionFields fields, byte[]? privateKey, string? passphrase)
    {
        var existing = breakGlassStore.Read();
        if (existing.IsError)
        {
            if (existing.FirstError.Type == ErrorType.NotFound)
            {
                // First-run: File B does not exist yet -> create it with root admin = the current Windows user.
                var user = currentUser.Username;
                if (string.IsNullOrWhiteSpace(user))
                    return Error.Validation("Config.CurrentUserUnknown",
                        "Không xác định được tài khoản Windows hiện tại để ghi nhận root admin.");

                var createB = breakGlassStore.Save(new[] { user }, privateKey, passphrase);
                if (createB.IsError) return createB.Errors;
                // Best-effort lifecycle audit — never change the save result.
                _ = audit.Append(new ConfigAuditEvent("FileB", "Create", null, "Success", null), privateKey, passphrase);
            }
            else
            {
                // File B exists but is corrupt/tampered -> fail clearly, does NOT silently overwrite.
                return existing.Errors;
            }
        }

        var saveA = connectionStore.Save(fields, privateKey, passphrase);
        if (saveA.IsError) return saveA.Errors;
        _ = audit.Append(
            new ConfigAuditEvent("FileA", "Update", null, "Success", null,
                new ConfigConnectionSnapshot(fields.Host.Trim(), fields.Port, fields.Database.Trim(), fields.User.Trim())),
            privateKey, passphrase);
        return saveA;
    }

    public ErrorOr<ConnectionFields> GetCurrent() => connectionStore.Read();

    public ErrorOr<IReadOnlyList<ConnectionHistoryEntry>> GetHistory()
    {
        var read = audit.Read();
        if (read.IsError) return read.Errors;
        // Concrete List: ErrorOr's implicit conversion is not applied from an interface-typed source.
        var list = new List<ConnectionHistoryEntry>();
        foreach (var r in read.Value)
        {
            if (r.Content is not { Target: "FileA", Action: "Update", Snapshot: { } s }) continue;
            list.Add(new ConnectionHistoryEntry(
                r.Content.TsUtc, r.Content.Actor.User, r.Content.Actor.Machine,
                s.Host, s.Port, s.Database, s.User));
        }
        list.Reverse(); // append order is oldest-first; history shows newest-first
        return list;
    }
}
