using System.Security.AccessControl;
using System.Security.Principal;
using Afterglow.Core.Security;

namespace Afterglow.Core.Tests;

/// <summary>
/// Pure decision logic for the ProgramData hardening and the logon-task XML
/// escaping (F-33). The ACL application and the elevated load-refusal path need
/// a real elevated process and are covered by manual testing; what is checked
/// here is the shape of the DACL that would be applied and the owner-trust rule
/// that decides which files an elevated load may trust.
/// </summary>
public class DataRootProtectionTests
{
    private const string Administrators = "S-1-5-32-544";
    private const string LocalSystem = "S-1-5-18";
    private const string Users = "S-1-5-32-545";
    private const string TrustedInstaller =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // A random, non-well-known account SID — stands in for a standard user who
    // planted a file before the data root was locked down.
    private const string StrangerUser = "S-1-5-21-1111111111-2222222222-3333333333-1007";

    [Fact]
    public void Protected_dacl_grants_write_only_to_trusted_principals()
    {
        var security = ProtectedDirectory.BuildDacl(allowUsersRead: true);

        Assert.True(security.AreAccessRulesProtected, "inheritance must be disabled so ProgramData's Users:WD is dropped");

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();

        // Reuse the existing trust evaluator: nothing outside SYSTEM/Admins may
        // replace a file here, so no untrusted principal can write.
        Assert.Null(TrustedInstallLocation.FindUntrustedGrant(rules, TrustedInstallLocation.ReplaceRights));

        // Users are present but read-only.
        var usersRule = rules.Single(r => ((SecurityIdentifier)r.IdentityReference).Value == Users);
        Assert.Equal(AccessControlType.Allow, usersRule.AccessControlType);
        Assert.Equal(FileSystemRights.ReadAndExecute, usersRule.FileSystemRights & FileSystemRights.ReadAndExecute);
        Assert.Equal((FileSystemRights)0, usersRule.FileSystemRights & FileSystemRights.Write);

        // Owner is Administrators, so no standard user keeps the implicit WRITE_DAC.
        Assert.Equal(Administrators, ((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!).Value);
    }

    [Fact]
    public void Staging_dacl_gives_users_no_access_at_all()
    {
        var security = ProtectedDirectory.BuildDacl(allowUsersRead: false);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();

        Assert.DoesNotContain(rules, r => ((SecurityIdentifier)r.IdentityReference).Value == Users);
        Assert.Null(TrustedInstallLocation.FindUntrustedGrant(rules, TrustedInstallLocation.ReplaceRights));
    }

    [Theory]
    [InlineData(Administrators)]
    [InlineData(LocalSystem)]
    [InlineData(TrustedInstaller)]
    public void Well_known_elevated_principals_are_trusted_owners(string ownerSid)
    {
        var owner = new SecurityIdentifier(ownerSid);
        var someUser = new SecurityIdentifier(StrangerUser);
        Assert.True(DataRootProtection.IsOwnerTrusted(owner, someUser));
    }

    [Fact]
    public void The_current_user_is_a_trusted_owner_of_their_own_files()
    {
        var me = new SecurityIdentifier(StrangerUser);
        Assert.True(DataRootProtection.IsOwnerTrusted(me, me));
    }

    [Fact]
    public void A_file_owned_by_another_standard_user_is_not_trusted()
    {
        // The planted-file case: owner is some other non-admin account.
        var planter = new SecurityIdentifier(StrangerUser);
        var currentUser = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1008");
        Assert.False(DataRootProtection.IsOwnerTrusted(planter, currentUser));
    }

    [Fact]
    public void MayLoad_is_a_no_op_until_enforcement_is_enabled()
    {
        // Default (unelevated / --data-dir): reads are never refused, so
        // monitoring-only mode and the unelevated CLI keep working.
        Assert.True(DataRootProtection.MayLoad(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())));
    }

    [Fact]
    public void MayLoad_trusts_a_file_the_current_user_owns_even_when_enforcing()
    {
        string file = Path.Combine(Path.GetTempPath(), "afterglow-mayload-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "{}");
        try
        {
            DataRootProtection.SetEnforceForTests(true);
            // The test process owns the file it just created, so an elevated
            // load of the same user's file is trusted; a missing file is allowed.
            Assert.True(DataRootProtection.MayLoad(file));
            Assert.True(DataRootProtection.MayLoad(file + ".missing"));
        }
        finally
        {
            DataRootProtection.ResetForTests();
            File.Delete(file);
        }
    }
}

/// <summary>F-33: the logon-task XML escapes the executable path and SID so neither can inject task content.</summary>
public class LogonTaskDefinitionTests
{
    [Fact]
    public void Build_xml_escapes_a_malicious_executable_path()
    {
        // A path crafted to close <Command> and append a second action.
        const string evil = "C:\\x\"/><Command>C:\\evil.exe</Command><Exec><Command>C:\\evil.exe";
        string xml = LogonTaskDefinition.BuildXml(evil, "S-1-5-21-1-2-3-1001");

        // The injected literal element must not appear unescaped...
        Assert.DoesNotContain("<Command>C:\\evil.exe</Command>", xml, StringComparison.Ordinal);
        // ...it must be entity-escaped instead.
        Assert.Contains("&lt;Command&gt;", xml, StringComparison.Ordinal);
        // Exactly one real <Command> element survives.
        Assert.Equal(1, CountOccurrences(xml, "<Command>"));
    }

    [Fact]
    public void Build_xml_carries_the_sid_and_the_no_timeout_limit()
    {
        string xml = LogonTaskDefinition.BuildXml("C:\\Program Files\\Afterglow\\Afterglow.exe", "S-1-5-21-9-8-7-1001");
        Assert.Contains("<UserId>S-1-5-21-9-8-7-1001</UserId>", xml, StringComparison.Ordinal);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml, StringComparison.Ordinal);
        Assert.Contains("<Arguments>--minimized</Arguments>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_command_round_trips_what_build_xml_registers()
    {
        const string exe = @"C:\Program Files\Afterglow\Afterglow.exe";
        Assert.Equal(exe, LogonTaskDefinition.ParseCommand(LogonTaskDefinition.BuildXml(exe, "S-1-5-21-1-2-3-1001")));
    }

    [Fact]
    public void Parse_command_reads_what_schtasks_prints_and_strips_quotes()
    {
        // Shape of `schtasks /Query /TN Afterglow /XML` on Windows 11 (version 1.3, quoted path).
        const string printed = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>"%ProgramFiles%\Afterglow\Afterglow.exe"</Command>
                  <Arguments>--minimized</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Afterglow", "Afterglow.exe"),
            LogonTaskDefinition.ParseCommand(printed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions/></Task>")]
    public void Parse_command_returns_null_when_there_is_no_exec_action(string xml) =>
        Assert.Null(LogonTaskDefinition.ParseCommand(xml));

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
