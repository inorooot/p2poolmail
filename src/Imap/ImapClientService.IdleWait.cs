using MailKit;

namespace p2poolmail
{
    /// <summary>Mail waiting of <see cref="ImapClientService"/>: IMAP IDLE with heartbeat, polling fallback and backoff.</summary>
    public partial class ImapClientService
    {
        private async Task WaitForMailAsync(IMailFolder folder, bool supportsIdle, CancellationToken token, CancellationToken doneToken, string? sessionId = null)
        {
            if (supportsIdle)
            {
                SetState(ImapRunState.Idle);
                await WaitWithIdleAsync(folder, doneToken, token, _idleHeartbeat).ConfigureAwait(false);
                return;
            }

            SetState(ImapRunState.Polling);
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
                    await _client.IdleAsync(idleCts.Token, cancellationToken).ConfigureAwait(false);
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

                // IdleAsync returns normally in BOTH wake cases (MailKit sends the DONE
                // for us): a folder event cancelled doneToken, or the heartbeat timer
                // cancelled idleCts. The doneToken parameter is idleDoneCts from the
                // caller, separate from idleCts - so the wake reason is distinguishable.
                if (idleCts.IsCancellationRequested && !doneToken.IsCancellationRequested)
                {
                    // Standard, provider-friendly IDLE wake: the heartbeat expired, not a
                    // server message. Use a unified NOOP probe before the next round to keep
                    // the connection fresh without issuing a reconnect storm or keeping a
                    // stale IDLE alive for too long.
                    _logger?.Invoke($"IDLE: heartbeat after {heartbeat} - checking for messages");
                    _ = await TryNoOpAsync(cancellationToken, "IDLE heartbeat").ConfigureAwait(false);
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

        private async Task<bool> TryNoOpAsync(CancellationToken token, string context)
        {
            try
            {
                await _client.NoOpAsync(token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetState(ImapRunState.Reconnecting);
                _logger?.Invoke($"{context}: NOOP failed ({ex.GetType().Name}), reconnecting");
                try
                {
                    if (_client.IsConnected)
                        _client.Disconnect(true);
                }
                catch { }
                return false;
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

            _ = await TryNoOpAsync(token, "IDLE recovery").ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        private async Task WaitWithPollingAsync(CancellationToken token)
        {
            if (!_idleFallbackWarned)
            {
                _logger?.Invoke($"POLL: server does not support IDLE, using polling every {_pollInterval}");
                _idleFallbackWarned = true;
            }

            SetState(ImapRunState.Polling);
            _idleFailureCount = 0;

            _ = await TryNoOpAsync(token, "POLL").ConfigureAwait(false);
            await Task.Delay(_pollInterval, token).ConfigureAwait(false);
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
    }
}
