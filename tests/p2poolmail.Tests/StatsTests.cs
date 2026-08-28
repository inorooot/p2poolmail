using System.Reflection;
using p2poolmail;

namespace p2poolmail.Tests;

/// <summary>
/// Tests Stats.Observe payout parsing/counting. The counters are private static,
/// read back via reflection. Stats state is static, so each test resets it.
/// </summary>
public class StatsTests
{
    public StatsTests()
    {
        ResetCounters();
    }

    private static void ResetCounters()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        typeof(Stats).GetField("_payoutCount", flags)!.SetValue(null, 0L);
        typeof(Stats).GetField("_payoutTotalXmr", flags)!.SetValue(null, 0m);
        typeof(Stats).GetField("_sharesFound", flags)!.SetValue(null, 0L);
    }

    private static (long payouts, decimal xmr, long shares) ReadCounters()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        return (
            (long)typeof(Stats).GetField("_payoutCount", flags)!.GetValue(null)!,
            (decimal)typeof(Stats).GetField("_payoutTotalXmr", flags)!.GetValue(null)!,
            (long)typeof(Stats).GetField("_sharesFound", flags)!.GetValue(null)!);
    }

    [Fact]
    public void Observe_PayoutLine_ParsesAmountAndCounts()
    {
        var line = "2026-08-29 12:00:00.0000 P2Pool PSolo moneroocean_4A6s got a payout of 0.001873661100 XMR in block 3732713";
        Stats.Observe(Notification.Type.GotaPayout, line);

        var (payouts, xmr, shares) = ReadCounters();
        Assert.Equal(1, payouts);
        Assert.Equal(0.001873661100m, xmr);
        Assert.Equal(0, shares);
    }

    [Fact]
    public void Observe_MultiplePayoutLines_Sum()
    {
        Stats.Observe(Notification.Type.GotaPayout, "x got a payout of 0.5 XMR in block 1");
        Stats.Observe(Notification.Type.GotaPayout, "x got a payout of 0.25 XMR in block 2");

        var (payouts, xmr, _) = ReadCounters();
        Assert.Equal(2, payouts);
        Assert.Equal(0.75m, xmr);
    }

    [Fact]
    public void Observe_MalformedAmount_CountsPayoutButAddsZero()
    {
        Stats.Observe(Notification.Type.GotaPayout, "got a payout of not-a-number XMR");

        var (payouts, xmr, _) = ReadCounters();
        Assert.Equal(1, payouts);
        Assert.Equal(0m, xmr);
    }

    [Fact]
    public void Observe_LineWithoutMarker_AddsNothingButCounts()
    {
        Stats.Observe(Notification.Type.GotaPayout, "totally unrelated log line");

        var (payouts, xmr, _) = ReadCounters();
        Assert.Equal(1, payouts);
        Assert.Equal(0m, xmr);
    }

    [Fact]
    public void Observe_ShareFound_IncrementsShares()
    {
        Stats.Observe(Notification.Type.ShareFound, "SHARE FOUND mainchain height 123");
        Stats.Observe(Notification.Type.ShareFound, "another share");

        var (_, _, shares) = ReadCounters();
        Assert.Equal(2, shares);
        var (payouts, _, _) = ReadCounters();
        Assert.Equal(0, payouts);
    }

    [Fact]
    public void Observe_OtherTypes_AreIgnored()
    {
        Stats.Observe(Notification.Type.ZMQNotRunning, "ZMQ is not running");

        var (payouts, xmr, shares) = ReadCounters();
        Assert.Equal(0, payouts);
        Assert.Equal(0m, xmr);
        Assert.Equal(0, shares);
    }
}
