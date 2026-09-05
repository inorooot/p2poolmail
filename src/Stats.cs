// Copyright (c) 2026 inorooot. MIT License.

using System.Globalization;

namespace p2poolmail;

/// <summary>
/// Accumulates mining totals (payouts, shares found) from p2pool.log event lines and
/// emails a scheduled summary report driven by the [daily_stats] section of Config.toml.
/// Totals are kept in memory only and reported as "last 24 hours"; each report resets
/// the counters, so consecutive reports roughly tile the timeline without strict
/// window bookkeeping.
/// </summary>
internal static class Stats
{
    private const string PayoutMarker = "got a payout of ";
    private const string PayoutUnit = " XMR";

    // Guards all mutable state below. Called from the tailer thread and the scheduler.
    private static readonly object Lock = new();
    private static long _payoutCount;
    private static decimal _payoutTotalXmr;
    private static long _sharesFound;

    /// <summary>
    /// Records one matched log line into the daily totals. Hooked from
    /// NotifyManager.Handle for every keyword hit, regardless of notification flags.
    /// Thread-safe: all counter updates are protected by the same Lock.
    /// </summary>
    internal static void Observe(Notification.Type type, ReadOnlySpan<char> line)
    {
        switch (type)
        {
            case Notification.Type.GotaPayout:
                lock (Lock)
                {
                    _payoutCount++;
                    _payoutTotalXmr += ParsePayoutAmount(line);
                }
                break;

            case Notification.Type.ShareFound:
                lock (Lock)
                {
                    _sharesFound++;
                }
                break;
        }
    }

    /// <summary>
    /// Builds and emails the summary report (data of the last 24 hours), then resets
    /// the counters. Optional <paramref name="subject"/> prefixes the default subject.
    /// </summary>
    public static void SendDailyStatsReport(string? subject = null)
    {
        long payouts, shares;
        decimal xmr;

        lock (Lock)
        {
            payouts = _payoutCount;
            xmr = _payoutTotalXmr;
            shares = _sharesFound;

            // Start fresh so the next report again covers roughly the last 24 hours.
            _payoutCount = 0;
            _payoutTotalXmr = 0;
            _sharesFound = 0;
        }

        var title = "Daily p2pool report (last 24h)";
        var body =
            $"{EmailIcons.Info} Hello workers, Here's what happened in the last 24 hours:\r\n" +
            $"{EmailIcons.Payout} Received    : {xmr.ToString("0.############", CultureInfo.InvariantCulture)} XMR ({payouts} payment(s))\r\n" +
            $"{EmailIcons.ShareFound} Share found : {shares}\r\n" +
            $"Current: \r\n {LocalStratum.StratumTxtFormatLittle()}";

        EmailQueue.Enqueue(subject is null ? title : $"{subject} | {title}", body, "daily-stats");
        CommonHelper.WriteLine($"daily stats: report queued ({payouts} payout(s), {shares} share(s) found)");
    }

    /// <summary>
    /// Extracts the XMR amount from a payout line, e.g.
    /// "... got a payout of 0.001873661100 XMR in block 3732713". Returns 0 when the
    /// line does not match the expected shape so counting never breaks tracking.
    /// </summary>
    private static decimal ParsePayoutAmount(ReadOnlySpan<char> line)
    {
        var start = line.IndexOf(PayoutMarker, StringComparison.Ordinal);
        if (start < 0)
            return 0m;
        start += PayoutMarker.Length;

        var tail = line[start..];
        var offset = tail.IndexOf(PayoutUnit, StringComparison.Ordinal);
        if (offset <= 0)
            return 0m;

        return decimal.TryParse(tail[..offset], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : 0m;
    }
}
