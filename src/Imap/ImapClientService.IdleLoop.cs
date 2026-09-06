using MimeKit;

namespace p2poolmail
{
    /// <summary>IDLE loop entry of <see cref="ImapClientService"/>: startup retries and the top-level loop.</summary>
    public partial class ImapClientService
    {
        /// <summary>
        /// Connects (with infinite retries for 24/7 operation) and starts the IDLE loop.
        /// Returns true on success; on cancellation it propagates the exception.
        /// </summary>
        public async Task<bool> InitializeAsync(Func<MimeMessage, Task> onNewMessage, CancellationToken cancellationToken = default)
        {
            if (onNewMessage == null)
                throw new ArgumentNullException(nameof(onNewMessage));

            var attempts = 0;
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
                    // A fast-failing connect (e.g. connection refused) surfaces as a
                    // plain socket exception, never as OperationCanceledException, so
                    // without this check the retry loop would spin forever even after
                    // cancellation (observed blocking shutdown). Honor it explicitly.
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    attempts++;
                    _logger?.Invoke($"IMAP connect failed: {ex.Message} - retrying (attempt #{attempts})");

                    // Retry once per second (previously immediate retry, which spun at
                    // ~5000 attempts/s and flooded the log). Task.Delay also exits the
                    // loop promptly when cancellation is requested mid-wait.
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }

            _ = StartIdleAsync(onNewMessage, cancellationToken);
            return true;
        }

        public Task StartIdleAsync(Func<MimeMessage, Task> onNewMessage, CancellationToken cancellationToken = default)
        {
            if (onNewMessage == null)
                throw new ArgumentNullException(nameof(onNewMessage));

            lock (_idleLifecycleLock)
            {
                if (_idleTask != null && !_idleTask.IsCompleted)
                    return _idleTask;

                _idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = _idleCts.Token;

                // The IDLE loop owns the connection lifecycle while running.
                // The SyncRoot gate ensures only this loop touches the ImapClient.
                _idleTask = Task.Run(() => RunIdleLoopAsync(onNewMessage, token), token);

                return _idleTask;
            }
        }

        /// <summary>
        /// The core IDLE loop. Acquires the SyncRoot gate to exclusively own the ImapClient,
        /// then iterates: connect → idle → process → repeat. On failure, disconnects and
        /// reconnects immediately for fast recovery.
        /// </summary>
        private async Task RunIdleLoopAsync(Func<MimeMessage, Task> onNewMessage, CancellationToken token)
        {
            // Acquire the SyncRoot gate: this loop now exclusively owns the ImapClient.
            if (!await AcquireLockAsync(token).ConfigureAwait(false))
            {
                SetState(ImapRunState.Stopped);
                return;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Ensure connection is alive
                        if (!_client.IsConnected)
                        {
                            SetState(ImapRunState.Connecting);
                            await ConnectAndAuthenticateAsync(token).ConfigureAwait(false);
                            SetState(ImapRunState.Idle);
                            LogIdleSupport("Connected");
                        }

                        // Run one IDLE iteration
                        await IdleLoopIterationAsync(onNewMessage, token).ConfigureAwait(false);
                        _idleFailureCount = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        SetState(ImapRunState.Stopped);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _idleFailureCount++;
                        SetState(ImapRunState.Reconnecting);
                        _logger?.Invoke($"IDLE loop error (attempt #{_idleFailureCount}): {ex.GetType().Name}: {ex.Message} - reconnecting");

                        DisconnectQuiet();
                    }
                }
            }
            finally
            {
                // Release the SyncRoot gate
                _syncRoot.Release();
            }
        }
    }
}
