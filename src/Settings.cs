using Tomlyn;
using Tomlyn.Serialization;

namespace p2poolmail;

/// <summary>
/// Application configuration, loaded once at startup from Config.toml.
/// Property/field names intentionally mirror the TOML section names used by the serializer.
/// </summary>
internal sealed partial class Settings
{
    public static Settings Current { get; private set; } = new();

    public SMTP smtp { get; set; } = new();
    public Receiver receiver { get; set; } = new();
    public Keepalive keepalive { get; set; } = new();
    public DailyStats daily_stats { get; set; } = new();
    public P2poolLogPath p2pool_log { get; set; } = new();
    public EventEnable notify_event { get; set; } = new();
    public ImapServer imap_server { get; set; } = new();

    /// <summary>Loads the config from the given path (default: "./Config.toml").</summary>
    public static void Initialize(string? path = null)
    {
        var configPath = path ?? Path.Combine(Directory.GetCurrentDirectory(), "Config.toml");
        Current = new Settings(configPath);
    }

    // Parameterless constructor is required by the Tomlyn source-generated serializer.
    public Settings()
    {
    }

    private Settings(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"config not found: {configPath}", configPath);

        var content = File.ReadAllText(configPath);
        var loaded = TomlSerializer.Deserialize<Settings>(content, SettingsContext.Default.Settings)
                     ?? throw new InvalidOperationException("config file error");

        smtp = loaded.smtp;
        receiver = loaded.receiver;
        keepalive = loaded.keepalive;
        daily_stats = loaded.daily_stats;
        p2pool_log = loaded.p2pool_log;
        notify_event = loaded.notify_event;
        imap_server = loaded.imap_server;

        ApplyNotificationFlags();
        Validate();
    }

    private void ApplyNotificationFlags()
    {
        Notification.SetEnabled(Notification.Type.ShareFound, notify_event.share_found);
        Notification.SetEnabled(Notification.Type.GotaPayout, notify_event.got_payout);
        

        // notify_event.worker_down_or_up has no matching Notification.Type yet (feature not implemented).
    }

    /// <summary>Warns early about values that would otherwise only fail later at runtime.</summary>
    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(p2pool_log.file_path))
            CommonHelper.WriteWarn("Config: [p2pool_log].file_path is empty - log tailing will not work.");
        if (string.IsNullOrWhiteSpace(p2pool_log.data_api_dir))
            CommonHelper.WriteWarn("Config: [p2pool_log].data_api_dir is empty - miner stats will not work.");
        if (keepalive.enable_remote_ping && string.IsNullOrWhiteSpace(keepalive.ping_url))
            CommonHelper.WriteWarn("Config: [keepalive].ping_url is empty - remote ping will not work.");
        if (keepalive.enable_remote_ping && keepalive.interval_minutes < 1)
            CommonHelper.WriteWarn("Config: [keepalive].interval_minutes must be >= 1 - clamping to 1 minute.");
    }

    internal sealed class SMTP
    {
        public string host { get; set; } = string.Empty;
        public int port { get; set; }
        public bool useSsl { get; set; }

        // Optional username for SMTP authentication; if empty, fromAddress will be used.
        public string username { get; set; } = string.Empty;
        public string password { get; set; } = string.Empty;
       // public string fromAddress { get; set; } = string.Empty;
        public string fromName { get; set; } = string.Empty;
        public string toAddress { get; set; } = string.Empty;
        public string toName { get; set; } = string.Empty;
    }

    internal sealed class Receiver
    {
        public string toAddress { get; set; } = string.Empty;
    }

    internal sealed class Keepalive
    {
        public bool enable_remote_ping { get; set; }

        /// <summary>Minutes between keepalive pings; values &lt; 1 are clamped at runtime.</summary>
        public int interval_minutes { get; set; } = 10;

        public string ping_url { get; set; } = string.Empty;

        /// <summary>Seconds before a single ping request is aborted; values &lt; 1 are clamped at runtime.</summary>
        public int timeout_seconds { get; set; } = 15;
    }

    internal sealed class DailyStats
    {
        public bool enable { get; set; }
        public string time_of_day { get; set; } = string.Empty;
        public int frequency_hours { get; set; }
    }

    internal sealed class P2poolLogPath
    {
        public string file_path { get; set; } = string.Empty;

        // Raw --data-api directory exactly as configured.
        public string data_api_dir { get; set; } = string.Empty;

        /// <summary>Path of the local stratum endpoint under the data-api directory.</summary>
        public string StratumApiDir => Path.Combine(data_api_dir, "local", "stratum");
    }

    internal sealed class EventEnable
    {
        public bool share_found { get; set; } = true;
        public bool got_payout { get; set; } = true;
        public bool worker_down_up { get; set; } = true;
    }

    internal sealed class ImapServer
    {
        /// <summary>Opt-in feature: default off so configs without an [imap_server] section never attempt a connection.</summary>
        public bool enable { get; set; } = false;
        public string host { get; set; } = string.Empty;
        public int port { get; set; }
        public bool useSsl { get; set; }
        public string username { get; set; } = string.Empty;
        public string password { get; set; } = string.Empty;
        /// <summary>When non-empty, status replies are only sent to these sender addresses (case-insensitive). Keep empty to reply to any human sender.</summary>
        public string[] reply_allowlist { get; set; } = System.Array.Empty<string>();
    }
}

[TomlSerializable(typeof(Settings))]
[TomlSerializable(typeof(Settings.SMTP))]
[TomlSerializable(typeof(Settings.Receiver))]
[TomlSerializable(typeof(Settings.Keepalive))]
[TomlSerializable(typeof(Settings.DailyStats))]
[TomlSerializable(typeof(Settings.P2poolLogPath))]
[TomlSerializable(typeof(Settings.EventEnable))]
[TomlSerializable(typeof(Settings.ImapServer))]
internal partial class SettingsContext : TomlSerializerContext
{
}
