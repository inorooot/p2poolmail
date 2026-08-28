using MailKit;
using MimeKit;

namespace p2poolmail
{
    /// <summary>IDLE loop entry of <see cref="ImapClientService"/>: startup retries and the top-level loop.</summary>
    public partial class ImapClientService
    {
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
    }
}
