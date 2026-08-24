using System.Globalization;
using ErrorOr;

namespace AST.Core.EffectivePeriod;

// Pure implementation over a pre-loaded candidate set (§3.3) — does not touch the DB.
public sealed class EffectivePeriodResolver : IEffectivePeriodResolver
{
    public ErrorOr<TVersion> ResolveAt<TVersion>(IReadOnlyList<TVersion> candidates, DateOnly asOf)
        where TVersion : IVersionRow
    {
        var usable = candidates
            .Where(c => c.IsActive && c.EffectiveFrom <= asOf && asOf <= c.EffectiveTo)
            .ToList();

        // D6 forbids 2 active versions of one identity with intersecting periods, but MySQL has no WITHOUT OVERLAPS
        // -- so this read is the last line of defence. Returning the first match would make the answer depend on row
        // order (LoadActiveVersionsAsync issues no ORDER BY), i.e. permissions and parameter values would resolve
        // non-deterministically and undebuggably. D9: STOP + report clearly, never fall back to a default.
        // An INACTIVE overlapping row is not a violation -- that is what soft-deleted history looks like.
        if (usable.Count > 1)
        {
            return Error.Conflict(
                "EffectivePeriod.OverlappingVersions",
                $"Dữ liệu '{typeof(TVersion).Name}' của định danh {usable[0].IdentityId} có {usable.Count} phiên bản " +
                $"cùng hiệu lực tại ngày {asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} (phiên bản {string.Join(", ", usable.Select(u => u.Id))}). " +
                "Dữ liệu không nhất quán — cần kiểm tra và sửa trước khi tiếp tục.");
        }

        if (usable.Count == 1)
        {
            return usable[0];
        }

        // [Assumption] The interface does not pass a business parameter name ({name} in the conventional error code) —
        // uses the TVersion type name instead until a caller specializes it via Metadata if needed.
        return Error.NotFound(
            "EffectivePeriod.NoCoverage",
            $"Tham số '{typeof(TVersion).Name}' chưa có giá trị hiệu lực tại ngày {asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
    }
}
