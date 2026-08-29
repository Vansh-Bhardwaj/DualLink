#define AppName "DualLink"
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#define AppPublisher "DualLink"
#define AppExeName "DualLink.exe"

[Setup]
AppId={{85739A0C-EE5E-4BA4-AB4D-126921C1B31E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\DualLink
DefaultGroupName=DualLink
DisableDirPage=no
DisableProgramGroupPage=no
AllowNoIcons=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=DualLink-{#AppVersion}-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
WizardSizePercent=110
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=DualLink offline installer
SetupIconFile=..\assets\DualLink.ico
LicenseFile=..\LICENSE
InfoBeforeFile=..\INSTALL-NOTICE.txt

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\publish\DualLink.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "prereqs\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "prereqs\Windows.Packet.Filter.3.6.2.1.x64.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "prereqs\ProxiFyre-2.5.0-win-x64.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\SECURITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\DualLink.spdx.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "licenses\ProxiFyre-AGPL-3.0.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "licenses\Windows-Packet-Filter-MIT.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "licenses\dotnet-runtime-MIT.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "licenses\dotnet-runtime-THIRD-PARTY-NOTICES.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "licenses\Inter-OFL-1.1.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\DualLink"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\Third-party notices"; Filename: "{app}\THIRD-PARTY-NOTICES.md"
Name: "{autodesktop}\DualLink"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Visual C++ runtime..."; Flags: waituntilterminated
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\Windows.Packet.Filter.3.6.2.1.x64.msi"" /qn /norestart"; StatusMsg: "Installing Windows Packet Filter..."; Flags: waituntilterminated; Check: not PacketFilterInstalled
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\ProxiFyre-2.5.0-win-x64.msi"" /qn /norestart"; StatusMsg: "Installing the application filter..."; Flags: waituntilterminated; Check: not ProxiFyreInstalled
Filename: "{app}\{#AppExeName}"; Description: "Launch DualLink"; Flags: nowait postinstall skipifsilent

[Code]
function PacketFilterInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\drivers\ndisrd.sys'));
end;

function ProxiFyreInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{autopf}\ProxiFyre\ProxiFyre.exe'));
end;
