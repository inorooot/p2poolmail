// Copyright (c) 2026 inorooot. MIT License.

using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace p2poolmail;

internal sealed class EmailSender : IAsyncDisposable
{
    // Provider SMTP idle timeouts are typically ~5 minutes, but the NoOp probe
    // (below) safely detects dead connections, so idle clients can be kept longer.
    // 10 minutes lets sparse traffic (minutes between emails) mostly reuse the
    // connection via NoOp instead of reconnecting + re-authenticating every send,
    // which is friendlier to providers that rate-limit connection/auth attempts.
    private static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NoOpAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinSendInterval = TimeSpan.FromSeconds(1); // default rate limit 1s

    private readonly Settings.SMTP _smtp;
    private readonly string _user;
    private readonly bool _auth;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SmtpClient? _client;
    // _lastOk/_lastAttemptTick are only read/written by the single EmailQueue worker
    // thread while holding _gate (DisposeAsync never touches them), so plain access
    // is sufficient - no volatile/interlocked needed.
    private long _lastOk;
    private long _lastAttemptTick;
    private int _disposed;

    public EmailSender(Settings.SMTP smtp)
    {
        _smtp = smtp;
        _user = smtp.username ?? string.Empty;
        _auth = !string.IsNullOrWhiteSpace(_user) && !string.IsNullOrWhiteSpace(smtp.password);
        // Seed with current time so a process starting within the first second after
        // boot does not trigger a spurious rate-limit wait (TickCount64 would be ~0).
        _lastAttemptTick = Environment.TickCount64;
    }

    public async Task SendAsync(MimeMessage message, TimeSpan timeout, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // Print the send action itself so every SMTP send is visible in the log.
            CommonHelper.WriteLine($"SMTP: sending \"{message.Subject}\" (timeout={timeout.TotalSeconds:F0}s)");

            for (var pass = 0; pass < 2; pass++)
            {
                await EnforceRateLimitAsync(cancellation).ConfigureAwait(false);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                cts.CancelAfter(timeout);
                try
                {
                    var client = await ConnectAsync(cts.Token).ConfigureAwait(false);
                    // Connect/auth above ran under the ctor's 30s Timeout plus cts;
                    // this Timeout governs the SMTP session IO of the send itself.
                    client.Timeout = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
                    await client.SendAsync(message, cts.Token).ConfigureAwait(false);
                    _lastAttemptTick = Environment.TickCount64;
                    _lastOk = Environment.TickCount64;
                    return;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    _lastAttemptTick = Environment.TickCount64;
                    CommonHelper.WriteWarn("SMTP: send canceled, disconnecting");
                    await DisconnectAsync().ConfigureAwait(false);
                    throw;
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // Send timed out; the connection state is unknown, so discard it.
                    // Propagate instead of retrying in place: EmailQueue applies its
                    // own attempt cap and exponential backoff, and an immediate retry
                    // here would double the worst-case latency per send.
                    _lastAttemptTick = Environment.TickCount64;
                    CommonHelper.WriteWarn($"SMTP: send/connect timed out after {timeout.TotalSeconds:F0}s, disconnecting");
                    await DisconnectAsync().ConfigureAwait(false);
                    throw;
                }
                catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500)
                {
                    _lastAttemptTick = Environment.TickCount64;
                    CommonHelper.WriteError($"SMTP: permanent error {(int)ex.StatusCode}, aborting send: {ex.Message}");
                    await DisconnectAsync().ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    _lastAttemptTick = Environment.TickCount64;
                    CommonHelper.WriteWarn(pass == 0
                        ? $"SMTP: send attempt failed: {ex.Message} - reconnecting and retrying"
                        : $"SMTP: send attempt failed again: {ex.Message}");
                    await DisconnectAsync().ConfigureAwait(false);
                    if (pass == 1)
                        throw;
                }
            }

            // Unreachable: the second pass always rethrows from the catch above.
            // Kept defensive so a future edit can never fall through and report
            // success without actually having sent anything.
            throw new InvalidOperationException("SMTP send failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnforceRateLimitAsync(CancellationToken cancellation)
    {
        if (MinSendInterval <= TimeSpan.Zero)
            return;

        var elapsed = Environment.TickCount64 - _lastAttemptTick;
        if (elapsed < 0)
            elapsed = 0;
        if (elapsed >= MinSendInterval.TotalMilliseconds)
            return;

        var delay = MinSendInterval - TimeSpan.FromMilliseconds(elapsed);
        CommonHelper.WriteDebug($"SMTP: rate limiting, waiting {delay.TotalMilliseconds:F0}ms");
        await Task.Delay(delay, cancellation).ConfigureAwait(false);
    }

    private async Task<SmtpClient> ConnectAsync(CancellationToken cancellation)
    {
        if (await TryReuseAsync(cancellation).ConfigureAwait(false) is { } reused)
        {
            CommonHelper.WriteDebug("SMTP: reusing existing connection");
            return reused;
        }

        await DisconnectAsync().ConfigureAwait(false);

        var client = new SmtpClient { Timeout = 30_000 };
        try
        {
            CommonHelper.WriteDebug($"SMTP: connecting to {_smtp.host}:{_smtp.port} (useSsl={_smtp.useSsl})");
            await client.ConnectAsync(_smtp.host, _smtp.port, SocketOptions(), cancellation).ConfigureAwait(false);
            if (_auth)
            {
                await client.AuthenticateAsync(_user, _smtp.password, cancellation).ConfigureAwait(false);
                CommonHelper.WriteLine($"SMTP: authenticated as {_user}");
            }
            else
            {
                CommonHelper.WriteDebug("SMTP: connected (no authentication)");
            }
            _client = client;
            _lastOk = Environment.TickCount64;
            CommonHelper.WriteDebug("SMTP: connection established");
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            CommonHelper.WriteWarn($"SMTP: connect failed: {ex.Message}");
            throw;
        }
    }

    private async Task<SmtpClient?> TryReuseAsync(CancellationToken cancellation)
    {
        var client = _client;
        if (client is not { IsConnected: true })
            return null;
        if (_auth && !client.IsAuthenticated)
            return null;
        if (Environment.TickCount64 - _lastOk >= MaxIdle.TotalMilliseconds)
            return null;

        var idleMs = Environment.TickCount64 - _lastOk;
        if (idleMs < NoOpAfter.TotalMilliseconds)
        {
            CommonHelper.WriteDebug("SMTP: reusing client without NoOp");
            return client;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.NoOpAsync(cts.Token).ConfigureAwait(false);
            _lastOk = Environment.TickCount64;
            CommonHelper.WriteDebug($"SMTP: NoOp succeeded after {idleMs / 1000.0:F0}s idle, reusing client");
            return client;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Aborting: rethrow instead of swallowing the cancellation and attempting
            // a doomed reconnect (which would only add a wasted disconnect + connect).
            throw;
        }
        catch (Exception ex)
        {
            CommonHelper.WriteWarn($"SMTP: NoOp failed after {idleMs / 1000.0:F0}s idle, will reconnect: {ex.Message}");
            return null;
        }
    }

    private SecureSocketOptions SocketOptions()
    {
        if (!_smtp.useSsl)
            return SecureSocketOptions.None;
        return SecureSocketOptions.SslOnConnect;
    }

    private async Task DisconnectAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null)
            return;

        try
        {
            CommonHelper.WriteDebug("SMTP: disconnecting client");
            if (client.IsConnected)
            {
                using var cts = new CancellationTokenSource(DisconnectTimeout);
                await client.DisconnectAsync(quit: true, cts.Token).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            client.Dispose();
            CommonHelper.WriteDebug("SMTP: client disposed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            // Bound the wait: an in-flight send (up to 2 passes) can hold the gate.
            // On timeout, skip the disconnect - the in-flight send cleans up its own
            // connection on failure, and _disposed=1 already blocks all future sends.
            await _gate.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CommonHelper.WriteWarn("SMTP: dispose timed out waiting for an in-flight send; skipping disconnect");
            return;
        }
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
