using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Presentation;
using FluentAssertions;

namespace AST.Shell.Tests.Presentation;

// §2.7.3 status-label table, exhaustively: isactive/status/dates -> the shared VersionStatus.
// Cancelled (N6) must win over date-based inference even when the dates alone would look "effective" --
// the durable lifecycle marker is the ONLY discriminator, never inferred from dates (§8 #10).
public class VersionStatusResolverTests
{
    private static readonly DateOnly Today = new(2026, 7, 24);

    [Fact]
    public void Inactive_Cancelled_IsBiHuy()
        => VersionStatusResolver.Resolve(
                isActive: false, status: VersionLifecycleStatus.Cancelled,
                Today.AddDays(10), EffectivePeriod.OpenEnd, Today)
            .Should().Be(VersionStatus.Cancelled);

    [Fact]
    public void Inactive_NotCancelled_IsHetHieuLuc()
        => VersionStatusResolver.Resolve(
                isActive: false, status: VersionLifecycleStatus.Normal,
                Today.AddDays(-30), Today.AddDays(-1), Today)
            .Should().Be(VersionStatus.Expired);

    [Fact]
    public void Active_EndedBeforeToday_IsHetHieuLuc()
        => VersionStatusResolver.Resolve(
                isActive: true, status: VersionLifecycleStatus.Normal,
                Today.AddDays(-30), Today.AddDays(-1), Today)
            .Should().Be(VersionStatus.Expired);

    [Fact]
    public void Active_CoveringToday_IsHieuLuc()
        => VersionStatusResolver.Resolve(
                isActive: true, status: VersionLifecycleStatus.Normal,
                Today.AddDays(-10), Today.AddDays(10), Today)
            .Should().Be(VersionStatus.Effective);

    [Fact]
    public void Active_StartsExactlyToday_IsHieuLuc()
        => VersionStatusResolver.Resolve(
                isActive: true, status: VersionLifecycleStatus.Normal,
                Today, Today.AddDays(10), Today)
            .Should().Be(VersionStatus.Effective);

    [Fact]
    public void Active_EndsExactlyToday_IsHieuLuc()
        => VersionStatusResolver.Resolve(
                isActive: true, status: VersionLifecycleStatus.Normal,
                Today.AddDays(-10), Today, Today)
            .Should().Be(VersionStatus.Effective);

    [Fact]
    public void Active_StartsInTheFuture_IsChoHieuLuc()
        => VersionStatusResolver.Resolve(
                isActive: true, status: VersionLifecycleStatus.Normal,
                Today.AddDays(1), EffectivePeriod.OpenEnd, Today)
            .Should().Be(VersionStatus.Pending);

    [Fact]
    public void Inactive_Cancelled_TakesPrecedenceEvenIfDatesLookCurrentlyEffective()
        => VersionStatusResolver.Resolve(
                isActive: false, status: VersionLifecycleStatus.Cancelled,
                Today.AddDays(-5), Today.AddDays(5), Today)
            .Should().Be(VersionStatus.Cancelled);

    [Fact]
    public void Resolve_ReplacedRow_ReturnsReplaced()
    {
        VersionStatusResolver.Resolve(
                isActive: false, status: VersionLifecycleStatus.Replaced,
                Today.AddDays(-30), Today.AddDays(30), Today)
            .Should().Be(VersionStatus.Replaced);
    }
}
