#ifndef AppVersion
  #error AppVersion must be supplied by build-release.ps1
#endif

[Setup]
AppId={{E373E34B-C4C0-4BB1-889A-3467A5C19F30}
AppName=DS5 BT Haptics
AppVersion={#AppVersion}
AppPublisher=yyusvf
AppPublisherURL=https://github.com/yyusvf/ds5-bt-haptics
AppSupportURL=https://github.com/yyusvf/ds5-bt-haptics/issues
AppUpdatesURL=https://github.com/yyusvf/ds5-bt-haptics/releases
DefaultDirName={localappdata}\Programs\DS5 BT Haptics
DefaultGroupName=DS5 BT Haptics
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362
AppMutex=Local\DS5BtHaptics.SingleInstance
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\DS5BtHaptics.exe
UninstallDisplayName=DS5 BT Haptics
SetupIconFile=..\DS5BtHaptics.App\DS5BtHaptics.ico
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=DS5BtHaptics-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[CustomMessages]
english.RuntimeDownloading=Downloading Microsoft .NET 8 Desktop Runtime:
english.RuntimeInstalling=Installing Microsoft .NET 8 Desktop Runtime. Windows may ask for permission.
english.RuntimeFailed=Microsoft .NET 8 Desktop Runtime (x64) could not be installed. Check your connection and allow the Windows permission prompt, then retry Setup. Alternatively install the Desktop Runtime from https://dotnet.microsoft.com/download/dotnet/8.0.
english.DriversNote=After installation, DS5 BT Haptics opens next to the clock and helps you install usbip-win2 and HidHide if they are missing.
english.UndoHidHide=Make the Bluetooth DualSense visible to games again in HidHide? Windows will ask for permission.
german.RuntimeDownloading=Microsoft .NET 8 Desktop Runtime wird heruntergeladen:
german.RuntimeInstalling=Microsoft .NET 8 Desktop Runtime wird installiert. Windows fragt eventuell nach einer Berechtigung.
german.RuntimeFailed=Microsoft .NET 8 Desktop Runtime (x64) konnte nicht installiert werden. Pruefe deine Verbindung und erlaube die Windows-Berechtigungsabfrage. Starte Setup danach erneut. Alternativ: https://dotnet.microsoft.com/download/dotnet/8.0.
german.DriversNote=Nach der Installation oeffnet sich DS5 BT Haptics neben der Uhr und hilft, usbip-win2 und HidHide zu installieren, falls sie fehlen.
german.UndoHidHide=Den Bluetooth-DualSense in HidHide wieder fuer Spiele sichtbar machen? Windows fragt nach einer Berechtigung.

[Files]
Source: "..\dist\app\DS5BtHaptics.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\cli\ds5-bt-haptics.exe"; DestDir: "{app}\cli"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\DS5 BT Haptics"; Filename: "{app}\DS5BtHaptics.exe"
Name: "{autodesktop}\DS5 BT Haptics"; Filename: "{app}\DS5BtHaptics.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DS5BtHaptics.exe"; Description: "{cm:LaunchProgram,DS5 BT Haptics}"; Flags: nowait postinstall skipifsilent

[Code]
#include "DesktopRuntime.iss"

const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  HidHideCli = 'C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe';

procedure InitializeWizard;
begin
  WizardForm.ReadyLabel.Caption := WizardForm.ReadyLabel.Caption + #13#10#13#10 + ExpandConstant('{cm:DriversNote}');
end;

// Preserve the user's choice in the app; only move an existing autostart entry.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Existing: String;
begin
  if (CurStep = ssPostInstall) and RegQueryStringValue(HKCU, RunKey, 'DS5BtHaptics', Existing) then
    RegWriteStringValue(HKCU, RunKey, 'DS5BtHaptics', '"' + ExpandConstant('{app}\DS5BtHaptics.exe') + '" --tray');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  RegDeleteValue(HKCU, RunKey, 'DS5BtHaptics');
  // Without the app a hidden controller would stay invisible to games, so offer to undo that.
  if FileExists(HidHideCli) and not UninstallSilent and
     (MsgBox(ExpandConstant('{cm:UndoHidHide}'), mbConfirmation, MB_YESNO) = IDYES) then
    ShellExec('runas', ExpandConstant('{app}\DS5BtHaptics.exe'), '--undo-hidhide', '', SW_HIDE,
              ewWaitUntilTerminated, ResultCode);
  // Settings in AppData intentionally survive uninstall and reinstall.
end;
