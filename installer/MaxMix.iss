; -----------------------------------------------------------------------------
; MaxMix Inno Setup Script
;
; This script is invoked by `installer/build-installer.ps1`, which injects the
; following defines on the ISCC.exe command line:
;
;   /DMyAppVersion=<value from MaxMix.App.csproj <Version>>
;   /DPublishDir=<absolute path to installer/publish>
;   /DOutputDir=<absolute path to installer/output>
;
; The .iss script never hard-codes version or paths — single source of truth
; lives in `src/MaxMix.App/MaxMix.App.csproj`.
; -----------------------------------------------------------------------------

#define MyAppName      "MaxMix"
#define MyAppExeName   "MaxMix.exe"
#define MyAppPublisher "MaxMix"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "publish"
#endif

#ifndef OutputDir
  #define OutputDir "output"
#endif

[Setup]
; Stable AppId — DO NOT change this value across releases. Inno Setup uses it to
; identify upgrades; changing it would cause side-by-side installs instead of
; replacing the previous version.
AppId={{B1F4E2A0-7C3D-4B5E-9A1C-MAXMIX000001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/maxmix-software
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\MaxMix
DefaultGroupName=MaxMix
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=MaxMixSetup
SetupIconFile=assets\app.ico
; License agreement shown on the wizard's "License Agreement" page. Path is
; resolved relative to this .iss file (installer/), so `..\LICENSE` points at
; the repo-root LICENSE. Required by Spec Requirement: Installer Metadata /
; Scenario "License shown before install".
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Pull everything dotnet publish produced into Program Files\MaxMix.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MaxMix";            Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall MaxMix";  Filename: "{uninstallexe}"
Name: "{autodesktop}\MaxMix";      Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch MaxMix"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Sweep up logs / runtime files dotnet publish didn't lay down (e.g. Serilog
; rolling logs if the user ever set them under {app}). User data lives under
; %LOCALAPPDATA% and is intentionally NOT deleted.
Type: filesandordirs; Name: "{app}"
