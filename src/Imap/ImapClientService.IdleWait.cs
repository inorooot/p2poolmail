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
                    _logger?.Invoke($"IDLE: heartbeat after {heartbeat} - checking for messages");
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
