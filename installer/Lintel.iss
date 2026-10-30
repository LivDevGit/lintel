; Lintel installer (Inno Setup 6).
; Build: installer\build.ps1   (publishes the app, then compiles this script)

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6C3B6F2E-4A47-4E0B-9C3E-5A1D2F7B8E41}
AppName=Lintel
AppVersion={#AppVersion}
AppVerName=Lintel {#AppVersion}
AppPublisher=Liviu Iorga
AppPublisherURL=https://github.com/LivDevGit/lintel
AppSupportURL=https://github.com/LivDevGit/lintel/issues
AppUpdatesURL=https://github.com/LivDevGit/lintel/releases
; Per-user install: no administrator prompt, installs to %LOCALAPPDATA%\Programs\Lintel.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Lintel
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=Lintel-Setup-{#AppVersion}
SetupIconFile=..\src\Lintel\Assets\lintel.ico
UninstallDisplayIcon={app}\Lintel.exe
UninstallDisplayName=Lintel
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; A running Lintel is closed in [Code] before installing or uninstalling (see StopLintel),
; so neither ever aborts because the app is open.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Lintel when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Lintel"; Filename: "{app}\Lintel.exe"
Name: "{autoprograms}\Lintel Settings"; Filename: "{app}\Lintel.exe"; Parameters: "--settings"
Name: "{autodesktop}\Lintel"; Filename: "{app}\Lintel.exe"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" setting writes, so the two stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Lintel"; ValueData: """{app}\Lintel.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Lintel.exe"; Description: "Launch Lintel"; Flags: nowait postinstall skipifsilent

[Code]
// Close a running Lintel so its files aren't locked. Windows also releases any reserved
// top strip (app bar) when the process ends.
procedure StopLintel();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Lintel.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function InitializeSetup(): Boolean;
begin
  StopLintel();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  StopLintel();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove "Start with Windows" even if it was turned on from inside the app.
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Lintel');
    // Settings are kept unless the user explicitly says otherwise. SuppressibleMsgBox returns the
    // default (No) in silent uninstalls, so automated removals never delete anyone's settings.
    if SuppressibleMsgBox('Also remove your Lintel settings and shelf list?' + #13#10 +
                          '(They are stored in ' + ExpandConstant('{userappdata}\Lintel') + '.)',
                          mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DelTree(ExpandConstant('{userappdata}\Lintel'), True, True, True);
  end;
end;
