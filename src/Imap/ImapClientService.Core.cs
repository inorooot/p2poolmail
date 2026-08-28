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

        private TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
        private TimeSpan _idleHeartbeat = TimeSpan.FromMinutes(9);
        private TimeSpan _idleResetInterval = TimeSpan.FromMinutes(9);
        private int _candidateLimit = 5;
        private TimeSpan _idleRetryDelay = TimeSpan.FromSeconds(2);
        private TimeSpan _idleMaxRetryDelay = TimeSpan.FromSeconds(30);
        private int _idleFailureCount;

        public ImapClientService(string host, int port = 993, bool useSsl = true, string? username = null, string? password = null, Action<string>? logger = null, bool ignoreCertificateErrors = false, TimeSpan? idleResetInterval = null, int candidateLimit = 5)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _useSsl = useSsl;
            _username = username;
            _password = password;
            _logger = logger;
            _ignoreCertificateErrors = ignoreCertificateErrors;

            if (idleResetInterval.HasValue)
            {
                _idleResetInterval = idleResetInterval.Value;
                _idleHeartbeat = idleResetInterval.Value;
            }

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

        public TimeSpan IdleRetryDelay
        {
            get => _idleRetryDelay;
            set => _idleRetryDelay = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(1);
        }

        public TimeSpan IdleMaxRetryDelay
        {
            get => _idleMaxRetryDelay;
            set => _idleMaxRetryDelay = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(10);
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
