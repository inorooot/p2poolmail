using System.Net;
using System.Net.Sockets;
using System.Text;
using p2poolmail;

namespace Tests;

/// <summary>
/// Live-loop smoke tests for <see cref="ImapClientService"/> against an in-process
/// fake IMAP server. These verify the real connection/IDLE/disconnect paths that
/// pure unit tests cannot cover: connecting through the self-dialed TCP socket
/// (with kernel keepalives), entering IDLE, and - most importantly - that a dropped
/// connection actually surfaces in the log output and triggers a reconnect.
/// </summary>
public class ImapClientServiceLiveLoopTests
{
    /// <summary>
    /// Minimal IMAP server: greets, answers the commands MailKit issues during
    /// connect/auth/IDLE and pushes one EXISTS per IDLE round. Accepts connections
    /// in a loop so reconnects succeed. Reports each accepted TcpClient through
    /// <paramref name="onAccepted"/> so a test can hard-kill it (RST).
    /// </summary>
    private static async Task RunFakeServerAsync(TcpListener listener, CancellationToken ct, List<string> log, Action<TcpClient>? onAccepted = null)
    {
        var sessionIndex = 0;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { return; }

            onAccepted?.Invoke(client);
            sessionIndex++;

            try
            {
                using (client)
                await using (var raw = client.GetStream())
                {
                    var reader = new StreamReader(raw, Encoding.ASCII);
                    await using var writer = new StreamWriter(raw, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

                    await writer.WriteLineAsync("* OK fake IMAP4rev1 ready").ConfigureAwait(false);
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line == null)
                            break;
                        lock (log) log.Add(line);

                        var parts = line.Split(' ', 3);
                        var tag = parts[0];
                        var cmd = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";

                        switch (cmd)
                        {
                            case "CAPABILITY":
                                await writer.WriteLineAsync("* CAPABILITY IMAP4rev1 IDLE").ConfigureAwait(false);
                                await writer.WriteLineAsync($"{tag} OK CAPABILITY done").ConfigureAwait(false);
                                break;
                            case "LOGIN":
                            case "AUTHENTICATE":
                                await writer.WriteLineAsync($"{tag} OK logged in").ConfigureAwait(false);
                                break;
                            case "ID":
                                await writer.WriteLineAsync("* ID (\"name\" \"fake\")").ConfigureAwait(false);
                                await writer.WriteLineAsync($"{tag} OK ID done").ConfigureAwait(false);
                                break;
                            case "SELECT":
                                await writer.WriteLineAsync("* 2 EXISTS").ConfigureAwait(false);
                                await writer.WriteLineAsync("* OK [UIDVALIDITY 1] UIDs valid").ConfigureAwait(false);
                                await writer.WriteLineAsync("* OK [UIDNEXT 3] Predicted next UID").ConfigureAwait(false);
                                await writer.WriteLineAsync($"{tag} OK [READ-WRITE] SELECT done").ConfigureAwait(false);
                                break;
                            case "IDLE":
                                await writer.WriteLineAsync("+ idling").ConfigureAwait(false);
                                await Task.Delay(200, ct).ConfigureAwait(false);
                                // Model a reconnect after an outage: flag changes that other
                                // sessions made while the client was offline are queued on
                                // the server and flushed as soon as the new IDLE begins.
                                // Sessions 1 (initial connect) get no such push.
                                if (sessionIndex >= 2)
                                    await writer.WriteLineAsync("* 1 FETCH (FLAGS (\\Seen))").ConfigureAwait(false);
                                await writer.WriteLineAsync("* 3 EXISTS").ConfigureAwait(false);
                                var done = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                                if (done == null)
                                    break;
                                lock (log) log.Add(done);
                                await writer.WriteLineAsync($"{tag} OK IDLE terminated").ConfigureAwait(false);
                                break;
                            case "SEARCH":
                                await writer.WriteLineAsync("* SEARCH").ConfigureAwait(false);
                                await writer.WriteLineAsync($"{tag} OK SEARCH done").ConfigureAwait(false);
                                break;
                            case "NOOP":
                                await writer.WriteLineAsync($"{tag} OK NOOP done").ConfigureAwait(false);
                                break;
                            case "LOGOUT":
                                await writer.WriteLineAsync("* BYE").ConfigureAwait(false);
                                await writer.WriteLineAsync($"{tag} OK LOGOUT done").ConfigureAwait(false);
                                return;
                            default:
                                await writer.WriteLineAsync($"{tag} OK").ConfigureAwait(false);
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch { /* connection died or was killed - keep accepting */ }
        }
    }

    private static async Task<(ImapClientService Service, List<string> Logs, CancellationTokenSource Cts, List<TcpClient> Accepted)>
        StartServiceAsync(TcpListener listener, CancellationTokenSource cts, List<string> serverLog, Action<TcpClient>? onAccepted)
    {
        var accepted = new List<TcpClient>();
        var serverTask = Task.Run(() => RunFakeServerAsync(listener, cts.Token, serverLog, c => { lock (accepted) accepted.Add(c); onAccepted?.Invoke(c); }));

        var logs = new List<string>();
        var service = new ImapClientService("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            useSsl: false, username: "user", password: "pass",
            logger: msg => { lock (logs) logs.Add(msg); });

        var started = await service.InitializeAsync(_ => Task.CompletedTask, cts.Token).ConfigureAwait(false);
        Assert.True(started, "InitializeAsync should succeed against the fake server");

        // Wait until the IDLE wait is actually active before the test manipulates the socket.
        try
        {
            await WaitUntilAsync(() => Contains(logs, "IDLE: entering idle mode"), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch
        {
            string captured;
            lock (logs) captured = string.Join(Environment.NewLine, logs);
            throw new Exception($"IDLE never started. Captured service logs:{Environment.NewLine}{captured}");
        }
        return (service, logs, cts, accepted);
    }

    private static bool Contains(List<string> logs, string fragment)
    {
        lock (logs) return logs.Any(l => l.Contains(fragment, StringComparison.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50).ConfigureAwait(false);
        }
        Assert.True(condition(), $"condition not met within {timeout.TotalSeconds:F0}s");
    }

    [Fact]
    public async Task ConnectsViaSelfDialedSocket_EntersIdle_AndReceivesPush()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverLog = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var (service, logs, _, accepted) = await StartServiceAsync(listener, cts, serverLog, null).ConfigureAwait(false);
            lock (accepted) Assert.Single(accepted);

            // The connection handshake went through the Stream overload of ConnectAsync.
            Assert.True(Contains(logs, "Connected to 127.0.0.1:"), "expected a 'Connected' log line");
            // A second IDLE round proves the loop survived the pushed EXISTS and re-entered.
            await WaitUntilAsync(() => CountOf(logs, "IDLE: entering idle mode") >= 2, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            service.Dispose();
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task HardSocketReset_SurfacesInLogs_AndReconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverLog = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var (service, logs, _, accepted) = await StartServiceAsync(listener, cts, serverLog, null).ConfigureAwait(false);

            // Kill the live connection with an RST (SO_LINGER 0): the client's blocked
            // IDLE read fails immediately instead of waiting for the 10-min heartbeat.
            TcpClient live;
            lock (accepted) live = accepted[^1];
            live.LingerState = new LingerOption(true, 0);
            live.Close();

            // The drop must be visible in the log within seconds (not silently swallowed).
            await WaitUntilAsync(
                () => Contains(logs, "IDLE: failed on attempt")
                   || Contains(logs, "Idle loop error")
                   || Contains(logs, "NOOP failed"),
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            // The outage must be announced to the user exactly once per outage...
            await WaitUntilAsync(
                () => CountOf(logs, "LOST - network problem") >= 1,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            lock (logs) Assert.Equal(1, logs.Count(l => l.Contains("LOST - network problem", StringComparison.Ordinal)));

            // And the loop must recover by reconnecting to the still-running fake server.
            await WaitUntilAsync(
                () => CountOf(logs, "Connected to 127.0.0.1:") >= 2,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            // ...with an explicit recovery notice including the outage duration.
            await WaitUntilAsync(
                () => CountOf(logs, "RESTORED - network recovered after") >= 1,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            lock (logs) Assert.Equal(1, logs.Count(l => l.Contains("RESTORED - network recovered after", StringComparison.Ordinal)));

            service.Dispose();
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    [Fact]
    public void EnableTcpKeepAlive_EnablesKeepAliveOnSocket()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var method = typeof(ImapClientService).GetMethod("EnableTcpKeepAlive",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        method!.Invoke(null, [socket]);

        var keepAlive = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive);
        Assert.True(keepAlive is bool b ? b : Convert.ToInt32(keepAlive) != 0,
            $"expected KeepAlive enabled, got {keepAlive}");
        // On platforms that support the TCP-level knobs, verify the values took effect.
        try
        {
            Assert.Equal(15, socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime));
            Assert.Equal(5, socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval));
            Assert.Equal(3, socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount));
        }
        catch (SocketException)
        {
            // Platform without the TCP knobs: the plain KeepAlive flag is enough.
        }
    }

    [Fact]
    public async Task FlagsChangedPush_AfterReconnect_WakesLoopWithoutReprocessing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverLog = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var (service, logs, _, accepted) = await StartServiceAsync(listener, cts, serverLog, null).ConfigureAwait(false);
            lock (accepted) Assert.Single(accepted);

            // Kill the connection with an RST; the fake server's session counter moves to
            // 2, so the reconnected session gets a queued "* 1 FETCH (FLAGS (\Seen))"
            // pushed as soon as IDLE begins - modeling changes other sessions made
            // while the client was offline.
            TcpClient live;
            lock (accepted) live = accepted[^1];
            live.LingerState = new LingerOption(true, 0);
            live.Close();

            await WaitUntilAsync(() => CountOf(logs, "Connected to 127.0.0.1:") >= 2, TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            // The flag push after reconnect must surface as a MessageFlagsChanged event...
            await WaitUntilAsync(
                () => CountOf(logs, "Folder.MessageFlagsChanged event:") >= 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            // ...the loop re-enters IDLE after the wake (no crash, no reconnect churn)...
            await WaitUntilAsync(
                () => CountOf(logs, "IDLE: entering idle mode") >= 3,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            // ...and the flags-only wake must NOT reprocess old mail (UID watermark intact).
            lock (logs) Assert.DoesNotContain(logs, l => l.Contains("Processing new message:", StringComparison.Ordinal));

            service.Dispose();
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    [Theory]
    [InlineData(0, 5, "5s")]
    [InlineData(2, 10, "2m 10s")]
    public void FormatOutageDuration_FormatsDurations(int minutes, int seconds, string expected)
    {
        var method = typeof(ImapClientService).GetMethod("FormatOutageDuration",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds)]);
        Assert.Equal(expected, result);
    }

    private static int CountOf(List<string> logs, string fragment)
    {
        lock (logs) return logs.Count(l => l.Contains(fragment, StringComparison.Ordinal));
    }
}
