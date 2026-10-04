; Script generated for OMT Capture Studio
; Inno Setup 6.x Script
; Targets Windows 10/11 x64 (Native AOT)

#ifndef MyAppVersion
#define MyAppVersion "1.0.0.12"
#endif

#define MyAppName "OMT Capture Studio"
#define MyAppPublisher "gnamse"
#define MyAppURL "https://github.com/gnamse/OmtCaptureStudio"
#define MyAppExeName "OmtCaptureStudio.exe"

[Setup]
; Unique AppId generated for OMT Capture Studio
AppId={{9F73A2B1-4A5D-4E90-B841-A89E62C85D21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=..\LICENSE.txt
OutputDir=..\Releases
OutputBaseFilename=OmtCaptureStudio-v{#MyAppVersion}-Setup
SetupIconFile=..\OmtCaptureStudio\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; 64-bit native configuration
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Allow installing for current user without admin rights or system-wide with admin rights
PrivilegesRequiredOverridesAllowed=dialog
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "firewalltask"; Description: "Add Windows Defender Firewall rule for OMT network streaming"; GroupDescription: "Network Configuration:"

[Files]
; Publish folder contents (Single-file exe or complete binaries)
Source: "..\Publish_AOT\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; Optional: If FFmpeg was placed in tools/, bundle it directly into app folder
Source: "..\tools\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Add Firewall rule for OMT Discovery (mDNS) and video transport if task was selected
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""OMT Capture Studio"" dir=in action=allow program=""{app}\{#MyAppExeName}"" enable=yes profile=any"; Flags: runhidden; Tasks: firewalltask
; Post-installation launch option
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Clean up firewall rule on uninstall
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""OMT Capture Studio"""; Flags: runhidden; RunOnceId: "DelFirewallRule"

[Code]
// Check for Microsoft Visual C++ 2015-2022 x64 Redistributable
function InitializeSetup(): Boolean;
var
  Installed: Cardinal;
  ErrorCode: Integer;
begin
  Result := True;
  if not RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Installed', Installed) or (Installed <> 1) then
  begin
    if MsgBox('OMT Capture Studio requires the Microsoft Visual C++ 2015-2022 x64 Redistributable.' + #13#10 + #13#10 +
              'Would you like to open the official Microsoft download page now to install it?', 
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://aka.ms/vs/17/release/vc_redist.x64.exe', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
end;
