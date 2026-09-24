#define MyAppName "DDJ-200 Codex Bridge"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "DDJ-200 Codex Bridge community project"
#define MyAppExeName "DDJ200.CodexBridge.exe"

[Setup]
AppId={{C5081C1E-79A1-4B6A-8C02-A3E3F9B8F8B1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion=1.1.0.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Instalador comunitário da ponte DDJ-200 para Codex Micro
VersionInfoCompany={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\DDJ200CodexBridge
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir=..\artifacts\release
OutputBaseFilename=DDJ200-Codex-Bridge-1.1.0-Setup
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
InfoBeforeFile=first-run.txt

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Iniciar com o Windows (pode ser desativado pelo menu da bandeja)"; GroupDescription: "Inicialização:"; Flags: unchecked
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Files]
Source: "..\stage\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\.deps\USBip-0.9.8.0-x64.exe"; Flags: dontcopy

[Icons]
Name: "{group}\DDJ-200 Codex Bridge"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Documentação"; Filename: "{app}\README.md"
Name: "{group}\Desinstalar DDJ-200 Codex Bridge"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DDJ-200 Codex Bridge"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DDJ200CodexBridge"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\RunOnce"; ValueType: string; ValueName: "DDJ200CodexBridgeSetup"; ValueData: """{app}\{#MyAppExeName}"" --setup"; Flags: uninsdeletevalue; Check: DependencyRestartRequired

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--setup"; Description: "Preparar e verificar Codex Micro"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: CanStartSetup

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--shutdown"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "ShutdownBridge"

[Code]
var
  USBipNeedsRestart, USBipPrepared: Boolean;

function DependencyRestartRequired(): Boolean;
begin
  Result := USBipNeedsRestart;
end;

function CanStartSetup(): Boolean;
begin
  Result := not USBipNeedsRestart;
end;

function NeedRestart(): Boolean;
begin
  Result := USBipNeedsRestart;
end;

function USBipPath(): String;
var Dir: String;
begin
  if not RegQueryStringValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1', 'InstallLocation', Dir) then
    Dir := ExpandConstant('{commonpf64}\USBip');
  Result := AddBackslash(Dir) + 'usbip.exe';
end;

function PrepareUSBip(): String;
var Code: Integer; PackagePath, Parameters: String;
begin
  Result := '';
  if USBipPrepared then Exit;
  if FileExists(USBipPath()) then
  begin
    if not Exec(USBipPath(), 'port', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Result := 'Não foi possível verificar USBip. Reinicie o Windows se houver instalação pendente e tente novamente.'
    else if Code <> 0 then
      Result := 'O USBip instalado não acessou o driver UDE (código ' + IntToStr(Code) + '). Reinicie o Windows; se persistir, repare a instalação oficial USBip. Nenhum driver existente foi substituído.';
    USBipPrepared := Result = '';
    Exit;
  end;
  if RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\usbip2_ude') then
  begin
    Result := 'Existe um driver USBip sem o cliente acessível. Repare a instalação oficial USBip antes de continuar; o instalador não substitui automaticamente uma instalação incompleta.';
    Exit;
  end;
  if WizardSilent then
  begin
    Result := 'USBip ausente. Execute este instalador interativamente para instalar o driver com a confirmação do Windows.';
    Exit;
  end;
  if MsgBox('Será instalado o USBip-win2 0.9.8.0 oficial (cliente, drivers UDE/filtro e Visual C++). O Windows solicitará permissão de administrador. Dispositivos USB podem desconectar brevemente; encerre transferências USB antes de continuar. Será necessário reiniciar o Windows para concluir. Continuar?', mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := 'A instalação da dependência foi cancelada. Nenhum sucesso de instalação da ponte será informado.';
    Exit;
  end;
  ExtractTemporaryFile('USBip-0.9.8.0-x64.exe');
  PackagePath := ExpandConstant('{tmp}\USBip-0.9.8.0-x64.exe');
  if CompareText(GetSHA256OfFile(PackagePath), '81f426741f7ee2ed991febe24a22daca8400b6ae2f171054e3fb404897e15d39') <> 0 then
  begin
    Result := 'Falha de integridade do instalador USBip. Baixe novamente o instalador completo.';
    Exit;
  end;
  Parameters := '/SP- /SILENT /NORESTART /RESTARTEXITCODE=3010 /TYPE=compact /TASKS=vcredist /NOICONS /DIR="' + ExpandConstant('{commonpf64}\USBip') + '" /LOG';
  if not ShellExec('runas', PackagePath, Parameters, '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then
  begin
    Result := 'USBip não foi instalado: permissão de administrador recusada ou falha ao iniciar (código ' + IntToStr(Code) + ').';
    Exit;
  end;
  if (Code <> 0) and (Code <> 3010) then
  begin
    Result := 'O instalador oficial USBip retornou erro ' + IntToStr(Code) + '. Consulte o log USBip na pasta temporária da conta administradora e tente novamente.';
    Exit;
  end;
  if not FileExists(USBipPath()) then
  begin
    Result := 'O instalador USBip terminou, mas o cliente não foi encontrado. A instalação precisa ser reparada.';
    Exit;
  end;
  // The pinned official package has AlwaysRestart=yes. Do not report ready yet.
  USBipNeedsRestart := True;
  USBipPrepared := True;
end;

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
  if Result = '' then Result := PrepareUSBip();
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ForceDirectories(ExpandConstant('{localappdata}\DDJ200CodexBridge'));
    SaveStringToFile(ExpandConstant('{localappdata}\DDJ200CodexBridge\setup-pending'), 'Micro setup pending', False);
  end;
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
