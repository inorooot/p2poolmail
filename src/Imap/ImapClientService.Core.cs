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
    public sealed partial class ImapClientService : IDisposable
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

        // MailKit ImapClient is NOT thread-safe. A single SyncRoot gate serializes
        // all client access (connect, idle, fetch, disconnect) to prevent races
        // between the IDLE loop and external calls (e.g., StopIdleAsync).
        private readonly SemaphoreSlim _syncRoot = new(1, 1);
        // Serializes StartIdleAsync/StopIdleAsync so two callers cannot create two loops.
        private readonly object _idleLifecycleLock = new();
        private readonly ImapClient _client;
        private CancellationTokenSource? _idleCts;
        private Task? _idleTask;
        private ImapRunState _state = ImapRunState.Disconnected;
        private bool _existingMailSkipped;
        private UniqueId? _lastProcessedUid;

        // IDLE heartbeat (provider-friendly): keeps connection alive and checks for mail.
        // This is a SAFETY NET only - real-time message detection relies on server push
        // via IMAP IDLE (RFC 2177). When a new message arrives, the server sends an
        // EXISTS notification immediately, and we process it without waiting for heartbeat.
        // 10 minutes is server-friendly for 24/7 operation while staying well within
        // the 30-minute idle timeout used by most IMAP servers.
        private TimeSpan _idleHeartbeat = DefaultHeartbeat;

        private int _idleFailureCount;
        /// <summary>UIDVALIDITY at the time <see cref="_lastProcessedUid"/> was captured. A change invalidates all UIDs.</summary>
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

        /// <summary>
        /// Acquires the SyncRoot gate with a timeout. Returns true if acquired.
        /// </summary>
        private async Task<bool> AcquireLockAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _syncRoot.WaitAsync(ConnectionLockTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try { StopIdleAsync().GetAwaiter().GetResult(); } catch { }
            DisconnectQuiet();
            _client.Dispose();
            _syncRoot.Dispose();
        }
    }
}
