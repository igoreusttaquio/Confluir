; Instalador do Confluir para Windows (Inno Setup 6).
; Compilado pelo workflow de lançamento:
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=<pasta do publish> /DOutputDir=<saída> packaging\windows\confluir.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\out"
#endif

[Setup]
; Não mude o AppId: é por ele que o Windows reconhece atualizações e a desinstalação.
AppId={{6B3F2C1E-8A4D-4E7B-9C2A-5D1F0E7A3B94}
AppName=Confluir
AppVersion={#AppVersion}
AppVerName=Confluir {#AppVersion}
AppPublisher=Confluir
AppPublisherURL=https://github.com/igoreusttaquio/Confluir
AppSupportURL=https://github.com/igoreusttaquio/Confluir/issues
AppUpdatesURL=https://github.com/igoreusttaquio/Confluir/releases
; Instala para o usuário atual, sem pedir administrador (o usuário pode escolher instalar para todos).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Confluir
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Confluir-Setup-x64
SetupIconFile=..\..\assets\confluir.ico
UninstallDisplayIcon={app}\Confluir.exe
UninstallDisplayName=Confluir
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Fecha o Confluir aberto antes de atualizar os arquivos.
CloseApplications=force

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Confluir"; Filename: "{app}\Confluir.exe"
Name: "{autodesktop}\Confluir"; Filename: "{app}\Confluir.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Confluir.exe"; Description: "{cm:LaunchProgram,Confluir}"; Flags: nowait postinstall skipifsilent
; Atualização pelo próprio app (/SILENT /RELAUNCH=1): reabre o Confluir no fim.
Filename: "{app}\Confluir.exe"; Flags: nowait; Check: ShouldRelaunch

; Os dados do usuário (contas, cache, configurações) ficam em %APPDATA%\Confluir e não são apagados ao desinstalar.

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

// Atualização pelo app: um Confluir que tenha ficado aberto (por exemplo só na bandeja) impediria trocar os
// arquivos; fecha à força antes de instalar.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  if ExpandConstant('{param:RELAUNCH|0}') = '1' then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM Confluir.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
