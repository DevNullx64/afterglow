using System.Text;

namespace Afterglow.Core;

/// <summary>Command-line helpers shared by the app and the CLI.</summary>
public static class CommandLine
{
    /// <summary>
    /// Re-joins parsed arguments into one Windows command line that parses back
    /// to the same array (CommandLineToArgvW rules). A plain space join splits a
    /// path such as <c>--data-dir "C:\My Tests"</c> in two when the app
    /// relaunches itself elevated.
    /// </summary>
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    internal static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            return arg;
        }

        var sb = new StringBuilder(arg.Length + 2).Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless they precede a quote: double the
            // run, then escape the quote itself.
            sb.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        // A trailing run precedes the closing quote, so it doubles too.
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>Value following <paramref name="name"/> (case-insensitive), or null.</summary>
    public static string? ValueOf(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Applies <c>--data-dir PATH</c> when present and returns the arguments
    /// without it, so commands that parse positionally never see it.
    /// </summary>
    public static string[] ApplyDataDir(string[] args)
    {
        int at = Array.FindIndex(args, a => a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return args;
        }

        if (at == args.Length - 1)
        {
            throw new ArgumentException("--data-dir needs a folder path.");
        }

        AppPaths.UseDataRoot(args[at + 1]);
        return [.. args[..at], .. args[(at + 2)..]];
    }
}
