namespace p2poolmail
{
    /// <summary>Constants for IMAP client timeouts.</summary>
    public partial class ImapClientService
    {
        private static readonly TimeSpan ConnectionLockTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan NoopTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(600); // 10 minutes
    }
}
