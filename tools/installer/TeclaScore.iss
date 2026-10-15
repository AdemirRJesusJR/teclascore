; Compile through tools/build_installer.ps1: its generated include contains only
; the verified manifest entries, the manifest itself, and the three delivery docs.
#ifndef AppVersion
  #error AppVersion must be supplied by build_installer.ps1
#endif
#ifndef PayloadInclude
  #error PayloadInclude must be supplied by build_installer.ps1
#endif
#ifndef BuildOutputDir
  #error BuildOutputDir must be supplied by build_installer.ps1
#endif
#ifndef AppIcon
  #error AppIcon must be supplied by build_installer.ps1
#endif
#define PayloadDirectory "versions\" + AppVersion + "-setup-x64"
#define AppExecutable "Teclas.Partituras.exe"

[Setup]
AppId=TeclaScore.Desktop
AppName=TeclaScore
AppVersion={#AppVersion}
AppVerName=TeclaScore {#AppVersion}
AppPublisher=TeclaScore
VersionInfoVersion={#AppVersion}
VersionInfoProductName=TeclaScore
VersionInfoDescription=Instalador do TeclaScore
DefaultDirName={localappdata}\Programs\TeclaScore
DefaultGroupName=TeclaScore
AllowNoIcons=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
CloseApplications=no
RestartApplications=no
AlwaysRestart=no
RestartIfNeededByRun=no
UninstallRestartComputer=no
UninstallFilesDir={app}\uninstall
UninstallDisplayName=TeclaScore
UninstallDisplayIcon={app}\{#PayloadDirectory}\{#AppExecutable}
UninstallLogMode=append
SetupIconFile={#AppIcon}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir={#BuildOutputDir}
OutputBaseFilename=TeclaScore-{#AppVersion}-Setup-x64

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Files]
#include PayloadInclude

[Icons]
Name: "{userprograms}\TeclaScore\TeclaScore"; Filename: "{app}\{#PayloadDirectory}\{#AppExecutable}"; WorkingDir: "{app}\{#PayloadDirectory}"; Comment: "TeclaScore — partituras para teclado"; AppUserModelID: "TeclaScore.Desktop"; Flags: uninsneveruninstall; Check: ShouldCreateShortcuts
Name: "{userdesktop}\TeclaScore"; Filename: "{app}\{#PayloadDirectory}\{#AppExecutable}"; WorkingDir: "{app}\{#PayloadDirectory}"; Comment: "TeclaScore — partituras para teclado"; AppUserModelID: "TeclaScore.Desktop"; Tasks: desktopicon; Flags: uninsneveruninstall; Check: ShouldCreateShortcuts

[Run]
Filename: "{app}\{#PayloadDirectory}\{#AppExecutable}"; WorkingDir: "{app}\{#PayloadDirectory}"; Description: "Abrir o TeclaScore"; Flags: postinstall unchecked skipifsilent nowait

[Code]
function ShouldCreateShortcuts: Boolean;
var
  I: Integer;
begin
  // Also honor /NOICONS for explicit userprograms/userdesktop paths.
  Result := not WizardNoIcons;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/NOICONS') = 0 then
    begin
      Result := False;
      Exit;
    end;
end;

// A later ZIP installation may reuse these names. Only remove a shortcut that
// still targets this exact setup payload; never remove another installation's link.
procedure RemoveOwnedShortcut(const ShortcutPath, ExpectedTarget: String);
var
  Shell, Shortcut: Variant;
  Target: String;
begin
  if not FileExists(ShortcutPath) then
    Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutPath);
    Target := Shortcut.TargetPath;
    if (Target <> '') and
       (CompareText(ExpandFileName(Target), ExpandFileName(ExpectedTarget)) = 0) then
    begin
      if DeleteFile(ShortcutPath) then
        Log('Atalho desta instalação removido: ' + ShortcutPath)
      else
        Log('Não foi possível remover o atalho: ' + ShortcutPath);
    end
    else
      Log('Atalho de outra instalação preservado: ' + ShortcutPath);
  except
    Log('Atalho preservado porque seu destino não pôde ser conferido: ' + ShortcutPath);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExpectedTarget: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    ExpectedTarget := ExpandConstant('{app}\{#PayloadDirectory}\{#AppExecutable}');
    RemoveOwnedShortcut(ExpandConstant('{userprograms}\TeclaScore\TeclaScore.lnk'), ExpectedTarget);
    RemoveOwnedShortcut(ExpandConstant('{userdesktop}\TeclaScore.lnk'), ExpectedTarget);
  end;
end;
