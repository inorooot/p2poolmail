using p2poolmail;

namespace p2poolmail.Tests;

/// <summary>Tests TOML config loading with a temporary config file.</summary>
public class SettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"p2poolmail.test.{Guid.NewGuid():N}.toml");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private Settings Load(string toml)
    {
        File.WriteAllText(_path, toml);
        Settings.Initialize(_path);
        return Settings.Current;
    }

    [Fact]
    public void Load_FullConfig_ParsesAllSections()
    {
        var s = Load("""
            [p2pool_log]
            file_path = "/tmp/p2pool.log"
            data_api_dir = "/tmp/data_api"

            [smtp]
            host = "smtp.example.com"
            port = 465
            useSsl = true
            username = "sender@example.com"
            fromName = "p2poolmail"
            password = "secret"

            [receiver]
            toAddress = "alerts@example.com"

            [notify_event]
            share_found = false
            got_payout = true
            worker_down_up = false

            [imap_server]
            enable = true
            host = "imap.example.com"
            port = 993
            useSsl = true
            username = "recv@example.com"
            password = "secret2"
            reply_allowlist = ["me@example.com", "admin@example.com"]
            """);

        Assert.Equal("smtp.example.com", s.smtp.host);
        Assert.Equal(465, s.smtp.port);
        Assert.True(s.smtp.useSsl);
        Assert.Equal("sender@example.com", s.smtp.username);
        Assert.Equal("alerts@example.com", s.receiver.toAddress);

        Assert.True(s.imap_server.enable);
        Assert.Equal("imap.example.com", s.imap_server.host);
        Assert.Equal(993, s.imap_server.port);
        Assert.Equal(new[] { "me@example.com", "admin@example.com" }, s.imap_server.reply_allowlist);
    }

    [Fact]
    public void Load_MissingImapSection_ImapDisabledByDefault()
    {
        var s = Load("""
            [p2pool_log]
            file_path = "/tmp/p2pool.log"
            data_api_dir = "/tmp/data_api"

            [smtp]
            host = "smtp.example.com"
            port = 465
            useSsl = true
            username = "sender@example.com"
            password = "secret"

            [receiver]
            toAddress = "alerts@example.com"
            """);

        // Opt-in feature: configs without [imap_server] must never connect.
        Assert.False(s.imap_server.enable);
        Assert.Empty(s.imap_server.reply_allowlist);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        Assert.ThrowsAny<Exception>(() => Settings.Initialize(Path.Combine(Path.GetTempPath(), "definitely-missing.toml")));
    }
}
