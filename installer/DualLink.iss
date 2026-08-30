#define AppName "DualLink"
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "2.0.0"
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
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
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

[Files]
Source: "..\dist\publish\DualLink.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\publish\DualLink.Watchdog.exe"; DestDir: "{app}"; Flags: ignoreversion
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
Filename: "{app}\{#AppExeName}"; Description: "Open DualLink now"; Flags: nowait postinstall skipifsilent runascurrentuser

[Code]
const
  UninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{85739A0C-EE5E-4BA4-AB4D-126921C1B31E}_is1';

var
  MaintenancePage: TInputOptionWizardPage;
  ExistingInstall: Boolean;
  InstalledVersion: String;
  ExistingInstallPath: String;

function ReadExistingInstall: Boolean;
begin
  Result := RegQueryStringValue(HKLM64, UninstallKey, 'DisplayVersion', InstalledVersion) and
    RegQueryStringValue(HKLM64, UninstallKey, 'InstallLocation', ExistingInstallPath) and
    FileExists(AddBackslash(ExistingInstallPath) + 'unins000.exe');
end;

procedure InitializeWizard;
begin
  ExistingInstall := ReadExistingInstall;
  if ExistingInstall and (ExpandConstant('{param:UPDATE|0}') <> '1') then
  begin
    MaintenancePage := CreateInputOptionPage(
      wpWelcome,
      'DualLink is already installed',
      'Installed version: ' + InstalledVersion,
      'Choose what setup should do. Your application choices and preferences are kept during update and repair.',
      True,
      False);
    MaintenancePage.Add('Update or reinstall DualLink {#AppVersion}');
    MaintenancePage.Add('Repair the current installation');
    MaintenancePage.Add('Uninstall DualLink');
    MaintenancePage.SelectedValueIndex := 0;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if Assigned(MaintenancePage) and (CurPageID = MaintenancePage.ID) then
    WizardForm.NextButton.Caption := 'Continue';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
  Uninstaller: String;
begin
  Result := True;
  if Assigned(MaintenancePage) and (CurPageID = MaintenancePage.ID) and
    (MaintenancePage.SelectedValueIndex = 2) then
  begin
    Uninstaller := AddBackslash(ExistingInstallPath) + 'unins000.exe';
    if not Exec(Uninstaller, '/SILENT /NORESTART', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) or
      (ResultCode <> 0) then
    begin
      MsgBox('Windows could not uninstall DualLink. You can also remove it from Settings > Apps.', mbError, MB_OK);
      Result := False;
      exit;
    end;
    MsgBox('DualLink was uninstalled and normal routing was restored.', mbInformation, MB_OK);
    WizardForm.Close;
    Result := False;
  end;
end;

function PacketFilterInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\drivers\ndisrd.sys'));
end;

function ProxiFyreInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{autopf}\ProxiFyre\ProxiFyre.exe'));
end;
