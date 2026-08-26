using MimeKit;

namespace p2poolmail
{
    internal sealed class Program
    {
        private const string SingleInstanceMutexName = "p2poolmail.single-instance";
        private static readonly CancellationTokenSource CancellationTokenSource = new();

        private static async Task<int> Main(string[] args)
        {
            Console.CancelKeyPress += OnCancelRequested;

            // Single-instance guard: createdNew == false means another instance is already running.
            using var singleInstanceMutex = new Mutex(initiallyOwned: false, name: SingleInstanceMutexName, createdNew: out bool createdNew);
            if (!createdNew)
            {
                CommonHelper.WriteWarn("p2poolmail is already running. This instance will exit.");
                return 1;
            }

            try
            {
                return await RunAsync(args);
            }
            finally
            {
                TryReleaseMutex(singleInstanceMutex);
            }
        }

        /// <summary>
        /// Main flow: init settings and mail queue -> start IMAP listener and worker poller -> tail p2pool.log -> cleanup.
        /// </summary>
        private static async Task<int> RunAsync(string[] args)
        {
            Settings.Initialize();
            CommonHelper.WriteLine("Config Initialized successfully");

            EmailQueue.Initialize();

            // IMAP listener for new mail (auxiliary feature). Only started when enabled;
            // a failure must not stop the main flow.
            // Note: imapService must be declared here (not inside the if) because ShutdownAsync uses it below.
            var imapService = Settings.Current.imap_server.enable
                ? await TryStartImapServiceAsync()
                : null;

            // Miner Tracker: report online worker count every 5 seconds (fire-and-forget; the loop handles its own exceptions).
            if (Settings.Current.notify_event.worker_down_up)
                _ = Task.Run(PollWorkersLoop);

            // Keepalive: periodic healthchecks.io heartbeat from [keepalive] config (fire-and-forget; the loop handles its own exceptions).
            if (Settings.Current.keepalive.enable_remote_ping)
                _ = Task.Run(() => NotifyManager.KeepaliveLoopAsync(CancellationTokenSource.Token));

            // Daily stats: scheduled mining summary report from [daily_stats] config (fire-and-forget; the loop handles its own exceptions).
            if (Settings.Current.daily_stats.enable)
                _ = Task.Run(() => NotifyManager.DailyStatsLoopAsync(CancellationTokenSource.Token));

            // Dev helper: `p2poolmail --sendtest` sends one test email and exits.
            if (args.Length > 0 && args[0] == "--sendtest")
            {
                await SendTestEmailAsync();
                await ShutdownAsync(imapService);
                return 0;
            }

            // Tailing p2pool.log is the primary task; block here until cancelled or failed.
           
            var exitCode = await RunTailerAsync();
            await ShutdownAsync(imapService);
            return exitCode;
        }

        /// <summary>Starts the IMAP IDLE listener; returns the service instance, or null on failure.</summary>
        private static async Task<ImapClientService?> TryStartImapServiceAsync()
        {
            var cfg = Settings.Current.imap_server;
            if (!cfg.enable)
                return null;

            var imapService = new ImapClientService(
                cfg.host,
                cfg.port,
                cfg.useSsl,
                cfg.username,
                cfg.password,
                msg => CommonHelper.WriteLine(msg),
                ignoreCertificateErrors: false);

            try
            {
                if (!await imapService.InitializeAsync(OnNewMailAsync, CancellationTokenSource.Token))
                {
                    // InitializeAsync already logged the reason; continue without IMAP.
                    imapService.Dispose();
                    return null;
                }
                return imapService;
            }
            catch (Exception ex)
            {
                CommonHelper.WriteError($"IMAP check failed: {ex}");
                imapService.Dispose();
                return null;
            }
        }

        /// <summary>Callback for new IMAP mail: enqueue a reply email. Must never block, or the IDLE loop stalls.</summary>
        private static Task OnNewMailAsync(MimeMessage message)
        {
            CommonHelper.WriteLine($"IMAP newmail: {message.Subject} from {message.From}");

            if (!string.IsNullOrWhiteSpace(message.Subject))
            {
                var stats = NotifyManager.RequestByEmail();
                EmailQueue.Enqueue($"{EmailIcons.Mail} Reply: your mining status", stats); // fire-and-forget; delivered by the EmailQueue background worker.
            }

            return Task.CompletedTask;
        }

        /// <summary>Worker-count polling loop: reads the data-api connection count with debounce/trend detection.</summary>
        private static async Task PollWorkersLoop()
        {
            
            while (!CancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    NotifyManager.ReportWorkersCount();
                }
                catch (Exception ex)
                {
                    CommonHelper.WriteError($"Reports worker Count error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), CancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>Tails p2pool.log until cancelled or failed; returns the process exit code.</summary>
        private static async Task<int> RunTailerAsync()
        {
            try
            {
                var tailer = new FileTailer(Settings.Current.p2pool_log.file_path);
                 CommonHelper.WriteLine("Monitoring started");
                await tailer.RunAsync(CancellationTokenSource.Token);
                return 0;
            }
            catch (OperationCanceledException) when (CancellationTokenSource.IsCancellationRequested)
            {
                return 0; // graceful exit triggered by Ctrl+C.
            }
            catch (Exception ex)
            {
                CommonHelper.WriteError(ex.ToString());
                return 1;
            }
        }

        /// <summary>Unified shutdown: disconnect IMAP, then abort the mail queue without drain wait.</summary>
        private static async Task ShutdownAsync(ImapClientService? imapService)
        {
            if (imapService is not null)
            {
                try
                {
                    await imapService.DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    CommonHelper.WriteError($"IMAP shutdown: {ex.Message}");
                }

                imapService.Dispose();
            }

            // The main process is ending; do not spend any time draining the queue.
            await EmailQueue.AbortAsync().ConfigureAwait(false);
        }

        private static void TryReleaseMutex(Mutex mutex)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Ignore when the current thread does not own the mutex.
            }
        }

        /// <summary>Dev helper: send an SMTP test email (fully asynchronous, never blocks the caller).</summary>
        private static async Task SendTestEmailAsync()
        {
            const string subject = "SMTP Test";
            var body = $"SMTP test email sent at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";

            CommonHelper.WriteLine("Sending SMTP test email...");

            var result = await EmailQueue.SendAsync(subject, body).ConfigureAwait(false);
            CommonHelper.WriteLine(result == EmailQueue.SendResult.Sent
                ? "SMTP test email sent successfully.  check your inbox or spam folder."
                : $"SMTP test email send status: {result}. check your inbox or spam folder.");
        }

        private static void OnCancelRequested(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true; // Suppress the default termination and shut down cooperatively instead.
            CancellationTokenSource.Cancel();
        }
    }
}