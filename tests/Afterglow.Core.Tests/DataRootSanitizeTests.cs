using System.Diagnostics;
using Afterglow.Core.Security;

namespace Afterglow.Core.Tests;

/// <summary>
/// The tree walk that runs after the data root is locked down. The re-owning
/// step needs an elevated process, so it is injected here; what is checked is
/// which items the walk removes, re-owns, or moves aside — on a real directory
/// junction, the case that turns the elevated log/flight writers into an
/// arbitrary-write primitive.
/// </summary>
public sealed class DataRootSanitizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "afterglow-sanitize-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "afterglow-outside-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (string dir in new[] { _root, _outside })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // Junctions need no privilege (unlike symbolic links), which is exactly
        // why a standard user can plant one.
        using var process = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Links_are_removed_without_touching_their_target_and_planted_files_are_moved_aside()
    {
        Directory.CreateDirectory(Path.Combine(_root, "profiles"));
        File.WriteAllText(Path.Combine(_root, "profiles", "Daily.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "profiles", "planted.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "precious.txt"), "do not touch");
        CreateJunction(Path.Combine(_root, "logs"), _outside);

        var inherited = new List<string>();
        DataRootProtection.Sanitize(
            new DirectoryInfo(_root),
            item => inherited.Add(Path.GetRelativePath(_root, item.FullName)),
            file => !file.Name.StartsWith("planted", StringComparison.Ordinal));

        // The junction is gone; what it pointed at is intact.
        Assert.False(Directory.Exists(Path.Combine(_root, "logs")));
        Assert.Equal("do not touch", File.ReadAllText(Path.Combine(_outside, "precious.txt")));

        // The planted file is kept for inspection under a name nothing loads.
        Assert.False(File.Exists(Path.Combine(_root, "profiles", "planted.json")));
        Assert.True(File.Exists(Path.Combine(_root, "profiles", "planted.json.untrusted")));

        // Real folders and trusted files are re-owned; the planted file and the link are not.
        Assert.Contains("profiles", inherited);
        Assert.Contains(Path.Combine("profiles", "Daily.json"), inherited);
        Assert.Contains("settings.json", inherited);
        Assert.DoesNotContain(inherited, i => i.Contains("planted", StringComparison.Ordinal));
        Assert.DoesNotContain(inherited, i => i.StartsWith("logs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failure_on_one_item_does_not_stop_the_walk()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        var seen = new List<string>();

        DataRootProtection.Sanitize(
            new DirectoryInfo(_root),
            item =>
            {
                seen.Add(item.Name);
                if (item.Name == "a")
                {
                    throw new UnauthorizedAccessException("simulated");
                }
            },
            _ => true);

        Assert.Equal(["a", "b"], seen.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Writing_through_a_rename_leaves_no_temp_file()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "old");

        DataRootProtection.WriteAllTextAtomic(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
