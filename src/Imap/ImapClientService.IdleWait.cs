using MailKit;

namespace p2poolmail
{
    /// <summary>Mail waiting of <see cref="ImapClientService"/>: IMAP IDLE with heartbeat, polling fallback and backoff.</summary>
    public partial class ImapClientService
    {
        private async Task WaitForMailAsync(IMailFolder folder, CancellationToken token, CancellationToken folderEventToken, string? sessionId = null)
        {
            SetState(ImapRunState.Idle);
            await WaitWithIdleAsync(folder, folderEventToken, token, _idleHeartbeat).ConfigureAwait(false);
        }

        private async Task WaitWithIdleAsync(IMailFolder folder, CancellationToken folderEventToken, CancellationToken cancellationToken, TimeSpan? heartbeat = null)
        {
            var idleTimeout = heartbeat ?? _idleHeartbeat;

            try
            {
                _logger?.Invoke($"IDLE: entering mode on {folder.FullName} ({_host}:{_port}), heartbeat={idleTimeout.TotalSeconds:F0}s");

                // Single linked token: fold both cancellation sources (server event + heartbeat)
                // Pass only linkedToken to IdleAsync to avoid token conflict issues
                using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(folderEventToken, cancellationToken);
                linkedToken.CancelAfter(idleTimeout);

                try
                {
                    // Pass only linkedToken, not both linkedToken.Token and cancellationToken
                    // This prevents double-cancellation issues and simplifies cleanup
                    await _client.IdleAsync(linkedToken.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // External shutdown - propagate immediately
                }
                catch (OperationCanceledException)
                {
                    // Determine cancellation cause: heartbeat vs server event
                    await HandleIdleWakeAsync(folderEventToken, idleTimeout, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Returned normally - determine the cause
                if (folderEventToken.IsCancellationRequested)
                {
                    _logger?.Invoke("IDLE: received server notification, checking messages");
                    ResetIdleState();
                }
                else
                {
                    _logger?.Invoke($"IDLE: heartbeat after {idleTimeout.TotalSeconds:F0}s, checking messages");
                    await KeepAliveAsync(cancellationToken).ConfigureAwait(false);
                }
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

        private async Task HandleIdleWakeAsync(CancellationToken folderEventToken, TimeSpan idleTimeout, CancellationToken cancellationToken)
        {
            if (folderEventToken.IsCancellationRequested)
            {
                _logger?.Invoke("IDLE: received server notification, checking messages");
                ResetIdleState();
            }
            else
            {
                _logger?.Invoke($"IDLE: heartbeat after {idleTimeout.TotalSeconds:F0}s, checking messages");
                await KeepAliveAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void ResetIdleState()
        {
            _idleFailureCount = 0;
        }

        private async Task KeepAliveAsync(CancellationToken token)
        {
            try
            {
                // Use explicit timeout to prevent NOOP from hanging indefinitely
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(_noapTimeout);
                
                await _client.NoOpAsync(timeoutCts.Token).ConfigureAwait(false);
                _idleFailureCount = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw; // External cancellation - propagate
            }
            catch (OperationCanceledException)
            {
                // NOOP timeout expired
                SetState(ImapRunState.Reconnecting);
                _logger?.Invoke($"IDLE heartbeat NOOP timeout ({_noapTimeout.TotalSeconds:F0}s), reconnecting");
                await TryDisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetState(ImapRunState.Reconnecting);
                _logger?.Invoke($"IDLE heartbeat NOOP failed ({ex.GetType().Name}), reconnecting");
                await TryDisconnectAsync().ConfigureAwait(false);
            }
        }

        private async Task HandleIdleFailureAsync(Exception ex, CancellationToken token)
        {
            SetState(ImapRunState.Reconnecting);
            _idleFailureCount++;

            _logger?.Invoke($"IDLE: failed on attempt #{_idleFailureCount}: {ex.GetType().Name}; reconnecting immediately");

            await KeepAliveAsync(token).ConfigureAwait(false);
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
                CleanupIdleResources();
            }
        }

        private void CleanupIdleResources()
        {
            _idleTask = null;
            _idleCts?.Dispose();
            _idleCts = null;
        }
    }
}
