using ErrorOr;

namespace AST.Core.Data;

public enum BreakGlassHealth { Valid, Tampered, UnsignedDebug, Missing, Unreadable }

public sealed record BreakGlassAdmin(string User, DateTime? CreatedUtc);

public sealed record BreakGlassView(
    IReadOnlyList<BreakGlassAdmin> Admins,
    BreakGlassHealth Health,
    string FilePath,
    DateTime? LastModifiedUtc,
    string? LastSignerFingerprint);

[SharedComponent]
public interface IBreakGlassAdminService
{
    ErrorOr<BreakGlassView> Load();
    ErrorOr<Success> Save(IReadOnlyList<string> admins, byte[]? privateKey, string? passphrase);
}
