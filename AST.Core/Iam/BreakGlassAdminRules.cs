using AST.Core.Security;
using ErrorOr;

namespace AST.Core.Iam;

public static class BreakGlassAdminRules
{
    public static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string> raw)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in raw)
        {
            var n = WindowsUsernameNormalizer.Normalize(item);
            if (n is null) continue;
            if (!seen.Add(n)) continue;
            result.Add(n);
        }
        return result;
    }

    public static ConfigAuditDiff Diff(IReadOnlyList<string> oldList, IReadOnlyList<string> newList)
    {
        var oldSet = oldList.ToHashSet(StringComparer.Ordinal);
        var newSet = newList.ToHashSet(StringComparer.Ordinal);
        var added = newList.Where(x => !oldSet.Contains(x)).ToList();
        var removed = oldList.Where(x => !newSet.Contains(x)).ToList();
        return new ConfigAuditDiff(added, removed);
    }

    public static ErrorOr<Success> ValidateNonEmpty(IReadOnlyList<string> admins)
    {
        if (admins.Count == 0)
            return Error.Validation("BreakGlass.Empty",
                "Danh sách admin gốc không được rỗng — phải giữ ít nhất 1 người.");
        return Result.Success;
    }
}
