using MailKit.Security;
using MimeKit;
using p2poolmail;
using System.Reflection;

namespace p2poolmail.Tests;

/// <summary>
/// Unit tests for the SMTP pipeline pieces that are deterministic without real
/// network I/O: configuration validation, message construction, envelope rules
/// and the TLS policy mapping.
/// </summary>
public class SmtpPipelineTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"p2poolmail.smtptest.{Guid.NewGuid():N}.toml");

    public void Dispose()
    {
        // Never leak a running queue worker between tests.
        EmailQueue.AbortAsync().GetAwaiter().GetResult();
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
