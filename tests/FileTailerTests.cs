using System.Reflection;
using p2poolmail;

namespace Tests;

/// <summary>
/// Regression tests for the FileTailer rotation handling. The fixed bug: when
/// the log file was rotated to a NEW EMPTY file (rename + create), CheckRotation
/// returned without re-opening the stream, leaving it attached to the renamed
/// (dead) inode while the read position had already been reset to 0 - the next
/// drain then re-read the ENTIRE old log from byte 0 and replayed every line,
/// producing duplicate SHARE FOUND / payout / alert emails.
/// Private members are reached via reflection, consistent with the other test
/// classes in this project. GlobalState collection: every tailed line runs
/// NotifyManager.Handle, which reads Notification state that other tests reset
/// via reflection (see AssemblyInfo.cs for the assembly policy).
/// </summary>
[Collection("GlobalState")]
public class FileTailerTests : IDisposable
{
    private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"p2poolmail.tailtest.{Guid.NewGuid():N}.log");
    private readonly string _rotatedPath;

    public FileTailerTests()
    {
        _rotatedPath = _path + ".1";
    }

    public void Dispose()
    {
        foreach (var p in new[] { _path, _rotatedPath })
        {
            try { File.Delete(p); } catch { }
        }
    }

    private object GetField(object target, string name) =>
        typeof(FileTailer).GetField(name, Flags)!.GetValue(target)!;

    private void SetField(object target, string name, object value) =>
        typeof(FileTailer).GetField(name, Flags)!.SetValue(target, value);

    private object Invoke(object target, string name, params object[] args) =>
        typeof(FileTailer).GetMethod(name, Flags)!.Invoke(target, args)!;

    private async Task<bool> DrainAsync(object tailer, byte[] buffer)
    {
        var task = (Task<bool>)Invoke(tailer, "DrainAvailableAsync", buffer, CancellationToken.None);
        return await task;
    }

    private bool CheckRotation(object tailer, long readPos, out long newPos)
    {
        var args = new object[] { readPos, 0L };
        var rotated = (bool)Invoke(tailer, "CheckRotation", args);
        newPos = (long)args[1];
        return rotated;
    }

    [Fact]
    public async Task Rotation_ToEmptyFile_ReopensCurrentFile_AndNeverReplaysOldContent()
    {
        var oldContent = "old line 1\nold line 2\n";
        File.WriteAllText(_path, oldContent);

        var tailer = new FileTailer(_path);
        Invoke(tailer, "EnsureStream");

        var buffer = new byte[8192];
        // EnsureStream positions at EOF by design (tailer starts at the end of
        // the file), so the first drain is a no-op.
        Assert.False(await DrainAsync(tailer, buffer));
        Assert.Equal((long)oldContent.Length, (long)GetField(tailer, "_readPosition"));

        // Rotate: rename the old file away, put a NEW EMPTY file in its place.
        File.Move(_path, _rotatedPath);
        File.WriteAllText(_path, string.Empty);
        SetField(tailer, "_lastWriteUtc", DateTime.MinValue);

        Assert.True(CheckRotation(tailer, (long)GetField(tailer, "_readPosition"), out var newPos));
        Assert.Equal(0L, newPos);
        SetField(tailer, "_readPosition", newPos);

        // The stream MUST now track the current (empty) file, not the renamed one.
        var stream = (FileStream)GetField(tailer, "_stream");
        Assert.NotNull(stream);
        Assert.Equal(0L, stream.Length);

        // The drain that follows must be a no-op: no replay of the renamed old
        // log (the old behavior re-read all of it and re-fired every event).
        Assert.False(await DrainAsync(tailer, buffer));
        Assert.Equal(0L, (long)GetField(tailer, "_readPosition"));

        // First write to the new file: exactly the new bytes get consumed.
        var newContent = "new line 1\n";
        File.AppendAllText(_path, newContent);
        SetField(tailer, "_lastWriteUtc", DateTime.MinValue);
        Assert.True(CheckRotation(tailer, 0L, out newPos));
        Assert.Equal(0L, newPos);

        Assert.True(await DrainAsync(tailer, buffer));
        Assert.Equal((long)newContent.Length, (long)GetField(tailer, "_readPosition"));
    }

    [Fact]
    public async Task PlainAppend_KeepsPendingPartialLine_AcrossRotationChecks()
    {
        File.WriteAllText(_path, "line1\n");
        var tailer = new FileTailer(_path);
        Invoke(tailer, "EnsureStream");

        var buffer = new byte[8192];

        // Append without a newline: the partial line must survive the rotation
        // check (a plain append must NOT reset the pending line buffer).
        File.AppendAllText(_path, "part");
        SetField(tailer, "_lastWriteUtc", DateTime.MinValue);
        Assert.True(CheckRotation(tailer, (long)GetField(tailer, "_readPosition"), out _));

        Assert.True(await DrainAsync(tailer, buffer));
        Assert.Equal(4, (int)GetField(tailer, "_lineBufferLength")); // "part" pending

        // Completing the line must flush it exactly once.
        File.AppendAllText(_path, "ial\n");
        SetField(tailer, "_lastWriteUtc", DateTime.MinValue);
        Assert.True(CheckRotation(tailer, (long)GetField(tailer, "_readPosition"), out _));

        Assert.True(await DrainAsync(tailer, buffer));
        Assert.Equal(0, (int)GetField(tailer, "_lineBufferLength"));
        Assert.Equal((long)"line1\npartial\n".Length, (long)GetField(tailer, "_readPosition"));
    }
}