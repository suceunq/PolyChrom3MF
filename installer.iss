[Setup]
AppName=PolyChrom 3MF
AppVersion=1.6.10
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

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"

[Files]
Source: "publish\\PolyChrom3MF_1.1\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\\PolyChrom 3MF"; Filename: "{app}\\PolyChrom3MF.exe"
Name: "{autodesktop}\\PolyChrom 3MF"; Filename: "{app}\\PolyChrom3MF.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\\PolyChrom3MF.exe"; Description: "Lancer PolyChrom 3MF"; Flags: nowait postinstall skipifsilent
Filename: "{app}\\PolyChrom3MF.exe"; Flags: nowait skipifnotsilent
