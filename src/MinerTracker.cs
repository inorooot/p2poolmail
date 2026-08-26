namespace p2poolmail;

/// <summary>
/// Debounces the total online miner count with an EMA and raises one event per
/// confirmed change of the rounded count. A change only fires after it persists
/// for the whole confirmation duration; reverting before that cancels it.
/// </summary>
internal sealed class MinerTracker
{
    private const double TrendEpsilon = 0.01;

    public enum SmoothedTrend { Down = -1, Stable = 0, Up = 1 }

    /// <summary>Raised once per confirmed rounded-count change: (previous, current, trend, raw delta).</summary>
    public event Action<int, int, SmoothedTrend, double>? SmoothedCountChanged;

    // Guards all mutable state below. The event handler runs outside the lock.
    private readonly object _lock = new();
    private readonly double _alpha;
    private readonly TimeSpan _confirmDuration;

    private double _smoothedCount;
    private int _lastRounded;
    private bool _hasBaseline;
    private int? _pendingRounded;
    private DateTimeOffset? _pendingSince;

    public MinerTracker(double? alpha = null, TimeSpan? confirmDuration = null)
    {
        _alpha = alpha ?? 0.3;
        _confirmDuration = confirmDuration ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Reports the total miner count (e.g. every 5 seconds). The first call becomes the baseline
    /// and never fires. The event is raised on the calling thread.
    /// </summary>
    public void ReportMinerTotalCount(int totalOnline, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;

        int previousRounded, newRounded;
        double delta;
        var confirmed = false;

        lock (_lock)
        {
            // First report: adopt the value as-is, no smoothing, no event.
            if (!_hasBaseline)
            {
                _smoothedCount = totalOnline;
                _lastRounded = (int)Math.Round(_smoothedCount);
                _hasBaseline = true;
                return;
            }

            var previousRaw = _smoothedCount;
            previousRounded = _lastRounded;

            _smoothedCount = _alpha * totalOnline + (1 - _alpha) * _smoothedCount;
            newRounded = (int)Math.Round(_smoothedCount);
            delta = _smoothedCount - previousRaw;

            // Rounded value unchanged: a pending change was just a flicker -> cancel it.
            if (newRounded == previousRounded)
            {
                _pendingRounded = null;
                _pendingSince = null;
                return;
            }

            if (_pendingRounded != newRounded)
            {
                // New target value: start (or restart) its confirmation window.
                _pendingRounded = newRounded;
                _pendingSince = current;
                return;
            }

            // Same target seen again before the window elapsed -> keep waiting.
            if (current - _pendingSince!.Value < _confirmDuration)
                return;

            // Change confirmed.
            _lastRounded = newRounded;
            _pendingRounded = null;
            _pendingSince = null;
            confirmed = true;
        }

        if (!confirmed)
            return;

        var trend = delta > TrendEpsilon ? SmoothedTrend.Up
                  : delta < -TrendEpsilon ? SmoothedTrend.Down
                  : SmoothedTrend.Stable;

        SmoothedCountChanged?.Invoke(previousRounded, newRounded, trend, delta);
    }
}
