#ifndef AppPublishDir
  #define AppPublishDir "publish\PolyChrom3MF_2.0.12"
#endif

[Setup]
AppName=PolyChrom 3MF
AppVersion=2.0.12
AppPublisher=bob59
DefaultDirName={localappdata}\Programs\PolyChrom 3MF
DefaultGroupName=PolyChrom 3MF
OutputDir=LIVRAISON_FINALE\Installateur
OutputBaseFilename=PolyChrom3MF_Setup_x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayName=PolyChrom 3MF
SetupIconFile=PolyChrom3MF.App\Assets\PolyChrom.ico
UninstallDisplayIcon={app}\PolyChrom3MF.exe
ChangesAssociations=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"

[Files]
Source: "{#AppPublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\\PolyChrom 3MF"; Filename: "{app}\\PolyChrom3MF.exe"
Name: "{autodesktop}\\PolyChrom 3MF"; Filename: "{app}\\PolyChrom3MF.exe"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\\Classes\\.poly3mf"; ValueType: string; ValueName: ""; ValueData: "PolyChrom3MF.Project"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\\Classes\\PolyChrom3MF.Project"; ValueType: string; ValueName: ""; ValueData: "Projet portable PolyChrom 3MF"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\\Classes\\PolyChrom3MF.Project\\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\\PolyChrom3MF.exe,0"
Root: HKA; Subkey: "Software\\Classes\\PolyChrom3MF.Project\\shell\\open\\command"; ValueType: string; ValueName: ""; ValueData: """{app}\\PolyChrom3MF.exe"" ""%1"""

[Run]
Filename: "{app}\\PolyChrom3MF.exe"; Description: "Lancer PolyChrom 3MF"; Flags: nowait postinstall skipifsilent
Filename: "{app}\\PolyChrom3MF.exe"; Flags: nowait skipifnotsilent
