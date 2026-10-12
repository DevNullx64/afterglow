using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Afterglow.Core.Metrics;

/// <summary>
/// Resolves the process that owns the foreground window, including the UWP case
/// where the foreground window belongs to ApplicationFrameHost and the real app
/// is a child window with a different PID.
/// </summary>
public static unsafe partial class ForegroundProcess
{
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(nint hwndParent, delegate* unmanaged<nint, nint, int> callback, nint lparam);

    /// <summary>EnumChildWindows state, passed to the callback through lparam.</summary>
    private struct ChildSearch
    {
        public uint FramePid;
        public uint ChildPid;
    }

    public static int? GetForegroundProcessId()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (!process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                return (int)pid;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        // UWP: find the child window owned by a different process. EnumChildWindows
        // runs the callback synchronously on this thread, so the search state can
        // live on this stack frame and travel by address — no delegate, no GC handle.
        var search = new ChildSearch { FramePid = pid };
        _ = EnumChildWindows(hwnd, &FindForeignChild, (nint)(&search));
        return search.ChildPid != 0 ? (int)search.ChildPid : (int)pid;
    }

    /// <summary>WNDENUMPROC: stops at the first child owned by a process other than the frame host.</summary>
    [UnmanagedCallersOnly]
    private static int FindForeignChild(nint child, nint lparam)
    {
        var search = (ChildSearch*)lparam;
        _ = GetWindowThreadProcessId(child, out uint candidate);
        if (candidate != 0 && candidate != search->FramePid)
        {
            search->ChildPid = candidate;
            return 0;   // FALSE: stop enumerating
        }

        return 1;   // TRUE: continue
    }
}
