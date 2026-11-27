; Sirius IV's installer, compiled by tools/pack-iv.ps1 with Inno Setup 6:
;
;   ISCC /DAppVersion=1.0.5 /DSourceDir=<published folder> /DOutputDir=<dist> SiriusIV.iss
;
; For the user alone by default - no administrator needed - into
; %LOCALAPPDATA%\Programs\Sirius IV; for all users into Program Files when
; chosen at the start, with administrator rights. A Start menu entry, a
; desktop icon if chosen, and an uninstaller in Settings > Apps.
;
; The licence code is kept in the user's own folder, not the program's,
; so it outlives an uninstall and a newer version (decision 97).

#ifndef AppVersion
  #error Give the version: /DAppVersion=1.0.5
#endif
#ifndef SourceDir
  #error Give the published folder: /DSourceDir=...
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
; The same for every version, so a newer one installs over an older.
AppId={{0B802314-42D0-4689-B986-D89FF4EC3573}
AppName=Sirius IV
AppVersion={#AppVersion}
AppVerName=Sirius IV {#AppVersion}
AppPublisher=Sirius Vision
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Sirius IV
DefaultDirName={autopf}\Sirius IV
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=SiriusIV-{#AppVersion}-Setup
SetupIconFile=..\..\res\Sirius Image Viewer.ico
UninstallDisplayIcon={app}\SiriusIV.exe
UninstallDisplayName=Sirius IV
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Sirius IV open while it is updated: asked to close it.
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Sirius IV"; Filename: "{app}\SiriusIV.exe"
Name: "{autodesktop}\Sirius IV"; Filename: "{app}\SiriusIV.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SiriusIV.exe"; Description: "{cm:LaunchProgram,Sirius IV}"; Flags: nowait postinstall skipifsilent
