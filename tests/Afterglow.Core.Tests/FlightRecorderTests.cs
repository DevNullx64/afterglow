using Afterglow.Core.Diagnostics;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Tests;

public sealed class FlightRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afterglow-flight-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static GpuSnapshot Snap(long ms, uint util = 10) => new()
    {
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms),
        DeviceIndex = 0,
        CoreClockMHz = 2500,
        GpuTempC = 50,
        PowerW = 100,
        GpuUtilPct = util,
    };

    [Fact]
    public void A_disposed_session_ends_with_the_clean_marker_and_reads_as_clean()
    {
        using (var recorder = new FlightRecorder(_dir))
        {
            recorder.RecordOffsets(100, 500);
            recorder.Record(Snap(1_000));
            recorder.Record(Snap(2_000));
        }

        string[] lines = File.ReadAllLines(PathOf("current.log"));
        Assert.StartsWith("#flight v1 started=", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(" clean-shutdown", lines[^1], StringComparison.Ordinal);
        Assert.True(FlightSession.EndedCleanly(PathOf("current.log")));
        Assert.Null(CrashForensics.AnalyzePreviousSession(_dir));
    }

    [Fact]
    public void A_new_session_moves_the_last_one_to_previous()
    {
        using (var first = new FlightRecorder(_dir))
        {
            first.Marker("first-session");
        }

        using (var second = new FlightRecorder(_dir))
        {
            second.Marker("second-session");
        }

        Assert.Contains(File.ReadAllLines(PathOf("previous.log")), l => l.EndsWith(" first-session", StringComparison.Ordinal));
        Assert.Contains(File.ReadAllLines(PathOf("current.log")), l => l.EndsWith(" second-session", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_session_rolls_over_and_restates_the_applied_offsets()
    {
        using (var recorder = new FlightRecorder(_dir, segmentBytes: 2_000))
        {
            recorder.RecordOffsets(75, 2000);
            for (int i = 0; i < 200; i++)
            {
                recorder.Record(Snap(1_000 + (i * 1_000L)));
            }
        }

        // Bounded: the older segment holds the session's start, current.log its end.
        Assert.True(new FileInfo(PathOf("current.log")).Length < 4_000);
        Assert.True(File.Exists(PathOf("current.older.log")));

        // The final segment alone still knows what was applied.
        var tail = FlightSession.Parse(File.ReadAllLines(PathOf("current.log")));
        Assert.Equal(75, tail.CoreOffsetMHz);
        Assert.Equal(2000, tail.MemOffsetMHz);
        Assert.True(tail.CleanShutdown);
    }

    [Fact]
    public void An_older_segment_from_a_session_that_is_not_the_previous_one_is_dropped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf("previous.older.log"), "stale");
        File.WriteAllText(PathOf("current.log"), "#flight v1 started=1\n");

        using (new FlightRecorder(_dir))
        {
        }

        Assert.False(File.Exists(PathOf("previous.older.log")));
        Assert.True(File.Exists(PathOf("previous.log")));
    }

    [Theory]
    [InlineData("#flight v1 started=1\n1000|2500|810|50||100|10||\n")]           // died mid-flight
    [InlineData("#flight v1 started=1\n#1000 clean-shutdown\n1000|25")]           // torn line after a marker
    [InlineData("")]
    public void Unfinished_sessions_are_not_mistaken_for_clean_ones(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf("current.log"), content);
        Assert.False(FlightSession.EndedCleanly(PathOf("current.log")));
    }

    [Fact]
    public void A_huge_legacy_recording_falls_back_to_a_full_read_for_its_offsets()
    {
        // Pre-segment recordings stated offsets once, at the start; a tail-only
        // read of a multi-day file would report stock clocks for a crash at +100/+500.
        Directory.CreateDirectory(_dir);
        string path = PathOf("current.log");
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("#flight v1 started=1");
            writer.WriteLine("#2 offsets core=100 mem=500");
            string filler = new('x', 60);
            long bytes = 0;
            for (long ms = 1_000; bytes < FlightSession.TailBytes + 100_000; ms += 1_000)
            {
                string line = $"{ms}|2500|810|50||100|10||{filler}";
                writer.WriteLine(line);
                bytes += line.Length + 2;
            }
        }

        var session = FlightSession.Load(path);

        Assert.NotNull(session);
        Assert.Equal(100, session.CoreOffsetMHz);
        Assert.Equal(500, session.MemOffsetMHz);
        Assert.False(session.CleanShutdown);
    }

    [Fact]
    public void A_recording_whose_tail_names_the_offsets_is_read_from_the_tail_only()
    {
        Directory.CreateDirectory(_dir);
        string path = PathOf("current.log");
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("#flight v1 started=1");
            writer.WriteLine("#2 offsets core=999 mem=999");
            string filler = new('x', 60);
            long bytes = 0;
            long ms = 1_000;
            for (; bytes < FlightSession.TailBytes + 100_000; ms += 1_000)
            {
                string line = $"{ms}|2500|810|50||100|10||{filler}";
                writer.WriteLine(line);
                bytes += line.Length + 2;
            }

            writer.WriteLine($"#{ms} offsets core=100 mem=500");
        }

        var session = FlightSession.Load(path);

        Assert.NotNull(session);
        Assert.Equal(100, session.CoreOffsetMHz);

        // The head of the file (and its stale 999 marker) was never parsed.
        Assert.DoesNotContain(session.Markers, m => m.Contains("999", StringComparison.Ordinal));
    }
}
