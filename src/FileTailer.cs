using System.Buffers; 
using System.Text;

namespace p2poolmail;

/// <summary>
/// Event-driven file tailer: detects rotation via mtime / file growth,
/// uses FileSystemWatcher as a wake-up signal with polling as fallback.
/// Low overhead and AOT-friendly.
/// String/line processing methods keep the original implementation.
/// </summary>
internal sealed class FileTailer : IDisposable
{
    private readonly string _path;
    private const int PollIntervalSeconds=3;
    private const int IdleThresholdSeconds = 30;

    private FileStream? _stream;
    private long _readPosition;

    private long _lastGrowthUnixSeconds;
    private DateTime _lastWriteUtc;

    private FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _signal;
    private int _wakePending;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _decodeBuffer = new char[8192];
    private char[] _lineBuffer;
    private int _lineBufferLength;

    private bool _disposed;

    public FileTailer(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        //PollIntervalSeconds = Math.Max(1, pollIntervalSeconds);
        _signal = new SemaphoreSlim(0, 1);
        _lineBuffer = ArrayPool<char>.Shared.Rent(1024);
        _lineBufferLength = 0;
        _lastGrowthUnixSeconds = CommonHelper.TimestampUtc;
        _lastWriteUtc = DateTime.MinValue;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            SetupWatcher();
        }
        catch (Exception ex)
        {
            CommonHelper.WriteLine($"SetupWatcher failed: {ex.Message}");
        }

        SignalWake();
        CommonHelper.WriteLine("Monitoring started");
        var buffer = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            if (!File.Exists(_path))
            {
                DisposeStream();
                CommonHelper.WriteWarn($"Log file {_path} does not exist.");
                await WaitSignalOrTimeout(TimeSpan.FromSeconds(PollIntervalSeconds), ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                EnsureStream();
                if (_stream == null)
                {
                    await WaitSignalOrTimeout(TimeSpan.FromSeconds(PollIntervalSeconds), ct).ConfigureAwait(false);
                    continue;
                }

                var len = _stream.Length;
                if (CheckRotation(_readPosition, out var newPos))
                {
                    _readPosition = newPos;
                    await WaitSignalOrTimeout(TimeSpan.FromSeconds(PollIntervalSeconds), ct).ConfigureAwait(false);
                    continue;
                }

                if (len < _readPosition)
                {
                    HandleTruncation();
                    len = _stream.Length;
                    if (_stream == null)
                    {
                        await WaitSignalOrTimeout(TimeSpan.FromSeconds(PollIntervalSeconds), ct).ConfigureAwait(false);
                        continue;
                    }
                }

                if (len == _readPosition)
                {
                    var now = CommonHelper.TimestampUtc;
                    if (now - _lastGrowthUnixSeconds >= IdleThresholdSeconds)
                    { 
                        CommonHelper.WriteWarn($"No new-line in the log file, Is p2pool not running?");
                    }
                    await WaitSignalOrTimeout(TimeSpan.FromSeconds(PollIntervalSeconds), ct).ConfigureAwait(false);
                    continue;
                }

                var drainedAny = await DrainAvailableAsync(buffer, ct).ConfigureAwait(false);
                if (drainedAny)
                {
                    continue;
                }

                await WaitSignalOrTimeout(TimeSpan.FromSeconds(PollIntervalSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CommonHelper.WriteError(ex);
                DisposeStream();
                await WaitSignalOrTimeout(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }

        DisposeWatcher();
        DisposeStream();

        // Return the pooled line buffer and release the wake-up signal.
        ArrayPool<char>.Shared.Return(_lineBuffer);
        _lineBuffer = null!;  
        _signal.Dispose();
    }

    private void SignalWake()
    {
        // Only one pending wakeup is needed. Repeated watcher events should coalesce into a single signal,
        // while timeout remains the safety net when no event arrives.
        try
        {
            if (Interlocked.Exchange(ref _wakePending, 1) == 0)
            {
                try
                {
                    _signal.Release();
                    LogDebug($"[FileTailer] wakeup signal set: path={_path}");
                }
                catch (SemaphoreFullException)
                {
                    LogDebug($"[FileTailer] wakeup already queued: path={_path}");
                }
            }
            else
            {
                LogDebug($"[FileTailer] wakeup already pending: path={_path}");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[FileTailer] wakeup error: path={_path} ex={ex.Message}");
        }
    }

    private bool ConsumePendingWakeup()
    {
        if (Interlocked.Exchange(ref _wakePending, 0) != 1)
        {
            return false;
        }

        while (_signal.CurrentCount > 0)
        {
            try
            {
                _signal.Wait(0);
            }
            catch (Exception)
            {
                break;
            }
        }

        LogDebug($"[FileTailer] wakeup consumed before sleep: path={_path}");
        return true;
    }

    private static void LogDebug(string message)
    {
#if DEBUG
        CommonHelper.WriteLine(message);
#endif
    }

    private void EnsureStream()
    {
        if (_stream != null) return;

        _stream = OpenReadStream(_path);
        if (_stream == null) return;

        CommonHelper.WriteLine($"Log file opened: {_path}");
        _readPosition = _stream.Length;
        _decoder.Reset();
        _lineBufferLength = 0;
        try
        {
            var fi = new FileInfo(_path);
            _lastWriteUtc = fi.LastWriteTimeUtc;
        }
        catch { }

        _lastGrowthUnixSeconds = CommonHelper.TimestampUtc;
    }

    private void HandleTruncation()
    {
        CommonHelper.WriteWarn($"Log file {_path} was truncated or rotated - reopening from the beginning");
        DisposeStream();
        _stream = OpenReadStream(_path);
        _readPosition = 0;
        _lineBufferLength = 0;
        _decoder.Reset();
        _lastGrowthUnixSeconds = CommonHelper.TimestampUtc;
    }

    private async Task<bool> DrainAvailableAsync(byte[] buffer, CancellationToken ct)
    {
        var drainedAny = false;
        while (true)
        {
            var available = _stream!.Length - _readPosition;
            if (available <= 0) break;

            _stream.Seek(_readPosition, SeekOrigin.Begin);
            var toRead = (int)Math.Min(available, buffer.Length);
            var read = await _stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read <= 0) break;

            _readPosition += read;
            ProcessReadChunk(buffer, read);
            _lastGrowthUnixSeconds = CommonHelper.TimestampUtc;
            try { _lastWriteUtc = new FileInfo(_path).LastWriteTimeUtc; } catch { }
            drainedAny = true;
        }

        return drainedAny;
    }

    // ---------------- string processing methods ----------------
    private void ProcessReadChunk(byte[] buffer, int bytesRead)
    {
        var charCount = _decoder.GetCharCount(buffer, 0, bytesRead, flush: false);
        if (charCount <= _decodeBuffer.Length)
        {
            _decoder.GetChars(buffer, 0, bytesRead, _decodeBuffer, 0, flush: false);
            ProcessDecodedChars(_decodeBuffer.AsSpan(0, charCount));
            return;
        }

        var tempBuffer = ArrayPool<char>.Shared.Rent(charCount);
        try
        {
            _decoder.GetChars(buffer, 0, bytesRead, tempBuffer, 0, flush: false);
            ProcessDecodedChars(tempBuffer.AsSpan(0, charCount));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(tempBuffer);
        }
    }

    private void ProcessDecodedChars(ReadOnlySpan<char> decodedChars)
    {
        var span = decodedChars;
        while (!span.IsEmpty)
        {
            var newlineIndex = span.IndexOf('\n');
            if (newlineIndex < 0)
            {
                EnsurePendingCapacity(span.Length);
                span.CopyTo(new Span<char>(_lineBuffer, _lineBufferLength, span.Length));
                _lineBufferLength += span.Length;
                break;
            }

            var segment = span[..newlineIndex];
            if (segment.Length > 0)
            {
                var take = segment.Length;
                if (segment[^1] == '\r') take--;
                if (take > 0)
                {
                    EnsurePendingCapacity(take);
                    segment[..take].CopyTo(new Span<char>(_lineBuffer, _lineBufferLength, take));
                    _lineBufferLength += take;
                }
            }

            var lineSpan = new ReadOnlySpan<char>(_lineBuffer, 0, _lineBufferLength);
            if (!lineSpan.IsEmpty && !IsAllWhiteSpace(lineSpan))
            {
                //CommonHelper.WriteLine(lineSpan.ToString());
                NotifyManager.Handle(lineSpan, Notification.Source.Keywords);
                
            }

            _lineBufferLength = 0;
            span = span[(newlineIndex + 1) ..];
        }
    }

    private void EnsurePendingCapacity(int additional)
    {
        var required = _lineBufferLength + additional;
        if (required <= _lineBuffer.Length) return;
        var newSize = Math.Max(_lineBuffer.Length * 2, required);
        var newBuf = ArrayPool<char>.Shared.Rent(newSize);
        Array.Copy(_lineBuffer, 0, newBuf, 0, _lineBufferLength);
        ArrayPool<char>.Shared.Return(_lineBuffer);
        _lineBuffer = newBuf;
    }

    private static bool IsAllWhiteSpace(ReadOnlySpan<char> span)
    {
        for (var i = 0; i < span.Length; i++) if (!char.IsWhiteSpace(span[i])) return false;
        return true;
    }

    // ---------------- helpers: stream, watcher, rotation ----------------
    private static FileStream OpenReadStream(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
    }

    private void DisposeStream()
    {
        if (_stream != null)
        {
            try { _stream.Dispose(); } catch { }
            _stream = null;
        }
    }

    private bool CheckRotation(long currentReadPos, out long newReadPos)
    {
        newReadPos = currentReadPos;
        try
        {
            var fi = new FileInfo(_path);
            var writeTime = fi.LastWriteTimeUtc;

            if (writeTime <= _lastWriteUtc)
            {
                return false;
            }

            _lastGrowthUnixSeconds = CommonHelper.TimestampUtc;
            _lastWriteUtc = writeTime;

            var currentLength = fi.Length;
            if (currentLength < currentReadPos)
            {
                newReadPos = 0;
            }
            else
            {
                newReadPos = Math.Min(currentReadPos, currentLength);
            }

            // The write time changed, so the file was appended to OR replaced.
            // Re-open the stream in BOTH cases so _stream always tracks the file
            // currently at _path, never a renamed (dead) inode.
            //
            // The empty-file branch used to return WITHOUT re-opening: after a
            // rename-style rotation (new p2pool.log created empty), _stream kept
            // pointing at the old file while newReadPos had already been forced
            // to 0. The next drain then re-read the ENTIRE old log from byte 0
            // and replayed every line - duplicate SHARE FOUND / payout / alert
            // emails - until the new file received its first write.
            DisposeStream();
            _stream = OpenReadStream(_path);
            if (_stream != null)
            {
                try { _lastWriteUtc = new FileInfo(_path).LastWriteTimeUtc; } catch { }
            }

            // When the read position moved backwards, the byte stream is no
            // longer a continuation of what was read before (truncated or
            // replaced file): drop the pending partial line and any buffered
            // partial UTF-8 sequence so the new file starts clean. On a plain
            // append (newReadPos == currentReadPos) the pending line MUST be
            // kept - it belongs to a line that is still being written.
            if (newReadPos != currentReadPos)
            {
                _decoder.Reset();
                _lineBufferLength = 0;
            }

            return true;
        }
        catch (Exception ex)
        {
            CommonHelper.WriteLine($"CheckRotation error: {ex.Message}");
            return false;
        }
    }

    private void SetupWatcher()
    {
        if (_watcher != null) return;
        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var file = Path.GetFileName(_path);
        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnWatcherError;
    }

    private void DisposeWatcher()
    {
        if (_watcher == null) return;
        try
        {
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch { }
        _watcher = null;
    }

    private void OnChanged(object? s, FileSystemEventArgs e)
    {
        try
        {
            LogDebug($"[FileTailer] watcher changed: path={_path} type={e.ChangeType}");
            SignalWake();
        }
        catch { }
    }

    private void OnRenamed(object? s, RenamedEventArgs e)
    {
        try
        {
            LogDebug($"[FileTailer] watcher renamed: path={_path} old={e.OldName} new={e.Name}");
            SignalWake();
        }
        catch { }
    }

    private void OnWatcherError(object? s, ErrorEventArgs e)
    {
        CommonHelper.WriteLine($"FileSystemWatcher error: {e.GetException()?.Message}");
        SignalWake();
    }

    private async Task WaitSignalOrTimeout(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            LogDebug($"[FileTailer] sleep start: path={_path} timeout={timeout.TotalSeconds}s");

            if (ConsumePendingWakeup())
            {
                return;
            }

            var waitTask = _signal.WaitAsync(ct);
            var completed = await Task.WhenAny(waitTask, Task.Delay(timeout, ct)).ConfigureAwait(false);

            if (completed == waitTask)
            {
                LogDebug($"[FileTailer] wakeup by signal: path={_path} timeout={timeout.TotalSeconds}s");
                await waitTask.ConfigureAwait(false);
                Interlocked.Exchange(ref _wakePending, 0);
            }
            else
            {
                LogDebug($"[FileTailer] wakeup by timeout: path={_path} timeout={timeout.TotalSeconds}s");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { CommonHelper.WriteLine($"WaitSignalOrTimeout error: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeWatcher();
        DisposeStream();
        _signal.Dispose();

        // Return the rented buffer to the pool to prevent memory leak
        if (_lineBuffer != null)
        {
            ArrayPool<char>.Shared.Return(_lineBuffer);
            _lineBuffer = null!;
        }
    }
}

