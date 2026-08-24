using AST.Core.EffectivePeriod;
using AST.Core.Presentation;

namespace AST.Shell.Tests.Presentation;

// §2.7.3 status-label table, exhaustively: isactive/cancelled/dates -> the shared 4-state VersionStatus.
// Cancelled (N6) must win over date-based inference even when the dates alone would look "effective" --
// the durable `cancelled` marker is the ONLY discriminator, never inferred from dates (§8 #10).
public class VersionStatusResolverTests
{
    private static readonly DateOnly Today = new(2026, 7, 24);

    [Fact]
    public void Inactive_Cancelled_IsBiHuy()
        => Assert.Equal(
            VersionStatus.Cancelled,
            VersionStatusResolver.Resolve(isActive: false, cancelled: true, Today.AddDays(10), EffectivePeriod.OpenEnd, Today));

    [Fact]
    public void Inactive_NotCancelled_IsHetHieuLuc()
        => Assert.Equal(
            VersionStatus.Expired,
            VersionStatusResolver.Resolve(isActive: false, cancelled: false, Today.AddDays(-30), Today.AddDays(-1), Today));

    [Fact]
    public void Active_EndedBeforeToday_IsHetHieuLuc()
        => Assert.Equal(
            VersionStatus.Expired,
            VersionStatusResolver.Resolve(isActive: true, cancelled: false, Today.AddDays(-30), Today.AddDays(-1), Today));

    [Fact]
    public void Active_CoveringToday_IsHieuLuc()
        => Assert.Equal(
            VersionStatus.Effective,
            VersionStatusResolver.Resolve(isActive: true, cancelled: false, Today.AddDays(-10), Today.AddDays(10), Today));

    [Fact]
    public void Active_StartsExactlyToday_IsHieuLuc()
        => Assert.Equal(
            VersionStatus.Effective,
            VersionStatusResolver.Resolve(isActive: true, cancelled: false, Today, Today.AddDays(10), Today));

    [Fact]
    public void Active_EndsExactlyToday_IsHieuLuc()
        => Assert.Equal(
            VersionStatus.Effective,
            VersionStatusResolver.Resolve(isActive: true, cancelled: false, Today.AddDays(-10), Today, Today));

    [Fact]
    public void Active_StartsInTheFuture_IsChoHieuLuc()
        => Assert.Equal(
            VersionStatus.Pending,
            VersionStatusResolver.Resolve(isActive: true, cancelled: false, Today.AddDays(1), EffectivePeriod.OpenEnd, Today));

    [Fact]
    public void Inactive_Cancelled_TakesPrecedenceEvenIfDatesLookCurrentlyEffective()
        => Assert.Equal(
            VersionStatus.Cancelled,
            VersionStatusResolver.Resolve(isActive: false, cancelled: true, Today.AddDays(-5), Today.AddDays(5), Today));
}
