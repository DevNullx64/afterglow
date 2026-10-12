using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Delivery of GpuTuner.StateWritten — the notification the Tuning and V/F
/// pages re-read on (F-08, F-75). It fires from hardware-write paths,
/// including the TDR and panic resets, so delivery must reach every page and
/// a failing page must never surface in the code that just wrote the GPU.
/// (The page wiring itself is WPF and has no test harness in this repo.)
/// </summary>
[Collection("AppPaths")]
public sealed class WriteNotificationTests : IDisposable
{
    private readonly string _root;

    public WriteNotificationTests()
    {
        // The failing subscriber below gets logged; keep that out of the real log.
        _root = Path.Combine(Path.GetTempPath(), $"afterglow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot = _root;
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Every_subscriber_hears_the_write_even_when_one_throws()
    {
        var heard = new List<string>();
        Action<string>? handlers = null;
        handlers += sender => heard.Add($"tuning page <- {sender}");
        handlers += _ => throw new InvalidOperationException("a page blew up while refreshing");
        handlers += sender => heard.Add($"v/f page <- {sender}");

        var ex = Record.Exception(() => WriteNotification.Raise(handlers, "gpu0"));

        Assert.Null(ex); // the writer never sees a subscriber's failure
        Assert.Equal(["tuning page <- gpu0", "v/f page <- gpu0"], heard);
    }

    [Fact]
    public void No_subscribers_is_a_no_op()
    {
        Assert.Null(Record.Exception(() => WriteNotification.Raise<string>(null, "gpu0")));
    }
}
