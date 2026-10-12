using System.Security.AccessControl;
using System.Security.Principal;
using Afterglow.Core.Diagnostics;

namespace Afterglow.Core.Security;

/// <summary>
/// Hardens the machine-wide data root (<c>%ProgramData%\Afterglow</c>) against a
/// standard user influencing the elevated app's GPU writes.
///
/// <para>ProgramData inherits <c>BUILTIN\Users:(CI)(WD,AD)</c>, so a non-admin can
/// create files under the Afterglow folder — e.g. plant a <c>settings.json</c>
/// with an auto-apply startup profile, plus a "marked stable" profile — before
/// the app first runs, and the elevated logon task would then trust them. Two
/// layers close that:</para>
/// <list type="number">
/// <item>when running elevated, lock the data root to SYSTEM + Administrators
/// (Users keep read, so unelevated monitoring still works), which stops any
/// future plant; and</item>
/// <item>refuse, at load time, any settings/profile file whose owner is neither
/// a trusted principal (Administrators/SYSTEM/TrustedInstaller) nor the current
/// user — which catches files planted before the lock was applied.</item>
/// </list>
///
/// <para>Both are elevation-gated and skip a <c>--data-dir</c> root entirely: an
/// unelevated instance never changes an ACL and never refuses a read, so
/// monitoring-only mode and the unelevated CLI keep working; the ACL is only
/// ever applied to the default ProgramData root, never to a redirected one.</para>
/// </summary>
public static class DataRootProtection
{
    private static readonly object Gate = new();
    private static bool _hardenAttempted;

    /// <summary>
    /// True once an elevated process has decided to enforce the owner check. When
    /// false (unelevated, --data-dir, or never initialised) <see cref="MayLoad"/>
    /// is a no-op, so reads always succeed.
    /// </summary>
    public static bool EnforceTrustedOwner { get; private set; }

    /// <summary>
    /// Idempotent. On the first elevated, non-redirected call: turn on the
    /// load-time owner check and apply the protected DACL to the data root.
    /// Does nothing when unelevated or when the root is a <c>--data-dir</c>
    /// override. A DACL failure is logged, not thrown — the owner check still
    /// stands as the second layer.
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

            // Set the flag before the ACL work: even if hardening the DACL fails
            // (insufficient privilege, a locked file), the load-time owner check
            // must still apply.
            EnforceTrustedOwner = true;

            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                ProtectedDirectory.Apply(AppPaths.Root, allowUsersRead: true);
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
    /// Pure decision: an owner is trusted for an elevated load when it is a
    /// well-known elevated principal or the current user themselves. A standard
    /// user's pre-planted file is owned by neither, so it is refused.
    /// </summary>
    public static bool IsOwnerTrusted(SecurityIdentifier owner, SecurityIdentifier currentUser) =>
        TrustedInstallLocation.IsTrustedPrincipal(owner) || owner.Equals(currentUser);

    /// <summary>
    /// Whether the elevated app may load <paramref name="path"/>. Always true
    /// unless <see cref="EnforceTrustedOwner"/> is on (i.e. elevated, default
    /// root). A missing file is allowed (nothing to distrust); an unreadable
    /// owner fails closed. Never throws.
    /// </summary>
    public static bool MayLoad(string path)
    {
        if (!EnforceTrustedOwner)
        {
            return true;
        }

        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            var security = new FileSecurity(path, AccessControlSections.Owner);
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
                     "planted by a standard user before the data root was locked down.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or InvalidOperationException or
                                   ArgumentException)
        {
            Log.Warn($"Refusing to load {path}: its permissions could not be read ({ex.GetType().Name}).");
            return false;
        }
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
