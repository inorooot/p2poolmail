namespace p2poolmail;

/// <summary>
/// Central emoji icon set used in email subjects and bodies.
/// Keeps a consistent visual language across every notification email and
/// makes messages easy to recognize/filter at a glance in the inbox.
/// </summary>
internal static class EmailIcons
{
    // ---- Severity levels (subject prefixes) ----
    public const string Info = "ℹ️";     // informational event
    public const string Warning = "⚠️";  // degraded-state alert
    public const string Alert = "🚨";    // critical fault alert
    public const string Ok = "✅";       // recovery / success

    // ---- Mining events ----
    public const string ShareFound = "⛏️"; // a share was found
    public const string Payout = "💰";     // payout received

    // ---- Reports & trends ----
    public const string Stats = "📊";      // summary/daily reports
    public const string TrendUp = "📈";    // worker count rising
    public const string TrendDown = "📉";  // worker count falling
    public const string TrendFlat = "➖";  // worker count stable
    public const string Workers = "👷";    // worker-related lines

    // ---- Report body labels ----
    public const string Hashrate = "⚡";   // hashrate values
    public const string Effort = "🎯";     // effort values
    public const string Received = "💵";   // received/payment totals
    public const string Mail = "📬";       // mailbox-request replies
    public const string Sync = "🔄";       // syncing activity
}
