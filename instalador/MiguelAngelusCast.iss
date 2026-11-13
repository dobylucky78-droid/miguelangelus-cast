; Instalador do MiguelAngelus Cast (Inno Setup 6) — o mesmo jeito do instalador do MiguelAngelus.
; Gerar tudo assinado: powershell -ExecutionPolicy Bypass -File cast\instalador\gerar_versao_cast.ps1
;
; - Instala por usuário, sem pedir administrador (pode escolher "para todos" se quiser).
; - Os dados (cenas, câmeras, roteiros, chaves, rostos…) ficam em %LOCALAPPDATA%\MiguelAngelusCast e NÃO são apagados
;   ao desinstalar nem ao instalar uma versão nova por cima.

#define Nome "MiguelAngelus Cast"
#define Versao "0.1.11"
#define Editor "Paróquia São José Operário — PASCOM"
#define Exe "MiguelAngelusCast.exe"

[Setup]
; AppId fixo: versões novas instalam por cima desta
AppId={{5B8E2C47-9D3A-4F61-B2E8-7A1C04D93F25}
AppName={#Nome}
AppVersion={#Versao}
AppVerName={#Nome} {#Versao}
AppPublisher={#Editor}
AppPublisherURL=https://pascom-sjo.github.io/
AppSupportURL=https://github.com/dobylucky78-droid/miguelangelus-cast
AppComments=Transmissão das celebrações
VersionInfoVersion={#Versao}
VersionInfoDescription=Instalador do {#Nome}
DefaultDirName={autopf}\{#Nome}
DefaultGroupName={#Nome}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=.
OutputBaseFilename=Instalar MiguelAngelus Cast {#Versao}
SetupIconFile=..\programa\icone.ico
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName={#Nome}
WizardStyle=modern
DisableWelcomePage=no
Compression=lzma2/max
SolidCompression=yes
CloseApplications=force
RestartApplications=no

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "atalho"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "saida\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; arquivos de versões antigas que não vêm mais
Type: filesandordirs; Name: "{app}\web"

[Icons]
Name: "{group}\{#Nome}"; Filename: "{app}\{#Exe}"; Comment: "Transmissão das celebrações"
Name: "{group}\Desinstalar o {#Nome}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#Nome}"; Filename: "{app}\{#Exe}"; Tasks: atalho; Comment: "Transmissão das celebrações"

[Run]
Filename: "{app}\{#Exe}"; Description: "Abrir o {#Nome} agora"; Flags: nowait postinstall skipifsilent
; atualização automática (instalação silenciosa): reabre o programa sozinho no fim
Filename: "{app}\{#Exe}"; Flags: nowait postinstall skipifnotsilent

[Messages]
pt.WelcomeLabel2=Este assistente vai instalar o [name/ver] neste computador.%n%nO MiguelAngelus Cast transmite as celebrações (Missas, adorações, terços) ao vivo, com o roteiro da celebração, câmeras PTZ e a capa da live.%n%nSe já houver uma versão instalada, ela será atualizada e as cenas, câmeras, roteiros e demais ajustes serão mantidos.

[Code]
// Avisa (sem impedir) se faltar o WebView2 (a tela do Cast) ou o NDI Runtime (as câmeras e o telão por NDI)
function TemWebView2: Boolean;
var v: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0') or
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0');
end;

function TemNdi: Boolean;
var d: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'NDI_RUNTIME_DIR_V6', d) or
     RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'NDI_RUNTIME_DIR_V5', d) then
    Result := FileExists(AddBackslash(d) + 'Processing.NDI.Lib.x64.dll');
end;

function InitializeSetup: Boolean;
var faltando: String;
begin
  Result := True;
  if WizardSilent then Exit;
  faltando := '';
  if not TemWebView2 then faltando := faltando + #13#10 + '• Microsoft Edge WebView2 (a tela do Cast)';
  if not TemNdi then faltando := faltando + #13#10 + '• NDI Runtime, que vem no NDI Tools (as câmeras e o telão por NDI)';
  if faltando <> '' then
  begin
    Result := MsgBox('Este computador ainda não tem:' + faltando + #13#10#13#10 +
      'O Cast instala assim mesmo, mas precisa disso para funcionar por inteiro.' + #13#10 +
      'Ao terminar, abro as páginas para baixar.' + #13#10#13#10 +
      'Continuar a instalação?', mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var erro: Integer;
begin
  if (CurStep = ssDone) and not WizardSilent then
  begin
    if not TemWebView2 then ShellExec('open', 'https://go.microsoft.com/fwlink/p/?LinkId=2124703', '', '', SW_SHOWNORMAL, ewNoWait, erro);
    if not TemNdi then ShellExec('open', 'https://ndi.video/tools/', '', '', SW_SHOWNORMAL, ewNoWait, erro);
  end;
end;
