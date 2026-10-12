using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace Afterglow.App.Services;

/// <summary>
/// Outcome of <see cref="StartupTaskService.Enable"/>. <see cref="Error"/> is
/// null only when the task actually exists afterwards; otherwise it says why
/// not, in words the user can act on.
/// </summary>
public sealed record StartupTaskResult(string? Error)
{
    public bool Created => Error is null;
}

/// <summary>
/// Manages the "Afterglow" Task Scheduler entry that starts the app at logon
/// elevated and without a UAC prompt. A classic Run-key autostart would launch
/// unelevated and re-prompt at every boot; the scheduled task is the standard
/// way resident tuning tools avoid that. Create/delete require the elevated
/// app; querying works from any context.
/// </summary>
public static class StartupTaskService
{
    private const string TaskName = "Afterglow";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>
    /// The executable the registered logon task actually launches, or null when
    /// there is no task (or its definition can't be read). This — not the exe
    /// that happens to be running — is what the elevated-autostart location
    /// check must judge: a dev build or a second copy elsewhere says nothing
    /// about the binary that will start with admin rights at logon.
    /// </summary>
    public static string? RegisteredExecutable()
    {
        if (RunSchtasks($"/Query /TN \"{TaskName}\" /XML", out string xml) != 0)
        {
            return null;
        }

        return Core.Security.LogonTaskDefinition.ParseCommand(xml);
    }

    /// <summary>
    /// Refuses unless the exe lives somewhere only administrators can change.
    /// The task starts Afterglow elevated with no consent prompt, so whatever
    /// can replace the exe inherits that at every logon — and the portable build
    /// usually sits in a user-writable folder, where that is a real privilege
    /// crossing rather than a theoretical one.
    /// </summary>
    public static StartupTaskResult Enable()
    {
        string? exe = Environment.ProcessPath;
        using var identity = WindowsIdentity.GetCurrent();
        string? sid = identity.User?.Value;
        if (exe is null || sid is null)
        {
            return new StartupTaskResult("Afterglow couldn't identify its own executable or user account.");
        }

        var trust = Core.Security.TrustedInstallLocation.Check(exe);
        if (!trust.IsTrusted)
        {
            return new StartupTaskResult(
                $"Afterglow won't create an elevated logon task from here: {trust.Reason}. The task would start " +
                "Afterglow with administrator rights and no UAC prompt at every logon, so anything able to replace " +
                "the exe would get those rights too. Install into Program Files (run the setup .exe) and switch " +
                "this on from there.");
        }

        // Stage the task XML in a freshly created, administrators-only directory
        // under %WINDIR%\Temp instead of the old %TEMP%\afterglow-task-<pid>.xml.
        // The old location was writable by the same user's unelevated processes
        // under a fully predictable name, so a medium-IL process could pre-create
        // or swap the file between our write and the elevated schtasks reading it
        // (TOCTOU, F-33). Here the directory name is random, its DACL grants only
        // SYSTEM + Administrators, and we hold the file open (FileShare.Read) for
        // the whole schtasks call, so there is no window in which anything the
        // standard user controls can influence what schtasks consumes.
        string stagingRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        string stagingDir = Path.Combine(stagingRoot, "afterglow-task-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!Core.Security.ProtectedDirectory.TryCreateExclusive(stagingDir, allowUsersRead: false))
            {
                return new StartupTaskResult(
                    "Afterglow couldn't create a private directory to stage the startup task definition.");
            }

            string xmlPath = Path.Combine(stagingDir, "task.xml");
            using var stream = new FileStream(xmlPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, System.Text.Encoding.Unicode);
            writer.Write(Core.Security.LogonTaskDefinition.BuildXml(exe, sid));
            writer.Flush();
            stream.Flush(flushToDisk: true);

            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F") == 0
                ? new StartupTaskResult(null)
                : new StartupTaskResult("Task Scheduler refused to create the startup task.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StartupTaskResult($"Couldn't stage the startup task definition: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(stagingDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static bool Disable() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    private static int RunSchtasks(string arguments) => RunSchtasks(arguments, out _);

    private static int RunSchtasks(string arguments, out string output)
    {
        output = string.Empty;
        try
        {
            // Full path: the elevated app must not run whatever "schtasks.exe"
            // sits in its own (possibly user-writable) folder or working directory.
            using var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (process is null)
            {
                return -1;
            }

            // Read before waiting: a full pipe would otherwise block the child.
            var reading = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return -1;
            }

            output = reading.Wait(5_000) ? reading.Result : string.Empty;
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
