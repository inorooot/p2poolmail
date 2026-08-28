using MailKit.Security;
using MimeKit;
using p2poolmail;
using System.Reflection;

namespace p2poolmail.Tests;

/// <summary>
/// Unit tests for the SMTP pipeline: configuration validation, message
/// construction, envelope rules and TLS policy mapping.
/// </summary>
public class SmtpPipelineTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"p2poolmail.smtptest.{Guid.NewGuid():N}.toml");

    public void Dispose()
    {
        try { EmailQueue.AbortAsync().GetAwaiter().GetResult(); } catch { }
        try { File.Delete(_path); } catch { }
    }

    private static string ValidSmtp => """
        [p2pool_log]
        file_path = "/tmp/p2pool.log"
        data_api_dir = "/tmp/data_api"

        [smtp]
        host = "smtp.example.com"
        port = 465
        useSsl = true
        username = "sender@example.com"
        fromName = "p2poolmail"
        toName = "Miner"
        password = "secret"

        [receiver]
        toAddress = "alerts@example.com"
        """;

    private void Load(string toml)
    {
        File.WriteAllText(_path, toml);
        Settings.Initialize(_path);
    }

    private static EmailQueue? CurrentInstance =>
        (EmailQueue?)typeof(EmailQueue).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    private static MimeMessage CreateMessage(string subject, string body, string? correlationId, bool isHtml)
    {
        var method = typeof(EmailQueue).GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (MimeMessage)method.Invoke(CurrentInstance!, [subject, body, correlationId, isHtml])!;
    }

    // ========== Configuration Validation ==========

    [Fact]
    public void Load_ValidSmtpConfig_ParsesCorrectly()
    {
        Load(ValidSmtp);
        var s = Settings.Current;
        Assert.Equal("smtp.example.com", s.smtp.host);
        Assert.Equal(465, s.smtp.port);
        Assert.True(s.smtp.useSsl);
        Assert.Equal("sender@example.com", s.smtp.username);
    }

    [Fact]
    public void Load_MissingHost_ThrowsAtQueueCreation()
    {
        Load("""
            [p2pool_log]
            file_path = "/tmp/p2pool.log"
            data_api_dir = "/tmp/data_api"

            [smtp]
            host = ""
            port = 465
            useSsl = true
            username = "sender@example.com"
            password = "secret"

            [receiver]
            toAddress = "alerts@example.com"
            """);
        Assert.Throws<InvalidOperationException>(() => EmailQueue.Initialize());
    }

    [Fact]
    public void Load_InvalidPort_ThrowsAtQueueCreation()
    {
        Load("""
            [p2pool_log]
            file_path = "/tmp/p2pool.log"
            data_api_dir = "/tmp/data_api"

            [smtp]
            host = "smtp.example.com"
            port = 0
            useSsl = true
            username = "sender@example.com"
            password = "secret"

            [receiver]
            toAddress = "alerts@example.com"
            """);
        Assert.Throws<InvalidOperationException>(() => EmailQueue.Initialize());
    }

    [Fact]
    public void Load_MissingUsername_ThrowsAtQueueCreation()
    {
        Load("""
            [p2pool_log]
            file_path = "/tmp/p2pool.log"
            data_api_dir = "/tmp/data_api"

            [smtp]
            host = "smtp.example.com"
            port = 465
            useSsl = true
            username = ""
            password = "secret"

            [receiver]
            toAddress = "alerts@example.com"
            """);
        Assert.Throws<InvalidOperationException>(() => EmailQueue.Initialize());
    }

    [Fact]
    public void Load_MissingPassword_ThrowsAtQueueCreation()
    {
        Load("""
            [p2pool_log]
            file_path = "/tmp/p2pool.log"
            data_api_dir = "/tmp/data_api"

            [smtp]
            host = "smtp.example.com"
            port = 465
            useSsl = true
            username = "sender@example.com"
            password = ""

            [receiver]
            toAddress = "alerts@example.com"
            """);
        Assert.Throws<InvalidOperationException>(() => EmailQueue.Initialize());
    }

    [Fact]
    public void Load_MissingToAddress_ThrowsAtQueueCreation()
    {
        Load("""
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
            toAddress = ""
            """);
        Assert.Throws<InvalidOperationException>(() => EmailQueue.Initialize());
    }

    // ========== TLS Policy Mapping ==========

    [Fact]
    public void SmtpConfig_UseSslTrue_MapsToSslOnConnect()
    {
        Load(ValidSmtp);
        EmailQueue.Initialize();
        var queue = CurrentInstance;
        Assert.NotNull(queue);

        var senderField = typeof(EmailQueue).GetField("_sender", BindingFlags.NonPublic | BindingFlags.Instance);
        var sender = senderField?.GetValue(queue);
        Assert.NotNull(sender);

        var socketOptionsMethod = sender!.GetType().GetMethod("SocketOptions", BindingFlags.NonPublic | BindingFlags.Instance);
        var options = socketOptionsMethod?.Invoke(sender, null);
        Assert.Equal(SecureSocketOptions.SslOnConnect, options);
    }

    [Fact]
    public void SmtpConfig_UseSslFalse_MapsToNone()
    {
        Load("""
            [p2pool_log]
            file_path = "/tmp/p2pool.log"
            data_api_dir = "/tmp/data_api"

            [smtp]
            host = "smtp.example.com"
            port = 587
            useSsl = false
            username = "sender@example.com"
            fromName = "p2poolmail"
            toName = "Miner"
            password = "secret"

            [receiver]
            toAddress = "alerts@example.com"
            """);
        EmailQueue.Initialize();
        var queue = CurrentInstance;
        Assert.NotNull(queue);

        var senderField = typeof(EmailQueue).GetField("_sender", BindingFlags.NonPublic | BindingFlags.Instance);
        var sender = senderField?.GetValue(queue);
        Assert.NotNull(sender);

        var socketOptionsMethod = sender!.GetType().GetMethod("SocketOptions", BindingFlags.NonPublic | BindingFlags.Instance);
        var options = socketOptionsMethod?.Invoke(sender, null);
        Assert.Equal(SecureSocketOptions.None, options);
    }

    // ========== Message Construction ==========

    [Fact]
    public void CreateMessage_TextOnly_SetsCorrectProperties()
    {
        Load(ValidSmtp);
        EmailQueue.Initialize();

        var msg = CreateMessage("Test Subject", "Test Body", null, false);
        Assert.Equal("Test Subject", msg.Subject);
        Assert.Equal("Test Body", msg.TextBody);
        Assert.Null(msg.HtmlBody);
        var toAddress = msg.To.Mailboxes.FirstOrDefault();
        Assert.NotNull(toAddress);
        Assert.Equal("alerts@example.com", toAddress.Address);
        Assert.Equal("Miner", toAddress.Name);
    }

    [Fact]
    public void CreateMessage_HtmlContent_SetsHtmlBody()
    {
        Load(ValidSmtp);
        EmailQueue.Initialize();

        var msg = CreateMessage("HTML Subject", "<p>HTML Body</p>", null, true);
        Assert.Equal("HTML Subject", msg.Subject);
        Assert.Equal("<p>HTML Body</p>", msg.HtmlBody);
        Assert.Null(msg.TextBody);
    }

    [Fact]
    public void CreateMessage_WithCorrelationId_SetsHeaders()
    {
        Load(ValidSmtp);
        EmailQueue.Initialize();

        var msg = CreateMessage("Re: Status", "Body", "corr-123", false);
        Assert.True(msg.Headers.Contains("X-Correlation-Id"));
        Assert.Equal("corr-123", msg.Headers["X-Correlation-Id"]);
    }

    // ========== Envelope Rules ==========

    [Fact]
    public void SmtpConfig_ToAddressOverridesReceiver()
    {
        Load("""
            [p2pool_log]
            file_path = "/tmp/p2pool.log"
            data_api_dir = "/tmp/data_api"

            [smtp]
            host = "smtp.example.com"
            port = 465
            useSsl = true
            username = "sender@example.com"
            fromName = "p2poolmail"
            toName = "Miner"
            toAddress = "override@example.com"
            password = "secret"

            [receiver]
            toAddress = "alerts@example.com"
            """);
        EmailQueue.Initialize();

        var msg = CreateMessage("Test", "Body", null, false);
        var toAddress = msg.To.Mailboxes.FirstOrDefault();
        Assert.NotNull(toAddress);
        Assert.Equal("override@example.com", toAddress.Address);
    }

    [Fact]
    public void SmtpConfig_FallbackToReceiverWhenToAddressEmpty()
    {
        Load(ValidSmtp);
        EmailQueue.Initialize();

        var msg = CreateMessage("Test", "Body", null, false);
        var toAddress = msg.To.Mailboxes.FirstOrDefault();
        Assert.NotNull(toAddress);
        Assert.Equal("alerts@example.com", toAddress.Address);
    }

    // ========== Rate Limiting ==========

    [Fact]
    public void SmtpSender_RateLimit_IsOneSecond()
    {
        Load(ValidSmtp);
        EmailQueue.Initialize();
        var queue = CurrentInstance;
        Assert.NotNull(queue);

        var senderField = typeof(EmailQueue).GetField("_sender", BindingFlags.NonPublic | BindingFlags.Instance);
        var sender = senderField?.GetValue(queue);
        Assert.NotNull(sender);

        var minSendInterval = sender!.GetType().GetField("MinSendInterval", BindingFlags.NonPublic | BindingFlags.Static);
        var value = (TimeSpan)minSendInterval?.GetValue(null)!;
        Assert.Equal(TimeSpan.FromSeconds(1), value);
    }

    // ========== Retry Policy ==========

    [Fact]
    public void EmailQueueConstants_HaveExpectedValues()
    {
        var maxAttemptsField = typeof(EmailQueue).GetField("MaxAttempts", BindingFlags.NonPublic | BindingFlags.Static);
        var initialRetryDelayField = typeof(EmailQueue).GetField("InitialRetryDelayMs", BindingFlags.NonPublic | BindingFlags.Static);
        var maxRetryDelayField = typeof(EmailQueue).GetField("MaxRetryDelayMs", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.Equal(5, maxAttemptsField?.GetValue(null));
        Assert.Equal(2000, initialRetryDelayField?.GetValue(null));
        Assert.Equal(15000, maxRetryDelayField?.GetValue(null));
    }

    // ========== Channel Configuration ==========

    [Fact]
    public void EmailQueueConstants_MaxPending()
    {
        var maxPendingField = typeof(EmailQueue).GetField("MaxPending", BindingFlags.NonPublic | BindingFlags.Static);
        var warnPendingField = typeof(EmailQueue).GetField("WarnPending", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.Equal(1024, maxPendingField?.GetValue(null));
        Assert.Equal(256, warnPendingField?.GetValue(null));
    }
}
