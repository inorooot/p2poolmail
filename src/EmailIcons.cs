namespace p2poolmail;
 
internal static class EmailIcons
{
    // ---- Severity levels (subject prefixes) ----
    public const string Info = "ℹ️";     // informational event / reply
    public const string Warning = "⚠️";  // degraded-state alert
    public const string Alert = "🚨";    // critical fault alert
    public const string Ok = "✅";       // recovery / success

    // ---- Mining events ----
    public const string ShareFound = "⛏️"; // a share was found
    public const string Payout = "💰";     // payout received / payment totals

    // ---- Reports & workers ----
    public const string Stats = "🗓️";      // summary/daily reports
    public const string Workers = "👷";    // worker count and trend lines
}
