using System.Reflection;
using p2poolmail;

namespace Tests;

/// <summary>
/// Tests the alert latch/recovery state machine of Notification. The latched
/// recovery emails go through EmailQueue.Enqueue, which safely no-ops (with a
/// warning) when the queue was never initialized - exactly the test scenario.
/// Notification state is static, so every test resets it via reflection.
/// </summary>
public class NotificationTests
{
    private const long T0 = 1_000_000;

    public NotificationTests()
    {
        ResetState();
    }

    private static void ResetState()
    {
        var t = typeof(Notification);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;

        // Slots is a readonly array: clear each element in place (Slot.Reset()).
        var slots = (Array)t.GetField("Slots", flags)!.GetValue(null)!;
        var reset = slots.GetType().GetElementType()!.GetMethod("Reset")!;
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots.GetValue(i)!;
            reset.Invoke(slot, null);
            slots.SetValue(slot, i);
        }

        // Re-enable every type (Settings tests may have disabled some).
        var enabled = (bool[])t.GetField("Enabled", flags)!.GetValue(null)!;
        for (var i = 0; i < enabled.Length; i++)
            enabled[i] = true;
    }

    private static bool IsFault(Notification.Type type)
    {
        var slots = (Array)typeof(Notification).GetField("Slots", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var boxed = slots.GetValue((int)type)!;
        return (bool)boxed.GetType().GetField("IsFault", BindingFlags.Public | BindingFlags.Instance)!.GetValue(boxed)!;
    }

    [Fact]
    public void ObserveAlert_FastBurst_LatchesFault()
    {
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0);
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 1);
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 2);

        Assert.True(IsFault(Notification.Type.ZMQNotRunning));
    }

    [Fact]
    public void ObserveAlert_BelowBurstCount_DoesNotLatch()
    {
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0);
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 1);

        Assert.False(IsFault(Notification.Type.ZMQNotRunning));
    }

    [Fact]
    public void ObserveAlert_PersistentSlowBursts_LatchEventually()
    {
        // Each burst window (30s) expires with only 1 hit: 3 consecutive failed
        // bursts must still latch a persistent low-rate fault.
        Notification.ObserveAlert(Notification.Type.MonerodNotSynchronized, T0 + 100);
        Notification.ObserveAlert(Notification.Type.MonerodNotSynchronized, T0 + 200);
        Notification.ObserveAlert(Notification.Type.MonerodNotSynchronized, T0 + 300);
        Notification.ObserveAlert(Notification.Type.MonerodNotSynchronized, T0 + 400);

        Assert.True(IsFault(Notification.Type.MonerodNotSynchronized));
    }

    [Fact]
    public void ObserveAlert_AlreadyLatched_StaysLatched()
    {
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0);
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 1);
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 2);
        Assert.True(IsFault(Notification.Type.ZMQNotRunning));

        // Further observations must not unlatch.
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 10_000);
        Assert.True(IsFault(Notification.Type.ZMQNotRunning));
    }

    [Fact]
    public void TryResume_FaultSeenRecently_StaysLatched()
    {
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0);
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 1);
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 2);

        Notification.TryResume(T0 + 30); // exactly at the recovery window boundary

        Assert.True(IsFault(Notification.Type.ZMQNotRunning));
    }
    [Fact]
    public void TryResume_FaultUnseenBeyondWindow_Resets()
    {
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0);
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 1);
        Notification.ObserveAlert(Notification.Type.ZMQNotRunning, T0 + 2);
        Assert.True(IsFault(Notification.Type.ZMQNotRunning));

        // Last alert was at T0+2 (which latched the fault), so recovery needs
        // utcNow - (T0+2) > 30s.
        Notification.TryResume(T0 + 33);

        Assert.False(IsFault(Notification.Type.ZMQNotRunning));
    }

    [Fact]
    public void TryResume_WithoutFault_DoesNothing()
    {
        Notification.TryResume(T0 + 10_000);

        Assert.False(IsFault(Notification.Type.ZMQNotRunning));
        Assert.False(IsFault(Notification.Type.GotaPayout));
    }

    [Fact]
    public void Enabled_Toggle_Works()
    {
        try
        {
            Notification.SetEnabled(Notification.Type.ShareFound, false);
            Assert.False(Notification.IsEnabled(Notification.Type.ShareFound));
            Assert.True(Notification.IsEnabled(Notification.Type.GotaPayout));
        }
        finally
        {
            Notification.SetEnabled(Notification.Type.ShareFound, true);
        }
    }

    [Fact]
    public void Keywords_MatchCatalogAndTypeOrder()
    {
        var types = Enum.GetValues<Notification.Type>();
        Assert.Equal(types.Length, Notification.Keywords.Length);
        Assert.Equal("SHARE FOUND", Notification.Keywords[(int)Notification.Type.ShareFound]);
        Assert.Equal("got a payout", Notification.Keywords[(int)Notification.Type.GotaPayout]);
        Assert.Equal("ZMQ is not running", Notification.Keywords[(int)Notification.Type.ZMQNotRunning]);
    }
}
