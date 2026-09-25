; InnoSetup script for PocketReader — portable, self-contained WinUI 3 app.
; Packages everything from ..\publish_out (produced by:
;   dotnet publish -c Release -r win-x64 -p:Platform=x64 --self-contained -o publish_out )
; User can install to any drive (C:, D:, E:, USB) without admin rights.

#define MyAppName "PocketReader"
#define MyAppVersion "1.9.0"
#define MyAppPublisher "PocketReader"
#define MyAppExeName "PocketReader.exe"
#define PublishDir "..\publish_out"

[Setup]
AppId={{3F4D5A6C-7E8F-9A0B-1C2D-3E4F5A6B7C8D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={sd}\PocketReader
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Let the user pick any location (incl. USB); no admin needed.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir=Output
OutputBaseFilename=PocketReader-Setup-{#MyAppVersion}
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
; Replace the uninstall log instead of appending to it: up to 1.8.2 the log held a step
; that deleted {app}\data, and an appended log would keep running it.
UninstallLogMode=overwrite

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Everything the publish produced (exe, all DLLs, runtime, WebView2 loader, etc.).
; Excludes guard against ever shipping a test run's local db/token if one leaked
; into the publish folder.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "\data\*,\data,token.txt,*.db"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; Up to 1.8.2 the app's resources were in resources.pri; since 1.9.0 they are in
; PocketReader.pri. A leftover resources.pri is read first, so remove it on upgrade.
Type: files; Name: "{app}\resources.pri"

[Dirs]
; Portable data folder lives next to the exe; writable by the installing user.
; Kept on uninstall: it holds the user's library.
Name: "{app}\data"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
