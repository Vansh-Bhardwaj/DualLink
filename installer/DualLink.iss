#define AppName "DualLink"
#ifndef AppVersion
  #define AppVersion "5.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "5.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\target\release"
#endif
#define AppPublisher "DualLink"
#define AppExeName "DualLink.exe"
#define ServiceExeName "DualLinkService.exe"

[Setup]
AppId={{85739A0C-EE5E-4BA4-AB4D-126921C1B31E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\DualLink
DefaultGroupName=DualLink
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist\release
OutputBaseFilename=DualLink-{#AppVersion}-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
WizardSizePercent=110
CloseApplications=force
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoVersion={#NumericVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#NumericVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=DualLink offline installer
SetupIconFile=..\assets\DualLink.ico
LicenseFile=..\LICENSE
InfoBeforeFile=..\INSTALL-NOTICE.txt

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[InstallDelete]
; DualLink 4's parts, replaced by the single service in DualLink 5.
Type: files; Name: "{app}\DualLink.Service.exe"
Type: files; Name: "{app}\DualLink.Watchdog.exe"
Type: files; Name: "{app}\DualLink.spdx.json"
Type: filesandordirs; Name: "{commonappdata}\DualLink\Recovery"

[Files]
Source: "{#PayloadDir}\DualLink.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\DualLinkService.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "prereqs\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "prereqs\Windows.Packet.Filter.3.6.2.1.x64.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\SECURITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "licenses\Windows-Packet-Filter-MIT.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "licenses\Inter-OFL-1.1.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\DualLink"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\DualLink"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing the Visual C++ runtime..."; Flags: waituntilterminated
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\Windows.Packet.Filter.3.6.2.1.x64.msi"" /qn /norestart"; StatusMsg: "Installing the network driver..."; Flags: waituntilterminated; Check: not PacketFilterInstalled
Filename: "{app}\{#ServiceExeName}"; Parameters: "--install"; StatusMsg: "Starting the DualLink service..."; Flags: waituntilterminated runhidden
Filename: "{app}\{#AppExeName}"; Description: "Open DualLink now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "CloseWindow"
Filename: "{app}\{#ServiceExeName}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\DualLink"

[Code]
function PacketFilterInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\drivers\ndisrd.sys'));
end;

{ The running service holds its exe open: stop and remove it before files are replaced.
  Setup registers it again once the new files are in place. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Service: String;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Service := ExpandConstant('{app}\{#ServiceExeName}');
  if FileExists(Service) then
    Exec(Service, '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Opening at sign-in is a per-user choice made in the app; remove it with the app. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DualLink');
end;
