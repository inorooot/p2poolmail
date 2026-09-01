using MailKit;
using MailKit.Net.Imap;

namespace p2poolmail
{
    /// <summary>
    /// Core of the IMAP client service: fields, construction and disposal.
    /// Split across partial class files: <c>Tls</c> (client factory + TLS policy),
    /// <c>Connection</c> (connect/auth/folder access), <c>Idle</c> (IDLE loop) and
    /// <c>Messages</c> (fetch/process mail).
    /// </summary>
    public partial class ImapClientService : IDisposable
    {
        private enum ImapRunState
        {
            Disconnected,
            Connecting,
            Idle,
            Reconnecting,
            Stopped
        }

        private readonly string _host;
        private readonly int _port;
        private readonly bool _useSsl;
        private readonly string? _username;
        private readonly string? _password;
        private readonly Action<string>? _logger;
        private readonly bool _ignoreCertificateErrors;

        private ImapClient _client;
        private CancellationTokenSource? _idleCts;
        private Task? _idleTask;
        private ImapRunState _state = ImapRunState.Disconnected;
        private bool _skippedExistingUnreadAtStartup;
        private UniqueId? _lastProcessedUid;

        // IDLE heartbeat (provider-friendly): keeps connection alive and checks for mail.
        // Maximum delay for new message detection. Extended to 10 minutes as requested.
        private TimeSpan _idleHeartbeat = TimeSpan.FromSeconds(600);
        
        // Reconnect immediately for network recovery: no delay/backoff, so new mail is
        // not held up by a deliberate reconnect gap after a transient IMAP failure.
        private TimeSpan _idleMaxRetryDelay = TimeSpan.Zero;
        
        // Timeout for NOOP keep-alive commands to prevent permanent hangs.
        private TimeSpan _noapTimeout = TimeSpan.FromSeconds(10);
        private int _idleFailureCount;
        // Track the UID currently being processed to avoid duplicate handling if server
        // emits notification before watermark is advanced.
        private UniqueId? _inFlightUid;
        /// <summary>UIDVALIDITY of INBOX at the time <see cref="_lastProcessedUid"/> was captured. A change invalidates all UIDs.</summary>
        private uint? _lastUidValidity;

        public ImapClientService(string host, int port = 993, bool useSsl = true, string? username = null, string? password = null, Action<string>? logger = null, bool ignoreCertificateErrors = false, TimeSpan? idleHeartbeat = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _useSsl = useSsl;
            _username = username;
            _password = password;
            _logger = logger;
            _ignoreCertificateErrors = ignoreCertificateErrors;

            if (idleHeartbeat.HasValue)
                _idleHeartbeat = idleHeartbeat.Value;

            _client = CreateClient();
        }

        private void SetState(ImapRunState state)
        {
            if (_state == state)
                return;

            _state = state;
            _logger?.Invoke($"IMAP state -> {state}");
        }

        private static void TryCancel(CancellationTokenSource? cts)
        {
            try
            {
                cts?.Cancel();
            }
            catch { }
        }

        public void Dispose()
        {
            try { StopIdleAsync().GetAwaiter().GetResult(); } catch { }
            try { if (_client.IsConnected) _client.Disconnect(true); } catch { }
            _client.Dispose();
        }
    }
}
