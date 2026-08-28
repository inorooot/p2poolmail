using System;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace p2poolmail
{
    public class ImapClientService : IDisposable
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

        private ImapClient CreateClient()
        {
            var client = new ImapClient();

            // MailKit performs online revocation (CRL/OCSP) checking during the TLS
            // handshake. The revocation data is fetched over plain HTTP and the fetch
            // often fails on this network (IPv6 black hole: DNS returns IPv6 addresses
            // that cannot connect, and .NET dials sequentially with no per-address
            // timeout), failing every handshake with "unable to get certificate CRL"
            // even though the certificate chain is perfectly sound (verified live:
            // OnlineRevocation -> OfflineRevocation/RevocationStatusUnknown; NoCheck ->
            // clean handshake in ~1s). Disabling revocation here keeps trust, hostname
            // and validity checks fully enforced; the callback below still rejects
            // chains that do not validate. A MITM is therefore still detected.
            client.CheckCertificateRevocation = false;

            client.ServerCertificateValidationCallback = (_, certificate, chain, sslPolicyErrors) =>
            {
                if (sslPolicyErrors == SslPolicyErrors.None)
                    return true;

                // Revocation data (CRL/OCSP) is fetched over plain HTTP during the TLS
                // handshake. On this network such fetches often fail (IPv6 black hole:
                // addresses resolve but cannot connect, and .NET dials sequentially),
                // which fails the handshake with "unable to get certificate CRL" even
                // though the certificate chain is perfectly sound. Re-validate the
                // chain with revocation checks disabled; accept only if it is then
                // fully valid — trust, expiration and (via sslPolicyErrors) hostname
                // checks are still enforced, so a MITM is still rejected.
                if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors
                    && certificate is X509Certificate2 cert)
                {
                    try
                    {
                        using var noRevocation = new X509Chain();
                        noRevocation.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        if (noRevocation.Build(cert)
                            && noRevocation.ChainStatus.All(s => s.Status == X509ChainStatusFlags.NoError))
                        {
                            _logger?.Invoke($"IMAP TLS for {_host}:{_port}: certificate chain valid, CRL fetch failed - revocation check skipped");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"IMAP TLS chain re-validation failed for {_host}:{_port}: {ex.Message}");
                    }
                }

                if (_ignoreCertificateErrors)
                {
                    _logger?.Invoke($"IMAP SSL certificate validation bypassed for {_host}:{_port} due to: {sslPolicyErrors}");
                    return true;
                }

                return false;
            };

            return client;
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

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_client.IsConnected)
                return;

            SetState(ImapRunState.Connecting);

            try
            {
                await ConnectAndAuthenticateAsync(cancellationToken).ConfigureAwait(false);
                SetState(ImapRunState.Idle);
                LogIdleSupport("Connected");
            }
            catch (Exception ex) when (_ignoreCertificateErrors && !_client.IsConnected)
            {
                _logger?.Invoke($"IMAP normal SSL connect failed for {_host}:{_port}, retrying with certificate validation disabled. Error: {ex.Message}");
                await ReconnectWithCertificateValidationBypassAsync(cancellationToken).ConfigureAwait(false);
                SetState(ImapRunState.Idle);
                LogIdleSupport("Connected using certificate validation bypass");
            }
            catch
            {
                SetState(ImapRunState.Reconnecting);
                if (_client.IsConnected)
                    _client.Disconnect(true);
                throw;
            }
        }

        private async Task ConnectAndAuthenticateAsync(CancellationToken cancellationToken)
        {
            _client.AuthenticationMechanisms.Remove("XOAUTH2");
            await _client.ConnectAsync(_host, _port, _useSsl, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_username))
            {
                await _client.AuthenticateAsync(_username!, _password!, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReconnectWithCertificateValidationBypassAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_client.IsConnected)
                    _client.Disconnect(true);
                _client.Dispose();
            }
            catch { }

            _client = new ImapClient
            {
                ServerCertificateValidationCallback = (_, _, _, _) =>
                {
                    _logger?.Invoke($"IMAP SSL certificate validation bypassed for {_host}:{_port}");
                    return true;
                }
            };

            await ConnectAndAuthenticateAsync(cancellationToken).ConfigureAwait(false);
        }

        private void LogIdleSupport(string prefix)
        {
            var supportsIdle = _client.Capabilities.HasFlag(ImapCapabilities.Idle);
            _logger?.Invoke(supportsIdle
                ? $"{prefix} to {_host}:{_port}; server supports IMAP IDLE"
                : $"{prefix} to {_host}:{_port}; server does not advertise IMAP IDLE");
        }

        public async Task DisconnectAsync()
        {
            await StopIdleAsync().ConfigureAwait(false);
            try
            {
                if (_client.IsConnected)
                    _client.Disconnect(true);
            }
            catch { }
        }

        public async Task<IList<(UniqueId uid, MimeMessage message)>> FetchUnreadAsync(string? folderName = null, int maxMessages = 1, DateTimeOffset? since = null)
        {
            if (!_client.IsConnected)
                await ConnectAsync().ConfigureAwait(false);

            var folder = await ResolveAndOpenFolderAsync(folderName, FolderAccess.ReadOnly).ConfigureAwait(false);
            var uids = (await folder.SearchAsync(SearchQuery.NotSeen).ConfigureAwait(false))
                .OrderBy(x => x.Id)
                .Take(Math.Max(1, maxMessages))
                .ToList();

            var results = new List<(UniqueId uid, MimeMessage message)>();
            foreach (var uid in uids)
            {
                try
                {
                    results.Add((uid, await folder.GetMessageAsync(uid).ConfigureAwait(false)));
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Failed to fetch message {uid}: {ex.Message}");
                }
            }

            return results;
        }

        public async Task MarkAsSeenAsync(string? folderName, IEnumerable<UniqueId> uids)
        {
            if (uids == null)
                return;

            if (!_client.IsConnected)
                await ConnectAsync().ConfigureAwait(false);

            var folder = await ResolveAndOpenFolderAsync(folderName, FolderAccess.ReadWrite).ConfigureAwait(false);
            var uidsList = uids as IList<UniqueId> ?? uids.ToList();
            await folder.AddFlagsAsync(uidsList, MessageFlags.Seen, true).ConfigureAwait(false);
        }

        /// <summary>
        /// Connects (with retries for transient startup failures such as DNS not being ready)
        /// and starts the IDLE loop. Returns true on success; on failure it only logs and
        /// returns false - callers are expected to continue without IMAP.
        /// </summary>
        public async Task<bool> InitializeAsync(Func<MimeMessage, Task> onNewMessage, CancellationToken cancellationToken = default)
        {
            if (onNewMessage == null)
                throw new ArgumentNullException(nameof(onNewMessage));

            const int maxAttempts = 5;
            var delay = TimeSpan.FromSeconds(2);
            var attempt = 0;

            while (true)
            {
                try
                {
                    await ConnectAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    attempt++;
                    if (attempt >= maxAttempts)
                    {
                        _logger?.Invoke($"ERROR: IMAP connect failed after {maxAttempts} attempts, giving up (continuing without IMAP): {ex.Message}");
                        return false;
                    }

                    _logger?.Invoke($"IMAP initial connect failed (attempt {attempt}/{maxAttempts}): {ex.Message} - retrying in {delay.TotalSeconds:F0}s");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                }
            }

            _ = StartIdleAsync(onNewMessage, cancellationToken);
            return true;
        }

        private async Task<IMailFolder> ResolveFolderAsync(string? folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName) || string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase))
                return _client.Inbox;

            try
            {
                return await _client.GetFolderAsync(folderName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to resolve IMAP folder '{folderName}': {ex.Message}");
                throw;
            }
        }

        private async Task<IMailFolder> ResolveAndOpenFolderAsync(string? folderName, FolderAccess access, CancellationToken cancellationToken = default)
        {
            var folder = await ResolveFolderAsync(folderName).ConfigureAwait(false);
            await folder.OpenAsync(access, cancellationToken).ConfigureAwait(false);
            return folder;
        }

        public Task StartIdleAsync(Func<MimeMessage, Task> onNewMessage, CancellationToken cancellationToken = default)
        {
            if (onNewMessage == null)
                throw new ArgumentNullException(nameof(onNewMessage));

            if (_idleTask != null && !_idleTask.IsCompleted)
                return _idleTask;

            _idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _idleCts.Token;

            _idleTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        SetState(ImapRunState.Connecting);
                        await IdleLoopIterationAsync(onNewMessage, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        SetState(ImapRunState.Stopped);
                        break;
                    }
                    catch (Exception ex)
                    {
                        SetState(ImapRunState.Reconnecting);
                        _logger?.Invoke($"Idle loop error: {ex.Message}");
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                        }
                        catch
                        {
                            SetState(ImapRunState.Stopped);
                            break;
                        }
                    }
                }
            }, token);

            return _idleTask ?? Task.CompletedTask;
        }

        private async Task IdleLoopIterationAsync(Func<MimeMessage, Task> onNewMessage, CancellationToken token)
        {
            if (!_client.IsConnected)
                await EnsureConnectedAsync(token).ConfigureAwait(false);

            var folder = await ResolveAndOpenFolderAsync(null, FolderAccess.ReadWrite, token).ConfigureAwait(false);
            var initialCount = folder.Count;
            _logger?.Invoke($"Folder status: {folder.Count} total messages, {folder.Unread} unread");

            await InitializeLastUidIfNeededAsync(folder).ConfigureAwait(false);

            var supportsIdle = _client.Capabilities.HasFlag(ImapCapabilities.Idle);
            using var idleDoneCts = new CancellationTokenSource();
            var sessionId = Guid.NewGuid().ToString("N")[..8];
            _logger?.Invoke($"[{sessionId}] IdleLoopIteration start: folder={folder.FullName}, count={folder.Count}, unread={folder.Unread}");

            var handlers = SubscribeFolderHandlers(folder, idleDoneCts, sessionId);

            try
            {
                await WaitForMailAsync(folder, supportsIdle, token, idleDoneCts.Token, sessionId).ConfigureAwait(false);
            }
            finally
            {
                UnsubscribeFolderHandlers(folder, handlers);
            }

            folder = await ResolveAndOpenFolderAsync(null, FolderAccess.ReadWrite, token).ConfigureAwait(false);
            if (folder.Count != initialCount)
                _logger?.Invoke($"Folder changed: {initialCount} → {folder.Count} messages");

            _logger?.Invoke($"[{sessionId}] IdleLoopIteration before CheckAndProcessNewMessageAsync");
            await CheckAndProcessNewMessageAsync(folder, supportsIdle, onNewMessage, token).ConfigureAwait(false);
            _logger?.Invoke($"[{sessionId}] IdleLoopIteration completed");
        }

        private (EventHandler<EventArgs> CountChanged, EventHandler<MessageEventArgs> MessageExpunged, EventHandler<MessageFlagsChangedEventArgs> MessageFlagsChanged) SubscribeFolderHandlers(IMailFolder folder, CancellationTokenSource idleDoneCts, string sessionId)
        {
            void CancelIdleOnEvent(string eventName, string details)
            {
                try
                {
                    _logger?.Invoke($"[{sessionId}] Folder.{eventName} event: {details}");
                    TryCancel(idleDoneCts);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[{sessionId}] {eventName} handler error: {ex.Message}");
                }
            }

            EventHandler<EventArgs> countChangedHandler = (_, _) => CancelIdleOnEvent("CountChanged", $"Count={folder.Count}, Unread={folder.Unread}");
            EventHandler<MessageEventArgs> messageExpungedHandler = (_, e) => CancelIdleOnEvent("MessageExpunged", $"Index={e.Index}");
            EventHandler<MessageFlagsChangedEventArgs> flagsChangedHandler = (_, e) => CancelIdleOnEvent("MessageFlagsChanged", $"Index={e.Index}, Flags={e.Flags}");

            folder.CountChanged += countChangedHandler;
            folder.MessageExpunged += messageExpungedHandler;
            folder.MessageFlagsChanged += flagsChangedHandler;

            return (countChangedHandler, messageExpungedHandler, flagsChangedHandler);
        }

        private static void UnsubscribeFolderHandlers(IMailFolder folder, (EventHandler<EventArgs> CountChanged, EventHandler<MessageEventArgs> MessageExpunged, EventHandler<MessageFlagsChangedEventArgs> MessageFlagsChanged) handlers)
        {
            try { folder.CountChanged -= handlers.CountChanged; } catch { }
            try { folder.MessageExpunged -= handlers.MessageExpunged; } catch { }
            try { folder.MessageFlagsChanged -= handlers.MessageFlagsChanged; } catch { }
        }

        private async Task EnsureConnectedAsync(CancellationToken token)
        {
            try
            {
                await ConnectAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to connect to {_host}:{_port}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                throw;
            }
        }

        private async Task InitializeLastUidIfNeededAsync(IMailFolder folder)
        {
            if (_skippedExistingUnreadAtStartup)
                return;

            try
            {
                if (folder.Count > 0)
                {
                    var lastSummary = (await folder.FetchAsync(folder.Count - 1, folder.Count - 1, MessageSummaryItems.UniqueId).ConfigureAwait(false)).FirstOrDefault();
                    if (lastSummary != null)
                    {
                        _lastProcessedUid = lastSummary.UniqueId;
                        _logger?.Invoke($"Initialized: skip existing messages up to UID {_lastProcessedUid}");
                    }
                }
                else
                {
                    _logger?.Invoke("Folder empty at startup");
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to initialize last UID: {ex.Message}");
            }
            finally
            {
                _skippedExistingUnreadAtStartup = true;
            }
        }

        private async Task WaitForMailAsync(IMailFolder folder, bool supportsIdle, CancellationToken token, CancellationToken doneToken, string? sessionId = null)
        {
            if (supportsIdle)
            {
                SetState(ImapRunState.Idle);
                _logger?.Invoke($"[{sessionId}] WaitForMailAsync: entering IDLE with heartbeat {_idleHeartbeat}");
                await WaitWithIdleAsync(folder, doneToken, token, _idleHeartbeat).ConfigureAwait(false);
                return;
            }

            SetState(ImapRunState.Polling);
            _logger?.Invoke($"[{sessionId}] WaitForMailAsync: entering polling mode every {_pollInterval}");
            await WaitWithPollingAsync(token).ConfigureAwait(false);
        }

        private async Task WaitWithIdleAsync(IMailFolder folder, CancellationToken doneToken, CancellationToken cancellationToken, TimeSpan? heartbeatOverride = null)
        {
            var heartbeat = heartbeatOverride ?? _idleResetInterval;

            try
            {
                _logger?.Invoke($"IDLE: entering idle mode on folder {folder.FullName} ({_host}:{_port}), heartbeat={heartbeat}");

                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(doneToken, cancellationToken);
                idleCts.CancelAfter(heartbeat);

                try
                {
                    _logger?.Invoke($"IDLE: calling client.IdleAsync...(listening for Email IMAP server notifications)");
                    await _client.IdleAsync(idleCts.Token, cancellationToken).ConfigureAwait(false);
                    _logger?.Invoke($"IDLE: client.IdleAsync returned(Email IMAP server notifications received, checking for messages)");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    _logger?.Invoke($"IDLE: heartbeat/done triggered after {heartbeat}");
                    _idleFailureCount = 0;
                    return;
                }

                _logger?.Invoke("IDLE: received notification from server, checking for messages");
                _idleFailureCount = 0;
                _idleFallbackWarned = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await HandleIdleFailureAsync(ex, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task HandleIdleFailureAsync(Exception ex, CancellationToken token)
        {
            SetState(ImapRunState.Reconnecting);
            _idleFailureCount++;

            var delay = TimeSpan.FromMilliseconds(Math.Min(
                _idleMaxRetryDelay.TotalMilliseconds,
                Math.Pow(2, Math.Min(_idleFailureCount, 4)) * 1000));

            if (!_idleFallbackWarned)
            {
                _logger?.Invoke($"IDLE: failed on attempt #{_idleFailureCount}: {ex.GetType().Name}");
                _logger?.Invoke($"IDLE: will retry in {delay.TotalSeconds:F0}s (exponential backoff)");
                _idleFallbackWarned = true;
            }

            try
            {
                await _client.NoOpAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception noOpEx)
            {
                _logger?.Invoke($"IDLE: connection lost ({noOpEx.GetType().Name}), will reconnect");
                try
                {
                    if (_client.IsConnected)
                        _client.Disconnect(true);
                }
                catch { }
            }

            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        private async Task WaitWithPollingAsync(CancellationToken token)
        {
            if (!_idleFallbackWarned)
            {
                _logger?.Invoke($"POLL: server does not support IDLE, using polling every {_pollInterval}");
                _idleFallbackWarned = true;
            }

            _idleFailureCount = 0;

            try
            {
                await _client.NoOpAsync(token).ConfigureAwait(false);
            }
            catch { }

            await Task.Delay(_pollInterval, token).ConfigureAwait(false);
        }

        private async Task CheckAndProcessNewMessageAsync(IMailFolder folder, bool isIdle, Func<MimeMessage, Task> onNewMessage, CancellationToken cancellationToken)
        {
            try
            {
                var candidateUids = await GetCandidateUidsAsync(folder, cancellationToken).ConfigureAwait(false);
                if (candidateUids.Count == 0)
                {
                    if (!_lastProcessedUid.HasValue)
                        _logger?.Invoke("No new messages (initialization mode)");
                    return;
                }

                foreach (var uid in candidateUids)
                {
                    _logger?.Invoke($"Processing new message: UID {uid} (lastProcessedUid: {_lastProcessedUid}, via {(isIdle ? "IDLE" : "POLL")})");

                    try
                    {
                        var message = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
                        _logger?.Invoke($"Calling onNewMessage callback for: {message.Subject}");
                        await onNewMessage(message).ConfigureAwait(false);
                        if (!await MarkMessageAsSeenAsync(folder, uid, cancellationToken).ConfigureAwait(false))
                            break;

                        _lastProcessedUid = uid;
                        _logger?.Invoke($"Updated lastProcessedUid: {_lastProcessedUid}");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"Error processing UID {uid}: {ex.GetType().Name} - {ex.Message}");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error checking messages: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private async Task<List<UniqueId>> GetCandidateUidsAsync(IMailFolder folder, CancellationToken cancellationToken)
        {
            var uidsList = await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken).ConfigureAwait(false);

            _logger?.Invoke($"Found {uidsList.Count} unread messages");

            return uidsList
                .Where(u => !_lastProcessedUid.HasValue || u.Id > _lastProcessedUid.Value.Id)
                .OrderBy(u => u.Id)
                .Take(_candidateLimit)
                .ToList();
        }

        private async Task<bool> MarkMessageAsSeenAsync(IMailFolder folder, UniqueId uid, CancellationToken cancellationToken)
        {
            try
            {
                await folder.AddFlagsAsync([uid], MessageFlags.Seen, true, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Warning: failed to mark UID {uid} as seen: {ex.Message}");
                return false;
            }
        }

        private static void TryCancel(CancellationTokenSource? cts)
        {
            try
            {
                cts?.Cancel();
            }
            catch { }
        }

        public async Task StopIdleAsync()
        {
            try
            {
                TryCancel(_idleCts);
                if (_idleTask != null)
                    await _idleTask.ConfigureAwait(false);
            }
            catch { }
            finally
            {
                _idleTask = null;
                _idleCts?.Dispose();
                _idleCts = null;
            }
        }

        public void Dispose()
        {
            try { StopIdleAsync().GetAwaiter().GetResult(); } catch { }
            try { if (_client.IsConnected) _client.Disconnect(true); } catch { }
            _client.Dispose();
        }
    }
}
