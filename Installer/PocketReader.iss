; InnoSetup script for PocketReader — portable, self-contained WinUI 3 app.
; Packages everything from ..\publish_out (produced by:
;   dotnet publish -c Release -r win-x64 -p:Platform=x64 --self-contained -o publish_out )
; User can install to any drive (C:, D:, E:, USB) without admin rights.

#define MyAppName "PocketReader"
#define MyAppVersion "1.8.2"
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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Everything the publish produced (exe, all DLLs, runtime, WebView2 loader, etc.).
; Excludes guard against ever shipping a test run's local db/token if one leaked
; into the publish folder.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "\data\*,\data,token.txt,*.db"; Flags: recursesubdirs createallsubdirs ignoreversion

[Dirs]
; Portable data folder lives next to the exe; writable by the installing user.
Name: "{app}\data"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove the local cache/db on uninstall (portable cleanup).
Type: filesandordirs; Name: "{app}\data"
