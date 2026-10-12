using System.Security.AccessControl;
using System.Security.Principal;

namespace Afterglow.Core.Security;

/// <summary>
/// Builds and applies an "administrators-only" DACL: SYSTEM and the
/// Administrators group get full control, nobody else can write, and the owner
/// is set to Administrators so no standard user keeps the implicit WRITE_DAC
/// that ownership carries. Used both for the data root (which additionally
/// lets Users read, so unelevated monitoring still works) and for the
/// throwaway directory the logon-task XML is staged in (no Users access at
/// all). Applying needs elevation; building the DACL does not, so the shape is
/// unit-testable offline.
/// </summary>
public static class ProtectedDirectory
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private const InheritanceFlags InheritToChildren =
        InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;

    /// <summary>
    /// A fresh DACL granting full control to SYSTEM and Administrators only
    /// (plus read &amp; execute for Users when <paramref name="allowUsersRead"/>),
    /// with inheritance disabled so the inherited "Users may create files" ACE
    /// that ProgramData hands down is dropped, and owner set to Administrators.
    /// </summary>
    public static DirectorySecurity BuildDacl(bool allowUsersRead)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(Administrators);

        security.AddAccessRule(new FileSystemAccessRule(
            System, FileSystemRights.FullControl, InheritToChildren, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators, FileSystemRights.FullControl, InheritToChildren, PropagationFlags.None, AccessControlType.Allow));

        if (allowUsersRead)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                Users, FileSystemRights.ReadAndExecute, InheritToChildren, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Applies <see cref="BuildDacl"/> to an existing directory (DACL + owner).
    /// Propagates to children that still inherit, so an already-populated tree
    /// loses the Users-create right too. Throws on failure — callers decide
    /// whether that is fatal.
    /// </summary>
    public static void Apply(string path, bool allowUsersRead)
    {
        var info = new DirectoryInfo(path);
        info.SetAccessControl(BuildDacl(allowUsersRead));
    }

    /// <summary>
    /// Creates a new directory with the protected DACL in one step, so it never
    /// exists with a permissive default ACL even briefly. Returns false if the
    /// name already exists (a pre-created name is refused rather than reused).
    /// </summary>
    public static bool TryCreateExclusive(string path, bool allowUsersRead)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            return false;
        }

        var info = new DirectoryInfo(path);
        FileSystemAclExtensions.Create(info, BuildDacl(allowUsersRead));
        return true;
    }
}
