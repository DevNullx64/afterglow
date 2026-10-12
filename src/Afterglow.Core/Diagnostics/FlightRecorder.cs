using System.Collections.Concurrent;
using System.Globalization;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Diagnostics;

/// <summary>
/// Always-on telemetry black box: appends one compact line per snapshot to a
/// session file, plus event markers (applied offsets, stress runs, clean
/// shutdown). After a hard crash the file ends mid-flight — the missing
/// clean-shutdown marker plus the last recorded seconds are what
/// <see cref="CrashForensics"/> reconstructs the death from.
/// Costs ~50 bytes/second. The file is capped at <see cref="SegmentBytes"/>:
/// a long session rolls into a second segment, so disk use stays bounded
/// (four files at most: this session's two segments and the previous
/// session's) instead of growing ~4 MB a day for as long as the PC stays up.
/// Lines are written by a background thread, so a stalled disk can never
/// hold up the telemetry tick that drives the fan curve.
/// </summary>
public sealed class FlightRecorder : IDisposable
{
    /// <summary>Roll the session file over at this size (~10 hours at 1 s polling).</summary>
    public const long SegmentBytes = 4 * 1024 * 1024;

    /// <summary>Lines buffered while the disk is stalled; beyond this the newest are dropped.</summary>
    private const int QueueCapacity = 4096;

    private readonly BlockingCollection<string> _queue = new(QueueCapacity);
    private readonly Thread _writerThread;
    private readonly string _olderPath;
    private readonly long _segmentBytes;
    private StreamWriter _writer;
    private long _bytes;

    // Last offsets marker text (writer thread only): re-emitted at the top of a
    // new segment, because the analyzer reads the final segment alone and the
    // offsets marker is deduplicated — without the carry-over a crash ten
    // hours into a session would be analyzed as "at stock".
    private string? _offsetsMarker;

    private int _lastCoreOffset = int.MinValue;
    private int _lastMemOffset = int.MinValue;
    private int _disposed;

    public string CurrentPath { get; }

    public FlightRecorder(string directory)
        : this(directory, SegmentBytes)
    {
    }

    /// <summary>Test seam: a smaller segment so a roll-over can be exercised quickly.</summary>
    internal FlightRecorder(string directory, long segmentBytes)
    {
        _segmentBytes = segmentBytes;
        Directory.CreateDirectory(directory);
        CurrentPath = Path.Combine(directory, "current.log");
        _olderPath = Path.Combine(directory, "current.older.log");

        // The previous session becomes previous*.log. A rename, not a copy:
        // the old copy-then-delete rewrote the whole file at every logon.
        Rotate(CurrentPath, Path.Combine(directory, "previous.log"), dropStale: false);
        Rotate(_olderPath, Path.Combine(directory, "previous.older.log"), dropStale: true);

        _writer = Open(CurrentPath);
        Enqueue($"#flight v1 started={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

        _writerThread = new Thread(WriteLoop)
        {
            Name = "Afterglow flight recorder",
            IsBackground = true,
        };
        _writerThread.Start();
    }

    public void Record(GpuSnapshot snapshot) => Enqueue(FormatLine(snapshot));

    /// <summary>Logs the currently applied offsets; deduplicated, so call freely.</summary>
    public void RecordOffsets(int coreOffsetMHz, int memOffsetMHz)
    {
        if (coreOffsetMHz == _lastCoreOffset && memOffsetMHz == _lastMemOffset)
        {
            return;
        }

        _lastCoreOffset = coreOffsetMHz;
        _lastMemOffset = memOffsetMHz;
        Marker($"offsets core={coreOffsetMHz} mem={memOffsetMHz}");
    }

    public void Marker(string text) => Enqueue(MarkerLine(text));

    internal static string FormatLine(GpuSnapshot s) => string.Create(
        CultureInfo.InvariantCulture,
        $"{s.Timestamp.ToUnixTimeMilliseconds()}|{s.CoreClockMHz}|{s.MemClockMHz}|{s.GpuTempC}|" +
        $"{s.MemJunctionTempC:F0}|{s.PowerW:F0}|{s.GpuUtilPct}|{s.CoreVoltageMv:F0}|{s.MaxFanPercent}");

    private static string MarkerLine(string text) => string.Create(
        CultureInfo.InvariantCulture, $"#{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {text}");

    private void Enqueue(string line)
    {
        // A full queue means the disk has been stalled for over an hour; drop
        // rather than block the caller (the telemetry thread).
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _ = _queue.TryAdd(line);
            }
            catch (InvalidOperationException)
            {
                // Completed by a concurrent Dispose.
            }
        }
    }

    private void WriteLoop()
    {
        foreach (string line in _queue.GetConsumingEnumerable())
        {
            Write(line);
        }
    }

    private void Write(string line)
    {
        try
        {
            if (_bytes > _segmentBytes)
            {
                RollSegment();
            }

            _writer.WriteLine(line);
            _bytes += line.Length + Environment.NewLine.Length;
            if (line.Length > 1 && line[0] == '#' &&
                line.IndexOf(' ', StringComparison.Ordinal) is int space and > 0 &&
                line.AsSpan(space + 1).StartsWith("offsets ", StringComparison.Ordinal))
            {
                _offsetsMarker = line[(space + 1)..];
            }
        }
        catch (IOException)
        {
            // A full or vanished disk must not take the recorder thread down.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RollSegment()
    {
        _writer.Dispose();
        Rotate(CurrentPath, _olderPath, dropStale: false);
        _writer = Open(CurrentPath);
        _bytes = 0;
        Write($"#flight v1 continued={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        if (_offsetsMarker is not null)
        {
            Write(MarkerLine(_offsetsMarker));
        }
    }

    private static StreamWriter Open(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

    private static void Rotate(string from, string to, bool dropStale)
    {
        try
        {
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
            else if (dropStale && File.Exists(to))
            {
                // The last session never rolled over, so an older segment left
                // from an even earlier one would sit beside a previous.log it
                // does not belong to.
                File.Delete(to);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The clean-shutdown marker goes in last, after everything queued, so
        // its presence still means "the session ended on purpose".
        _ = _queue.TryAdd(MarkerLine("clean-shutdown"));
        _queue.CompleteAdding();
        if (!_writerThread.Join(TimeSpan.FromSeconds(3)))
        {
            // The disk is hung; the writer still owns the stream. Leave it —
            // the process is exiting and a background thread dies with it.
            return;
        }

        _writer.Dispose();
        _queue.Dispose();
    }
}
