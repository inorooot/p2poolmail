using MailKit;
using MailKit.Net.Imap;

namespace p2poolmail
{
    /// <summary>
    /// Core of the IMAP client service: fields, construction and disposal.
    /// Split across partial class files: <c>Tls</c> (client factory + TLS policy),
    /// <c>Connection</c> (connect/auth/folder access), <c>Idle</c> (IDLE loop and
    /// polling wait) and <c>Messages</c> (fetch/process mail).
    /// </summary>
    public partial class ImapClientService : IDisposable
    {
        private enum ImapRunState
        {
            Disconnected,
            Connecting,
            Idle,
            Polling,
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
        private bool _idleFallbackWarned;
        private ImapRunState _state = ImapRunState.Disconnected;
        private bool _skippedExistingUnreadAtStartup;
        private UniqueId? _lastProcessedUid;

        private TimeSpan _pollInterval = TimeSpan.FromSeconds(60); // IF NO IDLE USE POLLING Interval
        // 600s (10 minutes) is a provider-friendly heartbeat that keeps the connection
        // alive without busy-looping the server. If the server does not push EXISTS
        // during IDLE, new-mail detection is bounded by this interval.
        private TimeSpan _idleHeartbeat = TimeSpan.FromSeconds(600);
        private int _candidateLimit = 5;
        private TimeSpan _idleMaxRetryDelay = TimeSpan.FromSeconds(30);
        private int _idleFailureCount;
        // Guard against re-entrant processing of the same UID if the server emits a
        // duplicate notification before the prior pass has advanced the watermark.
        private readonly HashSet<UniqueId> _inFlightUids = new();
        /// <summary>UIDVALIDITY of INBOX at the time <see cref="_lastProcessedUid"/> was captured. A change invalidates all UIDs.</summary>
        private uint? _lastUidValidity;
        /// <summary>Retry bookkeeping for a message that repeatedly fails processing (poison message).</summary>
        private UniqueId? _stuckUid;
        private int _stuckUidAttempts;
        private const int MaxAttemptsPerUid = 3;

        public ImapClientService(string host, int port = 993, bool useSsl = true, string? username = null, string? password = null, Action<string>? logger = null, bool ignoreCertificateErrors = false, TimeSpan? idleHeartbeat = null, int candidateLimit = 5)
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

            _candidateLimit = Math.Max(1, candidateLimit);
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
