using MailKit;

namespace p2poolmail
{
    /// <summary>Mail waiting of <see cref="ImapClientService"/>: IMAP IDLE with heartbeat keep-alive.</summary>
    public partial class ImapClientService
    {
        private async Task WaitForMailAsync(IMailFolder folder, CancellationToken token, CancellationToken folderEventToken)
        {
            SetState(ImapRunState.Idle);
            await WaitWithIdleAsync(folder, folderEventToken, token, _idleHeartbeat).ConfigureAwait(false);
        }

        private async Task WaitWithIdleAsync(IMailFolder folder, CancellationToken folderEventToken, CancellationToken cancellationToken, TimeSpan? heartbeat = null)
        {
            var idleTimeout = heartbeat ?? _idleHeartbeat;

            _logger?.Invoke($"IDLE: entering mode on {folder.FullName} ({_host}:{_port}), heartbeat={idleTimeout.TotalSeconds:F0}s");

            // One linked token covers both wake-up sources: server event and heartbeat.
            using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(folderEventToken, cancellationToken);
            linkedToken.CancelAfter(idleTimeout);

            try
            {
                await _client.IdleAsync(linkedToken.Token).ConfigureAwait(false);
                await OnIdleWakeAsync(folderEventToken, cancellationToken, idleTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // External shutdown - propagate immediately
            }
            catch (OperationCanceledException)
            {
                // Canceled by heartbeat or server event, not by shutdown.
                await OnIdleWakeAsync(folderEventToken, cancellationToken, idleTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"IDLE error: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        private async Task OnIdleWakeAsync(CancellationToken folderEventToken, CancellationToken cancellationToken, TimeSpan idleTimeout)
        {
            if (folderEventToken.IsCancellationRequested)
            {
                _logger?.Invoke("IDLE: received server notification, checking messages");
                _idleFailureCount = 0;
                return;
            }

            _logger?.Invoke($"IDLE: heartbeat after {idleTimeout.TotalSeconds:F0}s, checking messages");
            await KeepAliveAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends a NOOP keep-alive to verify the connection is still alive.
        /// Called during heartbeat cycles.
        /// </summary>
        private async Task KeepAliveAsync(CancellationToken token)
        {
            try
            {
                // Use explicit timeout to prevent NOOP from hanging indefinitely
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(NoopTimeout);

                await _client.NoOpAsync(timeoutCts.Token).ConfigureAwait(false);
                _idleFailureCount = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw; // External cancellation - propagate
            }
            catch (OperationCanceledException)
            {
                // NOOP timeout expired - connection is dead
                SetState(ImapRunState.Reconnecting);
                _logger?.Invoke($"IDLE heartbeat NOOP timeout ({NoopTimeout.TotalSeconds:F0}s), reconnecting");
                throw new InvalidOperationException("NOOP timeout - connection lost");
            }
            catch (Exception ex)
            {
                SetState(ImapRunState.Reconnecting);
                _logger?.Invoke($"IDLE heartbeat NOOP failed ({ex.GetType().Name}), reconnecting");
                throw new InvalidOperationException($"NOOP failed: {ex.Message}", ex);
            }
        }

        public async Task StopIdleAsync()
        {
            Task? idleTask;
            lock (_idleLifecycleLock)
            {
                TryCancel(_idleCts);
                idleTask = _idleTask;
            }

            if (idleTask != null)
            {
                try { await idleTask.ConfigureAwait(false); }
                catch { /* the loop logs its own errors */ }
            }

            lock (_idleLifecycleLock)
            {
                // Only clean up if no new loop was started in the meantime.
                if (_idleTask == idleTask)
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
