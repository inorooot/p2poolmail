using MailKit;
using MailKit.Net.Imap;
using p2poolmail;
using System.Reflection;

namespace Tests;

/// <summary>
/// Unit tests for core IMAP client service functionality: construction,
/// state machine, message processing logic, and IDLE/polling behavior.
/// </summary>
public class ImapClientServiceTests : IDisposable
{
    private List<string> _logMessages = new();
    private Action<string> _logger => msg => _logMessages.Add(msg);

    // ImapRunState is a private nested enum - access via reflection
    private static readonly Type ImapRunStateType = typeof(ImapClientService).GetNestedType("ImapRunState", BindingFlags.NonPublic)!;
    private static readonly object Disconnected = Enum.GetValues(ImapRunStateType).GetValue(0)!;
    private static readonly object Connecting = Enum.GetValues(ImapRunStateType).GetValue(1)!;
    private static readonly object Idle = Enum.GetValues(ImapRunStateType).GetValue(2)!;
    private static readonly object Polling = Enum.GetValues(ImapRunStateType).GetValue(3)!;
    private static readonly object Reconnecting = Enum.GetValues(ImapRunStateType).GetValue(4)!;
    private static readonly object Stopped = Enum.GetValues(ImapRunStateType).GetValue(5)!;

    public void Dispose()
    {
        _logMessages.Clear();
    }

    private static object GetState(ImapClientService service)
    {
        var stateField = typeof(ImapClientService).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        return stateField?.GetValue(service)!;
    }

    private static void SetState(ImapClientService service, object state)
    {
        var setStateMethod = typeof(ImapClientService).GetMethod("SetState", BindingFlags.NonPublic | BindingFlags.Instance);
        setStateMethod?.Invoke(service, [state]);
    }

    // ========== Core Constructor Tests ==========

    [Fact]
    public void Constructor_NullHost_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ImapClientService(null!, logger: _logger));
    }

    [Fact]
    public void Constructor_SetsDefaultPort993AndSslTrue()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);

        var portField = typeof(ImapClientService).GetField("_port", BindingFlags.NonPublic | BindingFlags.Instance);
        var useSslField = typeof(ImapClientService).GetField("_useSsl", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.Equal(993, portField?.GetValue(service));
        Assert.Equal(true, useSslField?.GetValue(service));
    }

    [Fact]
    public void Constructor_CreatesClientWithRevocationDisabled()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);

        var clientField = typeof(ImapClientService).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
        var client = (ImapClient?)clientField?.GetValue(service);

        Assert.NotNull(client);
        Assert.False(client.CheckCertificateRevocation);
    }

    // ========== Core State Machine Tests ==========

    [Fact]
    public void SetState_TransitionsAndLogs()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);
        _logMessages.Clear();

        SetState(service, Connecting);

        var state = GetState(service);
        Assert.Equal(Connecting, state);
        Assert.Contains(_logMessages, m => m.Contains("IMAP state -> Connecting"));
    }

    [Fact]
    public void SetState_SameState_NoLog()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);
        _logMessages.Clear();

        SetState(service, Disconnected);

        Assert.Empty(_logMessages);
    }

    [Fact]
    public void SetState_AllStates_TransitionsCorrectly()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);

        var allStates = new[] { Disconnected, Connecting, Idle, Polling, Reconnecting, Stopped };
        foreach (var expectedState in allStates)
        {
            _logMessages.Clear();
            SetState(service, expectedState);
            var actualState = GetState(service);
            Assert.Equal(expectedState, actualState);
        }
    }

    // ========== Core Configuration Tests ==========

    [Fact]
    public void CandidateLimit_EnforcesMinimumOfOne()
    {
        var service = new ImapClientService("imap.example.com", candidateLimit: 0, logger: _logger);

        var candidateLimitField = typeof(ImapClientService).GetField("_candidateLimit", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(1, candidateLimitField?.GetValue(service));
    }

    [Fact]
    public void CandidateLimit_CustomValue_Preserved()
    {
        var service = new ImapClientService("imap.example.com", candidateLimit: 10, logger: _logger);

        var candidateLimitField = typeof(ImapClientService).GetField("_candidateLimit", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(10, candidateLimitField?.GetValue(service));
    }

    [Fact]
    public void IdleResetInterval_OverridesHeartbeat()
    {
        var service = new ImapClientService("imap.example.com",
            idleResetInterval: TimeSpan.FromMinutes(5),
            logger: _logger);

        var idleHeartbeatField = typeof(ImapClientService).GetField("_idleHeartbeat", BindingFlags.NonPublic | BindingFlags.Instance);
        var idleResetIntervalField = typeof(ImapClientService).GetField("_idleResetInterval", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.Equal(TimeSpan.FromMinutes(5), idleHeartbeatField?.GetValue(service));
        Assert.Equal(TimeSpan.FromMinutes(5), idleResetIntervalField?.GetValue(service));
    }

    [Fact]
    public void MaxAttemptsPerUid_IsThree()
    {
        var maxAttemptsField = typeof(ImapClientService).GetField("MaxAttemptsPerUid", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Equal(3, maxAttemptsField?.GetValue(null));
    }

    // ========== Core Startup State Tests ==========

    [Fact]
    public void Startup_LastProcessedUid_IsNull()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);

        var lastUidField = typeof(ImapClientService).GetField("_lastProcessedUid", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Null(lastUidField?.GetValue(service));
    }

    [Fact]
    public void Startup_State_IsDisconnected()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);

        var state = GetState(service);
        Assert.Equal(Disconnected, state);
    }

    [Fact]
    public void UidProcessing_RejectsOlderUidInSameValidity()
    {
        var alreadyProcessed = InvokeIsUidAlreadyProcessed(
            uid: new UniqueId(100),
            lastProcessedUid: new UniqueId(120),
            currentUidValidity: 42,
            lastUidValidity: 42,
            uidNext: new UniqueId(130));

        Assert.True(alreadyProcessed);
    }

    [Fact]
    public void UidProcessing_AllowsNewerUidWhenUidNextMovesAhead()
    {
        var alreadyProcessed = InvokeIsUidAlreadyProcessed(
            uid: new UniqueId(125),
            lastProcessedUid: new UniqueId(120),
            currentUidValidity: 42,
            lastUidValidity: 42,
            uidNext: new UniqueId(130));

        Assert.False(alreadyProcessed);
    }

    private static bool InvokeIsUidAlreadyProcessed(UniqueId uid, UniqueId? lastProcessedUid, uint? currentUidValidity, uint? lastUidValidity, UniqueId? uidNext)
    {
        var method = typeof(ImapClientService).GetMethod("IsUidAlreadyProcessed", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [uid, lastProcessedUid, currentUidValidity, lastUidValidity, uidNext])!;
    }

    // ========== Default Values Tests ==========

    [Fact]
    public void Default_PollInterval_Is60Seconds()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);

        var pollIntervalField = typeof(ImapClientService).GetField("_pollInterval", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(TimeSpan.FromSeconds(60), pollIntervalField?.GetValue(service));
    }

    [Fact]
    public void Default_IdleHeartbeat_Is30Seconds()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);

        var idleHeartbeatField = typeof(ImapClientService).GetField("_idleHeartbeat", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(TimeSpan.FromSeconds(30), idleHeartbeatField?.GetValue(service));
    }

    [Fact]
    public void Default_CandidateLimit_Is5()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);

        var candidateLimitField = typeof(ImapClientService).GetField("_candidateLimit", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(5, candidateLimitField?.GetValue(service));
    }

    [Fact]
    public void Default_IdleMaxRetryDelay_Is30Seconds()
    {
        var service = new ImapClientService("imap.example.com", logger: _logger);

        var maxRetryDelayField = typeof(ImapClientService).GetField("_idleMaxRetryDelay", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(TimeSpan.FromSeconds(30), maxRetryDelayField?.GetValue(service));
    }
}
