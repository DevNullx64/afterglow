using System.Security.AccessControl;
using System.Security.Principal;
using Afterglow.Core.Diagnostics;

namespace Afterglow.Core.Security;

/// <summary>
/// Hardens the machine-wide data root (<c>%ProgramData%\Afterglow</c>) against a
/// standard user influencing the elevated app's GPU writes or file operations.
///
/// <para>ProgramData inherits <c>BUILTIN\Users:(CI)(WD,AD)</c>, so a non-admin can
/// create files and folders under the Afterglow folder — plant a
/// <c>settings.json</c> with an auto-apply startup profile and a "marked stable"
/// profile, or turn a folder the elevated app writes into (logs, flight) into a
/// junction pointing elsewhere — before or between runs. When elevated, on the
/// default root only, this:</para>
/// <list type="number">
/// <item>refuses a root that is itself a link (removing the link, never touching
/// its target) and locks the root to SYSTEM + Administrators, Users read-only;</item>
/// <item>walks the tree without following links: removes any link inside it,
/// re-owns every folder and trusted file to Administrators with inherit-only
/// permissions (a folder or file keeps its creator's implicit WRITE_DAC
/// otherwise, and any extra grant on it), and moves files owned by another
/// standard user aside as <c>*.untrusted</c> — kept for inspection, never loaded,
/// never re-owned;</item>
/// <item>checks the owner of every settings/profile file it reads from the OPEN
/// handle, so the check and the read are the same file.</item>
/// </list>
///
/// <para>Unelevated instances never change an ACL and never refuse a read, so
/// monitoring-only mode and the unelevated CLI keep working (writes they can no
/// longer make degrade with a message). A <c>--data-dir</c> root is never
/// touched. What this does NOT cover: code already running as the same
/// administrator user, unelevated (Windows' UAC same-user boundary).</para>
/// </summary>
public static class DataRootProtection
{
    private static readonly Lock Gate = new();
    private static bool _hardenAttempted;

    /// <summary>
    /// True once an elevated process has decided to enforce the owner check. When
    /// false (unelevated, --data-dir, or never initialised) <see cref="MayLoad"/>
    /// is a no-op, so reads always succeed.
    /// </summary>
    public static bool EnforceTrustedOwner { get; private set; }

    /// <summary>
    /// Idempotent. On the first elevated, non-redirected call: turn on the
    /// load-time owner check, lock the data root and sanitise the tree below it.
    /// Does nothing when unelevated or when the root is a <c>--data-dir</c>
    /// override. ACL failures are logged, not thrown — the owner check still
    /// stands, and every item is attempted independently.
    /// </summary>
    public static void EnsureHardened()
    {
        lock (Gate)
        {
            if (_hardenAttempted)
            {
                return;
            }

            _hardenAttempted = true;

            // A side-by-side/test instance may point --data-dir anywhere; never
            // touch its ACLs and never gate its reads.
            if (AppPaths.IsRedirected || !IsElevated())
            {
                return;
            }

            // Set the flag before the ACL work: even if hardening fails
            // (insufficient privilege, a locked file), the owner check applies.
            EnforceTrustedOwner = true;

            try
            {
                var root = new DirectoryInfo(AppPaths.Root);
                if (root.Exists && root.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Applying an ACL by name would follow the link and rewrite
                    // the permissions of whatever folder it points at; writing
                    // state through it would land there too. Remove the link
                    // itself (its target is untouched) and use a real folder.
                    Directory.Delete(root.FullName);
                    Log.Warn($"{root.FullName} was a link to another folder; removed the link and recreated the data folder.");
                }

                Directory.CreateDirectory(AppPaths.Root);
                ProtectedDirectory.Apply(AppPaths.Root, allowUsersRead: true);
                Sanitize(new DirectoryInfo(AppPaths.Root), ProtectedDirectory.Inherit, IsOwnerTrustedForElevatedLoad);

                // Create the standard folders now, as the elevated owner, so an
                // unelevated instance never needs to (it no longer can).
                AppPaths.EnsureCreated();
                Directory.CreateDirectory(AppPaths.FlightDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       PrivilegeNotHeldException or System.Security.SecurityException or
                                       InvalidOperationException)
            {
                Log.Warn($"Could not lock down {AppPaths.Root} ({ex.GetType().Name}: {ex.Message}); " +
                         "load-time owner checks still apply.");
            }
        }
    }

    /// <summary>
    /// Walks <paramref name="directory"/> without following links: removes every
    /// link (directory junctions/symlinks and file symlinks — the link only),
    /// recurses into real folders, applies <paramref name="inherit"/> to real
    /// folders and to files whose owner <paramref name="ownerTrusted"/> accepts,
    /// and renames the rest to <c>*.untrusted</c>. Each item is handled on its
    /// own: one failure is logged and the walk continues.
    /// </summary>
    internal static void Sanitize(DirectoryInfo directory, Action<FileSystemInfo> inherit, Func<FileInfo, bool> ownerTrusted)
    {
        FileSystemInfo[] items;
        try
        {
            items = directory.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not list {directory.FullName}: {ex.Message}");
            return;
        }

        foreach (var item in items)
        {
            try
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // A link here would let the elevated app's writes (logs,
                    // flight recordings, rotations) land wherever it points.
                    if (item is DirectoryInfo)
                    {
                        Directory.Delete(item.FullName);
                    }
                    else
                    {
                        File.Delete(item.FullName);
                    }

                    Log.Warn($"Removed link {item.FullName} from the data folder.");
                }
                else if (item is DirectoryInfo sub)
                {
                    inherit(sub);
                    Sanitize(sub, inherit, ownerTrusted);
                }
                else if (item is FileInfo file && !file.Name.EndsWith(".untrusted", StringComparison.OrdinalIgnoreCase))
                {
                    if (ownerTrusted(file))
                    {
                        inherit(file);
                    }
                    else
                    {
                        string aside = file.FullName + ".untrusted";
                        File.Move(file.FullName, aside, overwrite: true);
                        Log.Warn($"{file.FullName} is owned by another user; moved aside to {aside} and not loaded.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       PrivilegeNotHeldException or System.Security.SecurityException or
                                       InvalidOperationException)
            {
                Log.Warn($"Could not secure {item.FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Pure decision: an owner is trusted for an elevated load when it is a
    /// well-known elevated principal or the current user themselves. A standard
    /// user's pre-planted file is owned by neither, so it is refused.
    /// </summary>
    public static bool IsOwnerTrusted(SecurityIdentifier owner, SecurityIdentifier currentUser) =>
        TrustedInstallLocation.IsTrustedPrincipal(owner) || owner.Equals(currentUser);

    /// <summary>
    /// Whether the elevated app may load <paramref name="path"/> (by path, for
    /// callers that only need a yes/no). Prefer <see cref="TryReadAllText"/> for
    /// actually reading: it checks the open handle. Always true unless
    /// <see cref="EnforceTrustedOwner"/> is on. Never throws.
    /// </summary>
    public static bool MayLoad(string path)
    {
        if (!EnforceTrustedOwner || !File.Exists(path))
        {
            return true;
        }

        return IsOwnerTrustedForElevatedLoad(new FileInfo(path));
    }

    /// <summary>
    /// Reads a settings/profile file, refusing it (null) when it is missing or,
    /// while <see cref="EnforceTrustedOwner"/> is on, owned by an untrusted
    /// principal. The owner is read from the same open handle the content comes
    /// from, and the handle denies writers, so the file can't be swapped or
    /// edited between the check and the read.
    /// </summary>
    public static string? TryReadAllText(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (EnforceTrustedOwner && !IsHandleOwnerTrusted(stream, path))
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> through a
    /// temporary file and a rename. A rename (not File.Replace) so the result
    /// carries the folder's inherited permissions — File.Replace copies the
    /// REPLACED file's DACL, which would carry a planter's grants onto a file
    /// the owner check then trusts. A target the owner check refuses is moved
    /// aside as <c>*.untrusted</c> first, so whatever it held is kept for
    /// inspection rather than overwritten.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        if (EnforceTrustedOwner && File.Exists(path) && !MayLoad(path))
        {
            File.Move(path, path + ".untrusted", overwrite: true);
            Log.Warn($"{path} was owned by another user; moved aside to {path}.untrusted before saving.");
        }

        File.Move(temp, path, overwrite: true);
    }

    private static bool IsOwnerTrustedForElevatedLoad(FileInfo file)
    {
        try
        {
            var security = file.GetAccessControl(AccessControlSections.Owner);
            return IsTrustedOwnerSecurity(security, file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or InvalidOperationException or
                                   ArgumentException)
        {
            Log.Warn($"Refusing to load {file.FullName}: its permissions could not be read ({ex.GetType().Name}).");
            return false;
        }
    }

    private static bool IsHandleOwnerTrusted(FileStream stream, string path)
    {
        try
        {
            return IsTrustedOwnerSecurity(stream.GetAccessControl(), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or InvalidOperationException)
        {
            Log.Warn($"Refusing to load {path}: its permissions could not be read ({ex.GetType().Name}).");
            return false;
        }
    }

    private static bool IsTrustedOwnerSecurity(FileSystemSecurity security, string path)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
        {
            Log.Warn($"Refusing to load {path}: its owner could not be read while elevated.");
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } currentUser)
        {
            return false;
        }

        if (IsOwnerTrusted(owner, currentUser))
        {
            return true;
        }

        Log.Warn($"Refusing to load {path}: owned by {TrustedInstallLocation.Describe(owner)}, " +
                 "which is neither an administrator/SYSTEM nor the current user — it may have been " +
                 "planted by a standard user.");
        return false;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Test seam: drive <see cref="MayLoad"/> without an elevated process or real ProgramData.</summary>
    internal static void SetEnforceForTests(bool value)
    {
        EnforceTrustedOwner = value;
        _hardenAttempted = true;
    }

    /// <summary>Test seam: forget the one-shot latch so a later real call can run.</summary>
    internal static void ResetForTests()
    {
        EnforceTrustedOwner = false;
        _hardenAttempted = false;
    }
}
