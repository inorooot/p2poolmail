// Copyright (c) 2026 inorooot. MIT License.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Buffers;
using System.Text.Json;

namespace p2poolmail;

internal class CommonHelper
{
    // Blittable-only DllImports (byte instead of bool) so they compile as direct
    // P/Invokes under Native AOT without requiring AllowUnsafeBlocks.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern byte GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern byte SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>
    /// Enable ANSI escape-sequence processing on the Windows console (conhost).
    /// Without this, classic cmd/conhost windows print the raw sequences as
    /// garbage (e.g. "←[38;5;167m") instead of applying colors.
    /// </summary>
    private static bool TryEnableWindowsVirtualTerminal()
    {
        try
        {
            bool allEnabled = true;
            foreach (var stdHandle in stackalloc int[] { StdOutputHandle, StdErrorHandle })
            {
                var handle = GetStdHandle(stdHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    allEnabled = false;
                    continue;
                }

                if (GetConsoleMode(handle, out uint mode) == 0)
                {
                    allEnabled = false;
                    continue;
                }

                if ((mode & EnableVirtualTerminalProcessing) != 0)
                    continue; // already enabled (e.g. Windows Terminal)

                if (SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing) == 0)
                    allEnabled = false;
            }

            return allEnabled;
        }
        catch
        {
            return false;
        }
    }

    static CommonHelper()
    {
        if (OperatingSystem.IsWindows())
        {
            // If VT processing cannot be enabled (legacy Windows), drop colors
            // entirely so escape sequences never pollute the console output.
            bool vtEnabled = TryEnableWindowsVirtualTerminal();
            ColorStdout = !Console.IsOutputRedirected && vtEnabled;
            ColorStderr = !Console.IsErrorRedirected && vtEnabled;
        }
        else
        {
            ColorStdout = !Console.IsOutputRedirected;
            ColorStderr = !Console.IsErrorRedirected;
        }
    }

    #region read json field use Utf8JsonReader
     
    public static bool TryReadJsonField(string json, string fieldName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        var utf8 = Encoding.UTF8.GetBytes(json);
        return TryReadJsonField(utf8, fieldName, out value);
    }

    /// <summary>
    /// Read-only span overload accepting char span for JSON input.
    /// Encodes to UTF-8 using ArrayPool to avoid large temporary allocations.
    /// </summary>
    public static bool TryReadJsonField(ReadOnlySpan<char> json, string fieldName, out string value)
    {
        value = string.Empty;
        if (json.IsEmpty || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        var byteCount = Encoding.UTF8.GetByteCount(json);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var span = new Span<byte>(buffer, 0, byteCount);
            Encoding.UTF8.GetBytes(json, span);
            return TryReadJsonField(span, fieldName, out value);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    
    public static bool ReadJsonField(string json, string fieldName, out int value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        var utf8 = Encoding.UTF8.GetBytes(json);
        return TryReadJsonField(utf8, fieldName, out value);
    }

    /// <summary>
    /// Read-only span overload for int field parsing.
    /// </summary>
    public static bool ReadJsonField(ReadOnlySpan<char> json, string fieldName, out int value)
    {
        value = default;
        if (json.IsEmpty || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        var byteCount = Encoding.UTF8.GetByteCount(json);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var span = new Span<byte>(buffer, 0, byteCount);
            Encoding.UTF8.GetBytes(json, span);
            return TryReadJsonField(span, fieldName, out value);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Uses Utf8JsonReader to quickly read a string-array field value from JSON.
    /// Returns string[] directly, without boxing into object.
    /// </summary>
    public static bool TryReadJsonField(string json, string fieldName, out string[] value)
    {
        value = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        var utf8 = Encoding.UTF8.GetBytes(json);
        return TryReadJsonField(utf8, fieldName, out value);
    }

    /// <summary>
    /// Read-only span overload for string[] field parsing.
    /// </summary>
    public static bool TryReadJsonField(ReadOnlySpan<char> json, string fieldName, out string[] value)
    {
        value = Array.Empty<string>();
        if (json.IsEmpty || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        var byteCount = Encoding.UTF8.GetByteCount(json);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var span = new Span<byte>(buffer, 0, byteCount);
            Encoding.UTF8.GetBytes(json, span);
            return TryReadJsonField(span, fieldName, out value);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

     
    public static bool TryReadJsonField(ReadOnlySpan<byte> utf8Json, string fieldName, out string value)
    {
        value = string.Empty;
        if (utf8Json.IsEmpty || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(fieldName))
            {
                continue;
            }

            if (!reader.Read())
            {
                return false;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                value = reader.GetString() ?? string.Empty;
                return true;
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                return false;
            }

            return false;
        }

        return false;
    }
 
    public static bool TryReadJsonField(ReadOnlySpan<byte> utf8Json, string fieldName, out int value)
    {
        value = default;
        if (utf8Json.IsEmpty || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(fieldName))
            {
                continue;
            }

            if (!reader.Read())
            {
                return false;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out value))
            {
                return true;
            }

            return false;
        }

        return false;
    }

   
    public static bool TryReadJsonField(ReadOnlySpan<byte> utf8Json, string fieldName, out string[] value)
    {
        value = Array.Empty<string>();
        if (utf8Json.IsEmpty || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(fieldName))
            {
                continue;
            }

            if (!reader.Read())
            {
                return false;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                return false;
            }

            var items = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    value = items.ToArray();
                    return true;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    items.Add(reader.GetString() ?? string.Empty);
                }
            }

            return false;
        }

        return false;
    }
    #endregion
  

    
    [Flags]
    public enum LogParseFields
    {
        None = 0,
        Level = 1 << 0,   // NOTICE / WARNING / ERROR / INFO
        Timestamp = 1 << 1, // 
        Subject = 1 << 2, // module token, e.g. StratumServer / P2Pool
        Content = 1 << 3  // the message payload after the header
    }
 
    public static string ParseLogLine(ReadOnlySpan<char> span, LogParseFields fields)
    {
        if (span.IsEmpty || fields == LogParseFields.None)
            return string.Empty;

        // Expected format: "LEVEL  YYYY-MM-DD HH:mm:ss.ffff SUBJECT CONTENT"
        // Example: "NOTICE  2026-08-04 10:22:26.0477 StratumServer SHARE FOUND: ..."

        // Step 1: Extract Level (first token before double-space)
        int levelEnd = span.IndexOf("  ");
        if (levelEnd < 0)
            return string.Empty;

        var level = span.Slice(0, levelEnd).Trim().ToString();

        // Step 2: After the double-space, find the timestamp (ends at the Subject token)
        var afterLevel = span.Slice(levelEnd + 2).TrimStart();

        // Timestamp format: "YYYY-MM-DD HH:mm:ss.ffff" (23 chars + space)
        int dateSpace = afterLevel.IndexOf(' ');
        if (dateSpace < 0)
            return string.Empty;

        var afterDate = afterLevel.Slice(dateSpace + 1);
        int timeEnd = afterDate.IndexOf(' ');
        if (timeEnd < 0)
            return string.Empty;

        var timestamp = afterLevel.Slice(0, dateSpace + 1 + timeEnd).Trim().ToString();

        // Step 3: Subject is the next token after timestamp
        var afterTimestamp = afterDate.Slice(timeEnd + 1).TrimStart();
        int subjectEnd = afterTimestamp.IndexOf(' ');
        
        string subject;
        string content;
        
        if (subjectEnd < 0)
        {
            // No content after subject
            subject = afterTimestamp.ToString();
            content = string.Empty;
        }
        else
        {
            subject = afterTimestamp.Slice(0, subjectEnd).ToString();
            content = afterTimestamp.Slice(subjectEnd + 1).Trim().ToString();
        }

        // Step 3.5: Mask wallet addresses in content if this is a payout-related message
        if (fields.HasFlag(LogParseFields.Content) && !string.IsNullOrEmpty(content))
        {
            content = MaskWalletAddresses(content);
        }

        // Step 4: Build result based on requested fields
        var sb = new StringBuilder();
        bool hasPrev = false;

        if (fields.HasFlag(LogParseFields.Level))
        {
            sb.Append(level);
            hasPrev = true;
        }

        if (fields.HasFlag(LogParseFields.Timestamp))
        {
            if (hasPrev) sb.Append(' ');
            sb.Append(timestamp);
            hasPrev = true;
        }

        if (fields.HasFlag(LogParseFields.Subject))
        {
            if (hasPrev) sb.Append(' ');
            sb.Append(subject);
            hasPrev = true;
        }

        if (fields.HasFlag(LogParseFields.Content))
        {
            if (hasPrev) sb.Append(' ');
            sb.Append(content);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Masks Monero wallet addresses in the content string.
    /// Monero addresses are 95 (standard) or 106 (integrated) base58 characters.
    /// Optimized for performance: uses Span-based search and pre-calculates StringBuilder capacity.
    /// </summary>
    private static string MaskWalletAddresses(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // Quick check: if no "Your wallet " pattern exists, return original immediately
        if (!content.Contains("Your wallet ", StringComparison.Ordinal))
            return content;

        const string walletPrefix = "Your wallet ";
        var span = content.AsSpan();
        
        // Pre-calculate worst-case capacity to avoid StringBuilder resizing
        var result = new StringBuilder(content.Length);
        int searchStart = 0;

        while (searchStart < span.Length)
        {
            // Find next occurrence of "Your wallet "
            int prefixIdx = span.Slice(searchStart).IndexOf(walletPrefix.AsSpan());
            
            if (prefixIdx < 0)
            {
                // No more occurrences - append remainder and exit
                if (searchStart < span.Length)
                    result.Append(span.Slice(searchStart));
                break;
            }

            // Calculate absolute position of prefix
            int absPrefixIdx = searchStart + prefixIdx;
            
            // Append content up to and including "Your wallet "
            result.Append(span.Slice(searchStart, prefixIdx + walletPrefix.Length));

            // Move past the prefix
            int addressStart = absPrefixIdx + walletPrefix.Length;
            
            // Find end of wallet address (next space or end of string)
            int addressEnd = addressStart;
            while (addressEnd < span.Length && span[addressEnd] != ' ')
                addressEnd++;

            int addressLen = addressEnd - addressStart;
            
            // Mask the address: show first 4 and last 4 characters
            if (addressLen > 8)
            {
                result.Append(span.Slice(addressStart, 4));
                result.Append("***");
                result.Append(span.Slice(addressEnd - 4, 4));
            }
            else if (addressLen > 0)
            {
                // Very short token, mask entirely
                result.Append("***");
            }

            searchStart = addressEnd;
        }

        return result.ToString();
    }

    
    private const string ColorRed = "\x1b[38;5;167m";
    private const string ColorGreen = "\x1b[38;5;113m";
    private const string ColorYellow = "\x1b[38;5;214m";
    private const string ColorCyan = "\x1b[38;5;109m";
    private const string ColorGray = "\x1b[38;5;240m";
    private const string ColorViolet = "\x1b[38;5;147m";
    private const string ColorRose = "\x1b[38;5;211m";
    private const string ColorTeal = "\x1b[38;5;31m";
    private const string ColorAmber = "\x1b[38;5;180m";
    private const string ColorLime = "\x1b[38;5;150m";
    private const string ColorReset = "\x1b[0m";

    private static readonly object LogWriteLock = new();
    private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "p2poolmail.log");
    private static StreamWriter? _logWriter;

    private static string FormatLogLine(string level, string message) =>
        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}] {level}: {message}";

    private static void WriteToLogFile(string level, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            lock (LogWriteLock)
            {
                // Keep a long-lived writer open instead of open/append/close on every line,
                // so log writes never block callers on repeated disk I/O.
                try
                {
                    if (_logWriter == null)
                    {
                        var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                        _logWriter = new StreamWriter(stream) { AutoFlush = true };
                    }

                    _logWriter.WriteLine(FormatLogLine(level, message));
                    return;
                }
                catch (IOException)
                {
                    // Writer is broken (e.g. the log file was rotated or deleted): drop it,
                    // then fall back to a single synchronous append below.
                    try { _logWriter?.Dispose(); } catch { }
                    _logWriter = null;
                }

                File.AppendAllText(LogFilePath, FormatLogLine(level, message) + Environment.NewLine);
            }
        }
        catch
        {
            // Ignore file logging errors so runtime output is not blocked.
        }
    }

    // ANSI colors are suppressed when stdout/stderr is redirected (e.g. systemd),
    // so escape sequences never pollute redirected logs.
    // On Windows, colors are only kept if VT processing was enabled successfully
    // (see static constructor) — otherwise the sequences would print as garbage.
    private static readonly bool ColorStdout;
    private static readonly bool ColorStderr;

    private static string FormatConsoleMessage(string color, string level, string message, bool colorEnabled)
    {
        WriteToLogFile(level, message);
        return colorEnabled
            ? $"{color}{FormatLogLine(level, message)}{ColorReset}"
            : FormatLogLine(level, message);
    }

    public static void WriteLine(string message) =>
        Console.WriteLine(FormatConsoleMessage(ColorViolet, "Notice", message, ColorStdout));

    public static void WriteError(string message) =>
        Console.Error.WriteLine(FormatConsoleMessage(ColorRose, "ERROR", message, ColorStderr));

    public static void WriteWarn(string message) =>
        Console.Error.WriteLine(FormatConsoleMessage(ColorAmber, "WARN", message, ColorStderr));

    public static void WriteSuccess(string message) =>
        Console.WriteLine(FormatConsoleMessage(ColorLime, "OK", message, ColorStdout));

    public static void WriteDebug(string message) =>
        Console.WriteLine(FormatConsoleMessage(ColorGray, "DBG", message, ColorStdout));

    public static void WriteError(Exception ex) =>
        Console.Error.WriteLine(FormatConsoleMessage(ColorRed, "ERROR", ex.ToString(), ColorStderr));

    /// <summary>Current time as unix-seconds (UTC).</summary>
    public static long TimestampUtc => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
