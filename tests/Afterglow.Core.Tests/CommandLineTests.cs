using System.Runtime.InteropServices;
using Afterglow.Core;

namespace Afterglow.Core.Tests;

[Collection("AppPaths")]
public sealed partial class CommandLineTests
{
    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgv(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    /// <summary>What the relaunched process will actually see: Windows' own parser.</summary>
    private static string[] ParseLikeWindows(string arguments)
    {
        // argv[0] is parsed by different rules; give it a fixed program name.
        IntPtr argv = CommandLineToArgv("app.exe " + arguments, out int count);
        try
        {
            var result = new string[count - 1];
            for (int i = 1; i < count; i++)
            {
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    public static TheoryData<string[]> RoundTripCases => new()
    {
        { ["--minimized"] },
        { ["--data-dir", @"C:\My Tests\afterglow"] },
        { ["--data-dir", @"C:\trailing\"] },
        { ["--data-dir", @"C:\trailing space\"] },
        { ["--page", ""] },
        { ["say \"hi\"", @"back\\slash""quote", @"a\\\b"] },
        { ["--screenshot", @"D:\shots\x y.png", "--page", "fans"] },
    };

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Join_round_trips_through_the_windows_parser(string[] args) =>
        Assert.Equal(args, ParseLikeWindows(CommandLine.Join(args)));

    [Fact]
    public void Plain_arguments_are_left_unquoted() =>
        Assert.Equal("--page fans --minimized", CommandLine.Join(["--page", "fans", "--minimized"]));

    [Fact]
    public void ApplyDataDir_strips_the_option_and_redirects_the_root()
    {
        string? before = AppPaths.OverrideRoot;
        try
        {
            string[] rest = CommandLine.ApplyDataDir(["monitor", "--data-dir", @"C:\afterglow-test", "--once"]);

            Assert.Equal(["monitor", "--once"], rest);
            Assert.Equal(@"C:\afterglow-test", AppPaths.Root);
            Assert.True(AppPaths.IsRedirected);
            Assert.Matches("^-[0-9A-F]{8}$", AppPaths.InstanceSuffix);
        }
        finally
        {
            AppPaths.OverrideRoot = before;
        }
    }

    [Fact]
    public void ApplyDataDir_without_a_value_is_refused() =>
        Assert.Throws<ArgumentException>(() => CommandLine.ApplyDataDir(["monitor", "--data-dir"]));

    [Fact]
    public void ApplyDataDir_leaves_other_arguments_alone()
    {
        string[] args = ["get", "--gpu", "1"];
        Assert.Same(args, CommandLine.ApplyDataDir(args));
    }
}
