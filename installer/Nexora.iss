; Nexora 1.3.5 - installation neuve Windows (aucune reprise des anciennes installations)
#define SourceRoot "..\..\nexora\dist\Nexora"
[Setup]
AppId={{0D9ABDB5-E495-4D14-8B43-DB97FC17DB5C}
AppName=Nexora
AppVersion=1.3.5
AppPublisher=Nexora
DefaultDirName={localappdata}\Programs\Nexora
DefaultGroupName=Nexora
PrivilegesRequired=lowest
OutputDir=..\..\nexora\dist
OutputBaseFilename=Nexora-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\..\nexora\Nexora.Gui\Assets\Nexora_Icon.ico
UninstallDisplayName=Nexora
UninstallDisplayIcon={app}\Nexora.exe
CloseApplications=yes
RestartApplications=no
[Files]
Source: "{#SourceRoot}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
[Icons]
Name: "{autoprograms}\Nexora"; Filename: "{app}\Nexora.exe"
[Run]
Filename: "{app}\Nexora.exe"; Description: "Lancer Nexora"; Flags: nowait postinstall skipifsilent
; Le pilote USB/IP n'est jamais installe ni modifie par ce programme.
