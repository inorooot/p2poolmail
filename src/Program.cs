using System.Diagnostics;
using MimeKit;

namespace p2poolmail
{
    internal sealed class Program
    {
        private static readonly CancellationTokenSource _shutdownCts = new();

        /// <summary>Subject that triggers a status reply (case-insensitive).</summary>
        private const string TriggerSubject = "hello";

        private static async Task<int> Main(string[] args)
        {
            Console.CancelKeyPress += OnCancelRequested;
            SuppressCtrlCEcho();

            CommonHelper.WriteLine($"p2poolmail v{AppVersion.Value} starting...");

            // Single-instance guard: named Mutex does NOT work under NativeAOT on Linux
            // (every process observed createdNew == true and several instances ran side
            // by side). Use an exclusive global lock file instead - FileShare.None is
            // enforced by the OS, and the handle is released automatically on any kind
            // of process exit, so a leftover file never blocks the next start.
            FileStream instanceLock;
            try
            {
                instanceLock = AcquireInstanceLock();
            }
            catch (IOException)
            {
                CommonHelper.WriteWarn("p2poolmail is already running. This instance will exit.");
                return 1;
            }

            try
            {
                return await RunAsync(args);
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                CommonHelper.WriteLine("Shutdown complete.");
                return 0;
            }
            catch (Exception ex)
            {
                // Startup errors (bad config, SMTP settings incomplete) land here
                // with a readable message instead of an unhandled stack trace.
                CommonHelper.WriteError(ex.ToString());
                return 1;
            }
            finally
            {
                RestoreCtrlCEcho();
                instanceLock.Dispose();
            }
        }

        /// <summary>
        /// Opens the global lock file <c>/tmp/p2poolmail.single-instance.lock</c> with
        /// FileShare.None. Throws IOException when another running instance holds it.
        /// </summary>
        private static FileStream AcquireInstanceLock()
        {
            var path = Path.Combine(Path.GetTempPath(), "p2poolmail.single-instance.lock");
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                stream.SetLength(0);
                var info = System.Text.Encoding.ASCII.GetBytes(
                    $"pid={Environment.ProcessId} started={DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n");
                stream.Write(info, 0, info.Length);
                stream.Flush();
            }
            catch
            {
                // Diagnostics only - a failure to write the pid must not break the lock.
            }

            return stream;
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
            {
                _ = Task.Run(PollWorkersLoop);
                CommonHelper.WriteLine("Worker count poller started (every 5s)");
            }
            else
            {
                CommonHelper.WriteLine("Worker count poller disabled ([notify_event].worker_down_up = false)");
            }

            // Keepalive: periodic healthchecks.io heartbeat from [keepalive] config (fire-and-forget; the loop handles its own exceptions).
            if (Settings.Current.keepalive.enable_remote_ping)
            {
                _ = Task.Run(() => NotifyManager.KeepaliveLoopAsync(_shutdownCts.Token));
                CommonHelper.WriteLine("Keepalive heartbeat enabled");
            }
            else
            {
                CommonHelper.WriteLine("Keepalive heartbeat disabled ([keepalive].enable_remote_ping = false)");
            }

            // Daily stats: scheduled mining summary report from [daily_stats] config (fire-and-forget; the loop handles its own exceptions).
            if (Settings.Current.daily_stats.enable)
            {
                _ = Task.Run(() => NotifyManager.DailyStatsLoopAsync(_shutdownCts.Token));
                CommonHelper.WriteLine("Daily stats scheduler enabled");
            }
            else
            {
                CommonHelper.WriteLine("Daily stats scheduler disabled ([daily_stats].enable = false)");
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
                // InitializeAsync retries until connected; it only returns on
                // success or throws when cancelled.
                await imapService.InitializeAsync(OnNewMailAsync, _shutdownCts.Token);
                return imapService;
            }
            catch (Exception ex)
            {
                CommonHelper.WriteError($"IMAP check failed: {ex}");
                imapService.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Callback for new IMAP mail: enqueue a status reply. Must never block, or the IDLE loop stalls.
        /// Provider-friendly: automatic/bounce senders (no-reply, mailer-daemon, ...) never get a
        /// reply - answering them creates pointless outbound traffic and feeds spam backscatter.
        /// When [imap_server].reply_allowlist is non-empty, only listed senders are answered.
        /// Only emails with subject "hello" (case-insensitive) trigger a reply.
        /// </summary>
        private static Task OnNewMailAsync(MimeMessage message)
        {
            var from = message.From.Mailboxes.FirstOrDefault()?.Address;
            CommonHelper.WriteLine($"IMAP newmail: {message.Subject} from {from ?? "<unknown>"}");

            if (string.IsNullOrWhiteSpace(message.Subject))
                return Task.CompletedTask;

            // Only process emails with subject "hello" (case-insensitive)
            if (!message.Subject.Equals(TriggerSubject, StringComparison.OrdinalIgnoreCase))
            {
                CommonHelper.WriteLine($"IMAP: skipped - subject '{message.Subject}' is not '{TriggerSubject}'");
                return Task.CompletedTask;
            }

            if (from == null)
            {
                CommonHelper.WriteLine($"IMAP: skipped auto/bounce sender ({from ?? "no address"}) - no reply sent");
                return Task.CompletedTask;
            }

            var allowlist = Settings.Current.imap_server.reply_allowlist;
            if (allowlist is { Length: > 0 } &&
                !allowlist.Contains(from, StringComparer.OrdinalIgnoreCase))
            {
                CommonHelper.WriteLine($"IMAP: sender {from} not in reply_allowlist - no reply sent");
                return Task.CompletedTask;
            }

            var stats = NotifyManager.RequestByEmail();
            EmailQueue.Enqueue("Reply: your mining status", stats); // fire-and-forget; delivered by the EmailQueue background worker.
            return Task.CompletedTask;
        }
 

        /// <summary>Worker-count polling loop: reads the data-api connection count with debounce/trend detection.</summary>
        private static async Task PollWorkersLoop()
        {
            
            while (!_shutdownCts.IsCancellationRequested)
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
                    await Task.Delay(TimeSpan.FromSeconds(5), _shutdownCts.Token);
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
                using var tailer = new FileTailer(Settings.Current.p2pool_log.file_path);
                await tailer.RunAsync(_shutdownCts.Token);
                return 0;
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
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
            CommonHelper.WriteLine("Shutting down...");

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

         
        /// <summary>0 = shutdown not yet requested; 1 = a Ctrl+C was already handled.</summary>
        private static int _shutdownRequested;

        /// <summary>True while we own a temporary "stty -echoctl" on the controlling terminal.</summary>
        private static bool _ctrlEchoSuppressed;

        /// <summary>
        /// The '^C' shown when Ctrl+C is pressed is echoed by the terminal driver itself
        /// (termios ECHOCTL), not by this program, so it can land mid-line next to our
        /// log output. Disable it on interactive Unix terminals and restore on exit.
        /// </summary>
        private static void SuppressCtrlCEcho()
        {
            if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return;
            if (Console.IsInputRedirected) return; // no tty (e.g. systemd): nothing to suppress

            try
            {
                // stty acts on the terminal it inherits via stdin, which is the
                // controlling terminal here because input is not redirected.
                using var stty = Process.Start(new ProcessStartInfo("stty", "-echoctl"));
                stty?.WaitForExit(1000);
                _ctrlEchoSuppressed = stty is { HasExited: true, ExitCode: 0 };
            }
            catch
            {
                // Purely cosmetic; never block startup over it.
            }
        }

        private static void RestoreCtrlCEcho()
        {
            if (!_ctrlEchoSuppressed) return;
            _ctrlEchoSuppressed = false;

            try
            {
                using var stty = Process.Start(new ProcessStartInfo("stty", "echoctl"));
                stty?.WaitForExit(1000);
            }
            catch
            {
                // Best effort - the terminal is typically gone anyway at this point.
            }
        }

        private static void OnCancelRequested(object? sender, ConsoleCancelEventArgs e)
        {
            if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
            {
                // Second Ctrl+C: graceful shutdown is apparently stuck (e.g. SMTP
                // drain or IMAP disconnect hung), so let the OS terminate us.
                CommonHelper.WriteLine("Second Ctrl+C received - forcing termination.");
                e.Cancel = false;
                return;
            }

            e.Cancel = true; // Suppress the default termination and shut down cooperatively instead.
            CommonHelper.WriteLine("Ctrl+C received - shutting down...");
            _shutdownCts.Cancel();
        }
    }
}