; Afterglow installer (Inno Setup 6)
; Build: ISCC.exe /DAppVersion=v1.0.0 installer\afterglow.iss
; Expects tools/publish.ps1 to have produced artifacts/portable first.

#ifndef AppVersion
  #define AppVersion "v0.0.0-dev"
#endif
#define CleanVersion Copy(AppVersion, 2, 99)

[Setup]
AppId={{7E6F2C1D-9A44-4C43-B58E-AFTERGLOW01}
AppName=Afterglow
AppVersion={#CleanVersion}
AppPublisher=Afterglow contributors
AppPublisherURL=https://github.com/minewefu/afterglow
DefaultDirName={autopf}\Afterglow
DefaultGroupName=Afterglow
OutputBaseFilename=Afterglow-{#CleanVersion}-setup
OutputDir=..\artifacts
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
SetupIconFile=..\src\Afterglow.App\Assets\afterglow.ico
UninstallDisplayIcon={app}\Afterglow.exe
WizardStyle=modern
LicenseFile=..\LICENSE

[Files]
Source: "..\artifacts\portable\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Afterglow"; Filename: "{app}\Afterglow.exe"
Name: "{autodesktop}\Afterglow"; Filename: "{app}\Afterglow.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startup"; Description: "Start Afterglow with Windows (elevated via Task Scheduler, no UAC prompt at logon)"; GroupDescription: "Startup:"; Flags: unchecked

[Registry]
; Pre-1.0.2 installs used a Run-key autostart (unelevated, prompted at every
; boot). Delete it unconditionally so upgrades never double-start.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Afterglow"; Flags: deletevalue

[Run]
; --register-startup is NOT a plain [Run] entry: Inno ignores a [Run] exit code,
; so a failed Task Scheduler registration (group policy, a security product, a
; refused location) would be reported to the user as a successful install while
; the ticked "Start with Windows" box silently did nothing (F-67). It now runs
; from [Code] below, which inspects the exit code and tells the user on failure.
Filename: "{app}\Afterglow.exe"; Description: "Launch Afterglow"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Afterglow"" /F"; RunOnceId: "RemoveStartupTask"; Flags: runhidden

[UninstallDelete]
; Keep user data (saved profiles, logs, flight recordings), but remove the
; machine-wide state that would otherwise silently re-apply tuning on a later
; reinstall: the startup/auto-apply settings and the per-GPU crash-recovery
; records (F-66). The scheduled task itself is removed by [UninstallRun] above.
Type: files; Name: "{commonappdata}\Afterglow\settings.json"
Type: files; Name: "{commonappdata}\Afterglow\applied-state*.json"

[Code]
// F-67: register the elevated logon task from code so the exit code is checked.
// Setup already runs elevated (PrivilegesRequired=admin), so the child inherits
// that token and only Task Scheduler itself can refuse.
procedure RegisterStartupTask;
var
  ResultCode: Integer;
begin
  if not WizardIsTaskSelected('startup') then
    exit;

  if Exec(ExpandConstant('{app}\Afterglow.exe'), '--register-startup', '',
          SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode <> 0 then
    begin
      Log(Format('register-startup failed with exit code %d', [ResultCode]));
      if not WizardSilent then
        MsgBox('Afterglow was installed, but turning on "Start Afterglow with Windows"'
          + ' did not work: the Task Scheduler task could not be created. This is often'
          + ' group policy or security software blocking scheduled-task creation.'
          + #13#10#13#10 + 'You can try again later from Settings; the rest of the install'
          + ' completed normally.', mbError, MB_OK);
    end;
  end
  else
  begin
    Log(Format('register-startup could not be launched (code %d)', [ResultCode]));
    if not WizardSilent then
      MsgBox('Afterglow was installed, but the startup-task helper could not be launched,'
        + ' so "Start Afterglow with Windows" was not enabled. You can turn it on later'
        + ' from Settings.', mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterStartupTask;
end;
