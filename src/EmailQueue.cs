using System.Diagnostics;
using System.Threading.Channels;
using MailKit.Net.Smtp;
using MimeKit;

namespace p2poolmail;

internal sealed class EmailQueue : IAsyncDisposable
{
    public enum SendResult { Sent, Failed, Dropped, NotInitialized }

    private const int WarnPending = 256;
    private const int MaxPending = 1024;
    private const int MaxAttempts = 8;
    private static EmailQueue? _instance;

    private readonly Channel<MailJob> _channel = Channel.CreateBounded<MailJob>(new BoundedChannelOptions(MaxPending)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _abort = new();
    private readonly EmailSender _sender;
    private readonly Settings.SMTP _smtp;
    private readonly string _to;
    private readonly TimeSpan _sendTimeout;
    private readonly Task _worker;
    private int _pending;
    private int _stopping;

    private readonly record struct MailJob(MimeMessage Message, TaskCompletionSource<bool>? Done);

    private EmailQueue(Settings.SMTP smtp, Settings.Receiver receiver)
    {
        _smtp = smtp;
        _to = string.IsNullOrWhiteSpace(smtp.toAddress) ? receiver.toAddress : smtp.toAddress;
        if (string.IsNullOrWhiteSpace(smtp.host) || smtp.port is <= 0 or > 65535
            || string.IsNullOrWhiteSpace(smtp.username) || string.IsNullOrWhiteSpace(smtp.password)
            || string.IsNullOrWhiteSpace(_to))
            throw new InvalidOperationException("SMTP settings incomplete");

        // Print SMTP key configuration in English so the user knows (do NOT print password)
        var smtpUserDisplay = string.IsNullOrWhiteSpace(smtp.username) ? "<empty>" : smtp.username;
        CommonHelper.WriteLine($"SMTP: host={smtp.host}, port={smtp.port}, useSsl={smtp.useSsl}, username={smtpUserDisplay}, from={smtp.username}, to={_to}");

        _sender = new EmailSender(smtp);
        _sendTimeout = TimeSpan.FromSeconds(20);
        // Single background consumer. LongRunning adds nothing for async code
        // (the dedicated thread would only serve up to the first await point).
        _worker = Task.Run(WorkerAsync);
        CommonHelper.WriteLine("EmailQueue started");
    }

    public static void Initialize()
    {
        EmailQueue created;
        try
        {
            created = new EmailQueue(Settings.Current.smtp, Settings.Current.receiver);
        }
        catch (InvalidOperationException ex)
        {
            CommonHelper.WriteError($"EmailQueue failed to start - check the [smtp] settings in Config.toml: {ex.Message}");
            throw;
        }
        if (Interlocked.CompareExchange(ref _instance, created, null) is not null)
            created.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public static void Enqueue(string subject, string body, string? correlationId = null, bool isHtml = false)
    {
        var q = Volatile.Read(ref _instance);
        if (q is null || q._stopping != 0)
        {
            CommonHelper.WriteWarn($"EmailQueue unavailable, skipped: {subject}");
            return;
        }
        q.Write(new MailJob(q.Create(subject, body, correlationId, isHtml), null));
    }

    /// <summary>
    /// Asynchronous variant of Enqueue: awaits delivery without blocking any thread.
    /// </summary>
    public static async Task<SendResult> SendAsync(string subject, string body, string? correlationId = null, bool isHtml = false)
    {
        var q = Volatile.Read(ref _instance);
        if (q is null || q._stopping != 0)
        {
            CommonHelper.WriteWarn($"EmailQueue not initialized, cannot send: {subject}");
            return SendResult.NotInitialized;
        }

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        q.Write(new MailJob(q.Create(subject, body, correlationId, isHtml), done));
        try
        {
            return await done.Task.ConfigureAwait(false) ? SendResult.Sent : SendResult.Failed;
        }
        catch (OperationCanceledException)
        {
            return SendResult.Dropped;
        }
        catch (Exception ex)
        {
            CommonHelper.WriteError($"EmailQueue.Send failed: {ex.Message}");
            return SendResult.Failed;
        }
    }

    public static async Task ShutdownAsync(TimeSpan drainTimeout)
    {
        var q = Interlocked.Exchange(ref _instance, null);
        if (q is not null)
            await q.DisposeAsync(drainTimeout).ConfigureAwait(false);
    }

    private void Write(MailJob job)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            Reject(job, "EmailQueue stopping");
            return;
        }

        var n = Interlocked.Increment(ref _pending);
        if (n > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            Reject(job, $"EmailQueue full ({MaxPending})");
            return;
        }

        if (n == WarnPending || (n > WarnPending && n % WarnPending == 0))
            CommonHelper.WriteWarn($"EmailQueue backlog={n}");

        if (_channel.Writer.TryWrite(job))
        {
            // Print every accepted enqueue action so the mail pipeline is fully traceable.
            CommonHelper.WriteLine($"Email queued (pending={n}): \"{job.Message.Subject}\"");
            return;
        }

        Interlocked.Decrement(ref _pending);
        Reject(job, "EmailQueue rejected");
    }

    private static void Reject(MailJob job, string reason)
    {
        CommonHelper.WriteWarn($"{reason}: {job.Message.Subject}");
        job.Done?.TrySetResult(false);
    }

    private MimeMessage Create(string subject, string body, string? correlationId, bool isHtml)
    {
        var message = new MimeMessage
        {
            Subject = subject ?? string.Empty,
            Body = new TextPart(isHtml ? "html" : "plain") { Text = body ?? string.Empty }
        };
        message.From.Add(new MailboxAddress(_smtp.fromName ?? string.Empty, _smtp.username));
        message.To.Add(new MailboxAddress(_smtp.toName ?? string.Empty, _to));
        if (!string.IsNullOrWhiteSpace(correlationId))
            message.Headers["X-Correlation-Id"] = correlationId;
        return message;
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
            {
                try
                {
                    var sent = await DeliverAsync(job.Message).ConfigureAwait(false);
                    job.Done?.TrySetResult(sent);
                }
                catch (OperationCanceledException)
                {
                    job.Done?.TrySetCanceled(_abort.Token);
                    break;
                }
                catch (Exception ex)
                {
                    CommonHelper.WriteError(ex);
                    job.Done?.TrySetResult(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CommonHelper.WriteError(ex);
        }
        finally
        {
            while (_channel.Reader.TryRead(out var leftover))
            {
                // Jobs never delivered because of shutdown are reported as canceled (Dropped),
                // not Failed, so callers can tell them apart from real SMTP failures.
                leftover.Done?.TrySetCanceled(_abort.Token);
                Interlocked.Decrement(ref _pending);
            }
            CommonHelper.WriteLine("EmailQueue worker stopped");
        }
    }

    private async Task<bool> DeliverAsync(MimeMessage message)
    {
        var delay = 1000;
        var started = Stopwatch.StartNew();
        for (var n = 1; n <= MaxAttempts; n++)
        {
            _abort.Token.ThrowIfCancellationRequested();
            try
            {
                await _sender.SendAsync(message, _sendTimeout, _abort.Token).ConfigureAwait(false);
                started.Stop();
                // Always print an OK-level status line after a successful send,
                // including recipient, elapsed time and retry count.
                var to = message.To.Mailboxes.FirstOrDefault()?.Address ?? message.To.ToString();
                CommonHelper.WriteSuccess(n > 1
                    ? $"Email sent OK after {n} attempts ({started.ElapsedMilliseconds} ms): \"{message.Subject}\" -> {to}"
                    : $"Email sent OK ({started.ElapsedMilliseconds} ms): \"{message.Subject}\" -> {to}");
                return true;
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                throw;
            }
            catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500)
            {
                CommonHelper.WriteError($"Email discarded (SMTP {(int)ex.StatusCode}): {message.Subject}: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                if (n == MaxAttempts)
                {
                    CommonHelper.WriteError($"Email dropped after {n} attempts: {message.Subject}: {ex.Message}");
                    return false;
                }

                CommonHelper.WriteWarn($"Email send failed (attempt {n}/{MaxAttempts}), will retry in {delay} ms: \"{message.Subject}\": {ex.Message}");
                await Task.Delay(delay, _abort.Token).ConfigureAwait(false);
                // Cap backoff so a short SMTP hiccup can still recover within
                // a few attempts instead of stalling the worker indefinitely.
                delay = Math.Min(delay * 2, 8_000);
            }
        }

        return false;
    }

    private async ValueTask DisposeAsync(TimeSpan drainTimeout)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        _channel.Writer.TryComplete();

        if (drainTimeout <= TimeSpan.Zero)
        {
            // No-drain shutdown (main process is exiting): cancel in-flight sends
            // immediately instead of spending any drain time waiting for them.
            try { await _abort.CancelAsync().ConfigureAwait(false); } catch { }
        }
        else
        {
            using var delayCts = new CancellationTokenSource();
            if (await Task.WhenAny(_worker, Task.Delay(drainTimeout, delayCts.Token)).ConfigureAwait(false) != _worker)
            {
                CommonHelper.WriteWarn($"EmailQueue drain timeout, aborting ({Volatile.Read(ref _pending)} pending)");
                try { await _abort.CancelAsync().ConfigureAwait(false); } catch { }
            }
            else
            {
                delayCts.Cancel();
            }
        }

        await _worker.ConfigureAwait(false);
        await _sender.DisposeAsync().ConfigureAwait(false);
        _abort.Dispose();
    }

    public ValueTask DisposeAsync() => DisposeAsync(TimeSpan.FromSeconds(45));

    /// <summary>
    /// Stops the queue immediately when the main process is exiting:
    /// pending and in-flight emails are aborted without any drain wait.
    /// </summary>
    public static Task AbortAsync()
    {
        var q = Interlocked.Exchange(ref _instance, null);
        return q is null ? Task.CompletedTask : q.DisposeAsync(TimeSpan.Zero).AsTask();
    }
}
