#define MyAppName "DDJ-200 Codex Bridge"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "DDJ-200 Codex Bridge community project"
#define MyAppExeName "DDJ200.CodexBridge.exe"

[Setup]
AppId={{C5081C1E-79A1-4B6A-8C02-A3E3F9B8F8B1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion=1.0.0.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Instalador comunitário da ponte DDJ-200 para Codex Micro
VersionInfoCompany={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\DDJ200CodexBridge
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=DDJ200-Codex-Bridge-1.0.0-Setup
SetupIconFile=..\assets\windows\ddj200-codex-bridge.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
ChangesEnvironment=no
ChangesAssociations=no
MinVersion=10.0.18362
SetupLogging=yes
SignedUninstaller=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Iniciar com o Windows (pode ser desativado pelo menu da bandeja)"; GroupDescription: "Inicialização:"; Flags: unchecked
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Files]
Source: "..\stage\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DDJ-200 Codex Bridge"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Documentação"; Filename: "{app}\README.md"
Name: "{group}\Desinstalar DDJ-200 Codex Bridge"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DDJ-200 Codex Bridge"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DDJ200CodexBridge"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir DDJ-200 Codex Bridge"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--shutdown"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "ShutdownBridge"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode, Attempt: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    for Attempt := 1 to 30 do
    begin
      if not CheckForMutexes('Local\DDJ200.CodexBridge.Tray,Local\DDJ200.CodexBridge.Engine') then Break;
      Sleep(500);
    end;
    if CheckForMutexes('Local\DDJ200.CodexBridge.Tray,Local\DDJ200.CodexBridge.Engine') then
      Result := 'A ponte ou o USB/IP ainda não encerrou com segurança. Feche pela bandeja e tente novamente.';
  end;
  if (Result = '') and FileExists(ExpandConstant('{localappdata}\DDJ200CodexBridge\runtime\usbip-127.0.0.1-3240-1-1.owned')) then
    Result := 'Há uma porta USB/IP residual desta ponte. Abra a versão instalada, use Parar ponte para confirmar a limpeza e tente novamente.';
end;

function InitializeUninstall(): Boolean;
var
  ResultCode, Attempt: Integer;
begin
  Result := True;
  if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for Attempt := 1 to 30 do
  begin
    if not CheckForMutexes('Local\DDJ200.CodexBridge.Tray,Local\DDJ200.CodexBridge.Engine') then Break;
    Sleep(500);
  end;
  if CheckForMutexes('Local\DDJ200.CodexBridge.Tray,Local\DDJ200.CodexBridge.Engine') then
  begin
    MsgBox('A ponte ou o USB/IP ainda não encerrou com segurança. A desinstalação foi cancelada; feche pela bandeja e tente novamente.', mbError, MB_OK);
    Result := False;
  end;
  if Result and FileExists(ExpandConstant('{localappdata}\DDJ200CodexBridge\runtime\usbip-127.0.0.1-3240-1-1.owned')) then
  begin
    MsgBox('Há uma porta USB/IP residual desta ponte. Abra o aplicativo e use Parar ponte antes de desinstalar.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DDJ200CodexBridge');
end;
