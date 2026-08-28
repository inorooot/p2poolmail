using System.Runtime.CompilerServices;

namespace p2poolmail;

internal static class Notification
{
    public enum Type : byte
    {
        ShareFound = 0,
        GotaPayout,
        MonerodNotSynchronized,
        MonerodBusySyncing,
        ErrorEBADF,
        ZMQNotRunning
    }

    public enum Category : byte { Event, Alert }

    public enum Source : byte { Keywords, Api, Timer, Internal }

    private const int AlertBurstCount = 3;
    private const long RecoveryWindowSeconds = 30;
    private const long AlertWindowSeconds = 30;

    /// <summary>
    /// How many consecutive burst windows may expire without a fast-path alert
    /// before a recurring (but slow) fault is considered persistent enough to alert.
    /// Guards against the old behavior where a low-rate fault never reached
    /// AlertBurstCount inside AlertWindowSeconds and therefore NEVER alerted.
    /// </summary>
    private const int MaxFailedBursts = 3;

    /// <summary>Immutable notification definition. Index in the catalog MUST equal (int)Type.</summary>
    public readonly record struct Spec(Type Type, Category Category, string Keyword, string Subject, string Body);

    /// <summary>Mutable alert state for latching/recovering one notification type.</summary>
    private struct Slot
    {
        public long SeenCount;
        public long FirstSeen;
        public long LastSeen;
        public bool IsFault;

        /// <summary>
        /// Number of burst windows that expired without reaching AlertBurstCount.
        /// A fault that keeps recurring slower than the burst window must still
        /// alert eventually instead of looping through resets forever.
        /// </summary>
        public int FailedBursts;

        public void Reset()
        {
            this = default;
        }
    }

    // Single source of truth; all notifications start enabled.
    private static readonly Spec[] Catalog =
    [
        new(Type.ShareFound, Category.Event, "SHARE FOUND", $"{EmailIcons.ShareFound} SHARE FOUND", "{line}  "),
        new(Type.GotaPayout, Category.Event, "got a payout", $"{EmailIcons.Payout} Got a Payout", "{line}  "),
        new(Type.MonerodNotSynchronized, Category.Alert, "monerod is not synchronized", $"{EmailIcons.Warning} Monerod is not synchronized", $"{EmailIcons.Warning} P2Pool: Monerod is not synchronized "),
        new(Type.MonerodBusySyncing, Category.Alert, "monerod is busy syncing", $"{EmailIcons.Warning} Monerod is busy syncing", $"{EmailIcons.Warning} P2pool: monerod is busy syncing "),
        new(Type.ErrorEBADF, Category.Alert, "error EBADF", $"{EmailIcons.Alert} JSONRPCRequest error EBADF", $"{EmailIcons.Alert} JSONRPCRequest uv_poll_start returned error EBADF "),
        new(Type.ZMQNotRunning, Category.Alert, "ZMQ is not running", $"{EmailIcons.Alert} ZMQ is not running", $"{EmailIcons.Alert} P2PServer ZMQ is not running ")
    ];

    public static readonly string[] Keywords;
    private static readonly bool[] Enabled;
    private static readonly Slot[] Slots = new Slot[Catalog.Length];

    static Notification()
    {
        Keywords = new string[Catalog.Length];
        Enabled = new bool[Catalog.Length];
        for (var i = 0; i < Catalog.Length; i++)
        {
            if ((int)Catalog[i].Type != i)
                throw new InvalidOperationException($"Notification.Catalog[{i}] must be declared in Type order.");
            Keywords[i] = Catalog[i].Keyword;
            Enabled[i] = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(Type type) => Enabled[(int)type];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetEnabled(Type type, bool enabled) => Enabled[(int)type] = enabled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Category CategoryOf(Type type) => Catalog[(int)type].Category;

    public static string GetSubject(Type type) => Catalog[(int)type].Subject;

    public static string GetBody(Type type) => Catalog[(int)type].Body;

    /// <summary>
    /// Observes an alert-type line: latch the fault (send one alert email) after
    /// AlertBurstCount hits within AlertWindowSeconds (fast path). If bursts keep
    /// expiring because the fault recurs slower than the window, the window is
    /// restarted FROM THE CURRENT LINE and after MaxFailedBursts consecutive slow
    /// bursts the fault is latched anyway — a persistent low-rate fault must not
    /// stay silent forever.
    /// </summary>
    public static void ObserveAlert(Type type, long utcNow)
    {
        var i = (int)type;
        ref var slot = ref Slots[i];
        slot.SeenCount++;

        // Already latched: just extend the recovery deadline.
        if (slot.IsFault)
        {
            slot.LastSeen = utcNow;
            return;
        }

        if (slot.FirstSeen == 0)
            slot.FirstSeen = utcNow;
        slot.LastSeen = utcNow;

        // Fast path: dense burst inside the window confirms a real fault.
        if (slot.SeenCount >= AlertBurstCount && utcNow - slot.FirstSeen <= AlertWindowSeconds)
        {
            Latch(i, ref slot);
            return;
        }

        if (utcNow - slot.FirstSeen > AlertWindowSeconds)
        {
             
            slot.FailedBursts++;
            slot.FirstSeen = utcNow;
            slot.SeenCount = 1;

            if (slot.FailedBursts >= MaxFailedBursts)
                Latch(i, ref slot);
        }
    }

    private static void Latch(int i, ref Slot slot)
    {
        slot.IsFault = true;
        CommonHelper.WriteLine($"alert: latched \"{Catalog[i].Subject}\" - sending alert email");
        EmailQueue.Enqueue(Catalog[i].Subject, Catalog[i].Body, $"alertid:{i}");
    }

    /// <summary>
    /// Called on every observed log line: a latched fault unseen for more than
    /// RecoveryWindowSeconds is considered recovered and sends one recovery email.
    /// </summary>
    public static void TryResume(long utcNow)
    {
        for (var i = 0; i < Catalog.Length; i++)
        {
            ref var slot = ref Slots[i];
            if (!slot.IsFault || !Enabled[i] || utcNow - slot.LastSeen <= RecoveryWindowSeconds)
                continue;

            slot.Reset();
            EmailQueue.Enqueue(
                EmailTemplates.RecoverySubject,
                $"{EmailIcons.Ok} No recurrence of the \"{Catalog[i].Body}\". Condition may have cleared.",
                $"recoverid:{i}");
        }
    }
}
