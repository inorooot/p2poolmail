// Copyright (c) 2026 inorooot. MIT License.

using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace p2poolmail;

internal sealed class EmailSender : IAsyncDisposable
{
    private static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NoOpAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(3);

    private readonly Settings.SMTP _smtp;
    private readonly string _user;
    private readonly bool _auth;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SmtpClient? _client;
    private long _lastOk;
    private int _disposed;

    public EmailSender(Settings.SMTP smtp)
    {
        _smtp = smtp;
        _user = string.IsNullOrWhiteSpace(smtp.username) ? smtp.username : smtp.username;
        _auth = !string.IsNullOrWhiteSpace(_user) && !string.IsNullOrWhiteSpace(smtp.password);
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

            Exception? last = null;
            for (var pass = 0; pass < 2; pass++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                cts.CancelAfter(timeout);
                try
                {
                    var client = await ConnectAsync(cts.Token).ConfigureAwait(false);
                    client.Timeout = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
                    await client.SendAsync(message, cts.Token).ConfigureAwait(false);
                    _lastOk = Environment.TickCount64;
                    return;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    CommonHelper.WriteWarn("SMTP: send canceled, disconnecting");
                    await DisconnectAsync().ConfigureAwait(false);
                    throw;
                }
                catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500)
                {
                    CommonHelper.WriteError($"SMTP: permanent error {(int)ex.StatusCode}, aborting send: {ex.Message}");
                    await DisconnectAsync().ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    CommonHelper.WriteWarn(pass == 0
                        ? $"SMTP: send attempt failed: {ex.Message} - reconnecting and retrying"
                        : $"SMTP: send attempt failed again: {ex.Message}");
                    await DisconnectAsync().ConfigureAwait(false);
                    if (pass == 1)
                        throw;
                }
            }

            throw last ?? new InvalidOperationException("SMTP send failed");
        }
        finally
        {
            _gate.Release();
        }
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
            CommonHelper.WriteLine($"SMTP: connecting to {_smtp.host}:{_smtp.port} (useSsl={_smtp.useSsl})");
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

        if (Environment.TickCount64 - _lastOk < NoOpAfter.TotalMilliseconds)
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
            CommonHelper.WriteDebug("SMTP: NoOp succeeded, reusing client");
            return client;
        }
        catch (Exception ex)
        {
            CommonHelper.WriteWarn($"SMTP: NoOp failed, will reconnect: {ex.Message}");
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
            CommonHelper.WriteLine("SMTP: disconnecting client");
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

        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
