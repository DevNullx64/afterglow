using System.Security;

namespace Afterglow.Core.Security;

/// <summary>
/// Builds the Task Scheduler XML for the "Afterglow" logon task. Pure and in
/// Core so the one security-critical property — that the executable path and
/// user SID are XML-escaped, and so cannot inject extra task elements — is
/// unit-testable. The task itself is created by the elevated app
/// (<c>StartupTaskService</c>), which stages this XML in an administrators-only
/// directory rather than the user-writable %TEMP% the old code used (F-33).
/// </summary>
public static class LogonTaskDefinition
{
    public const string TaskName = "Afterglow";

    /// <summary>
    /// ExecutionTimeLimit PT0S is load-bearing: the scheduler's default 72-hour
    /// limit would kill a resident tray app mid-week. The <c>&lt;Command&gt;</c>
    /// and both <c>&lt;UserId&gt;</c> values are escaped so a path or SID can
    /// never close an element and append attacker-chosen task content.
    /// </summary>
    public static string BuildXml(string exePath, string userSid)
    {
        string command = SecurityElement.Escape(exePath);
        string sid = SecurityElement.Escape(userSid);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Afterglow at logon, elevated, without a UAC prompt.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{sid}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>--minimized</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// The executable a task definition launches (its first Exec action's
    /// Command), quotes removed and environment variables expanded the way Task
    /// Scheduler expands them. Null when the XML is not a task definition or has
    /// no Exec action — callers must then not claim anything about the location.
    /// </summary>
    public static string? ParseCommand(string taskXml)
    {
        try
        {
            // `schtasks /XML` prints a UTF-16 declaration on text it has already
            // decoded; parsing from a string ignores the declared encoding.
            var doc = System.Xml.Linq.XDocument.Parse(taskXml);
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            string? command = doc.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value;
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            return Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
