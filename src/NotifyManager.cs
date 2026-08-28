using System.Buffers;
using System.Globalization;

namespace p2poolmail;

internal static class NotifyManager
{
    private static readonly SearchValues<string> Prefix = SearchValues.Create(
        ["WARNING", "ERROR", "NOTICE"], StringComparison.OrdinalIgnoreCase);

    private static readonly AhoCorasickTree Keywords = new(Notification.Keywords);

    public static void Handle(ReadOnlySpan<char> line, Notification.Source source)
    {
        if (source != Notification.Source.Keywords)
            return;

        // Recovery is a time-based decision (fault unseen for RecoveryWindowSeconds),
        // so it must be evaluated on EVERY line. Previously it only ran on lines
        // without a WARNING/ERROR/NOTICE prefix: unrelated error lines skipped the
        // check entirely, delaying or suppressing recovery emails while other,
        // unrelated faults kept logging.
        // Ordering is safe: alert lines extend slot.LastSeen via ObserveAlert below.
        Notification.TryResume(CommonHelper.timestampUtc);

        if (!line.ContainsAny(Prefix))
            return;

        var index = Keywords.FirstMatch(line);
        if (index < 0)
        {
            // Unrelated ERROR/WARNING/NOTICE lines are not recovery signals here
            // anymore (TryResume above already ran); they simply do not refresh
            // any fault's LastSeen, which is correct: they say nothing about it.
            return;
        }

        var type = (Notification.Type)index;

        // Count every matched line into the daily aggregates, independent of the
        // notification enable flags, so the report stays complete even when
        // individual event emails are turned off.
         if(type==Notification.Type.ShareFound || type==Notification.Type.GotaPayout)
         Stats.Observe(type, line);

        if (!Notification.IsEnabled(type))
            return;

        if (Notification.CategoryOf(type) == Notification.Category.Alert)
           //alert
            Notification.ObserveAlert(type, CommonHelper.timestampUtc);
        else
            //event
            EmailQueue.Enqueue(Notification.GetSubject(type), CommonHelper.ParseLogLine(line,CommonHelper.LogParseFields.Content));
    }

    // Single reused instance: the EMA state and confirmation window must persist across
    // polls, otherwise trend/delta detection can never fire.
    private static readonly MinerTracker Miner = new(alpha: 0.6, confirmDuration: TimeSpan.FromSeconds(5));

    static NotifyManager()
    {
        Miner.SmoothedCountChanged += (prev, cur, trend, delta) =>
        {
            CommonHelper.WriteLine($"miner: {prev} -> {cur}, trend={trend}, delta={delta:F2}");

            try
            {
                var subject = $"{EmailIcons.Workers} Worker online count: {prev} -> {cur}";
                // timestampUtc is a unix-seconds long; convert before applying the ":u" format,
                // otherwise interpolation throws FormatException and the mail is silently lost.
                var body = $"\n{EmailIcons.Workers} Previous: {prev}\n{EmailIcons.Workers} Current: {cur}\nTrend: {trend}\n";
                EmailQueue.Enqueue(subject, body, "smoothed-count");
            }
            catch (Exception ex)
            {
                // Notification must never break tracking.
                CommonHelper.WriteError($"miner-event notify failed: {ex.Message}");
            }
        };
    }

    public static void ReportWorkersCount()
    {
        string json = File.ReadAllText(Settings.Current.p2pool_log.StratumApiDir);

        CommonHelper.ReadJsonField(json, "connections", out int miner_count);
        Miner.ReportMinerTotalCount(miner_count);
    }

    // ---------- Keepalive: periodic healthchecks.io heartbeat configured under [keepalive] ----------
    private const int KeepaliveRetryCount = 2;
    private const int KeepaliveRetryDelayMs = 2000;

    // Created lazily (after settings are loaded) because the request timeout comes
    // from [keepalive].timeout_seconds. Single shared instance: avoids socket
    // exhaustion from repeated requests.
    private static HttpClient? _keepaliveHttp;

    /// <summary>
    /// Sends an HTTP GET to [keepalive].ping_url every interval_minutes until
    /// cancelled. The URL is a healthchecks.io hc-ping.com endpoint: each successful
    /// GET tells the service this process is still alive; if checks stop arriving,
    /// healthchecks.io alerts on its side. Results are only written to the console,
    /// no emails.
    /// </summary>
    public static async Task KeepaliveLoopAsync(CancellationToken token)
    {
        var cfg = Settings.Current.keepalive;
        var url = cfg.ping_url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            CommonHelper.WriteWarn("keepalive: [keepalive].ping_url must be a http(s) URL - heartbeat disabled.");
            return;
        }

        // Clamp to at least 1 minute so a bad config cannot turn into a busy loop.
        var interval = TimeSpan.FromMinutes(Math.Max(1, cfg.interval_minutes));
        var timeoutSeconds = Math.Max(1, cfg.timeout_seconds);
        bool isDown = false;

        _keepaliveHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

        CommonHelper.WriteLine($"keepalive: reporting alive every {interval.TotalMinutes:F0} min (timeout={timeoutSeconds}s, retries={KeepaliveRetryCount - 1})");

        while (!token.IsCancellationRequested)
        {
            bool ok = await PingUrlAsync(url, timeoutSeconds, token).ConfigureAwait(false);

            // Log only on state change (steady ok/failed states stay silent).
            if (ok == isDown)
            {
                isDown = !ok;
                CommonHelper.WriteLine($"keepalive: {url} is {(ok ? "ok" : "FAILED")} at {DateTimeOffset.FromUnixTimeSeconds(CommonHelper.timestampUtc):u}");
            }

            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        CommonHelper.WriteLine("keepalive: stopped");
    }

    /// <summary>
    /// Sends a single GET to the hc-ping URL and reports whether it returned 2xx.
    /// Transient failures (network errors / timeouts) are retried up to
    /// <see cref="KeepaliveRetryCount"/> times so one hiccup does not flip the state.
    /// </summary>
    private static async Task<bool> PingUrlAsync(string url, int timeoutSeconds, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await _keepaliveHttp!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // Network errors / timeouts count as a failed report, not as a loop crash.
                var reason = ex is OperationCanceledException
                    ? $"timed out after {timeoutSeconds}s"
                    : ex.Message;

                if (attempt >= KeepaliveRetryCount)
                {
                    CommonHelper.WriteError($"keepalive: GET {url} failed after {attempt} attempts: {reason}");
                    return false;
                }

                CommonHelper.WriteWarn($"keepalive: GET {url} attempt {attempt}/{KeepaliveRetryCount} failed ({reason}), retrying");
                try
                {
                    await Task.Delay(KeepaliveRetryDelayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }
    }

    public static void DailyStats()
    {
        LocalStratum.StratumTxtFormat();

    }

    // ---------- Daily stats: scheduled mining summary report ([daily_stats] in Config.toml) ----------

    /// <summary>
    /// Sends the Stats summary report every [daily_stats].frequency_hours, aligned to
    /// [daily_stats].time_of_day. Runs until cancelled; a failed report is logged and
    /// retried at the next scheduled slot.
    /// </summary>
    public static async Task DailyStatsLoopAsync(CancellationToken token)
    {
        var cfg = Settings.Current.daily_stats;
        var runAt = ParseTimeOfDay(cfg.time_of_day);
        var interval = TimeSpan.FromHours(Math.Max(1, cfg.frequency_hours));

        while (!token.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var next = now.Date + runAt;
            while (next <= now)
                next += interval;

            CommonHelper.WriteLine($"daily stats: next report at {next:yyyy-MM-dd HH:mm}");
            try
            {
                await Task.Delay(next - now, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                Stats.Daily_stats();
            }
            catch (Exception ex)
            {
                CommonHelper.WriteError($"daily stats: report failed: {ex.Message}");
            }
        }

        CommonHelper.WriteLine("daily stats: stopped");
    }

    /// <summary>Parses "HH:mm" (local time); falls back to 18:00 on bad input.</summary>
    private static TimeSpan ParseTimeOfDay(string? value)
    {
        if (TimeSpan.TryParseExact(value?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var t)
            && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
            return t;

        CommonHelper.WriteWarn($"daily stats: invalid time_of_day '{value}' - falling back to 18:00.");
        return new TimeSpan(18, 0, 0);
    }
    public static string RequestByEmail()
    {
        
     return   LocalStratum.StratumTxtFormat();

    }

}
