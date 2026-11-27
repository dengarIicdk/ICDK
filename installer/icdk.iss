; ICDK 1.0.0 — установщик для обычных игроков.
; Собирается из publish.ps1:  ISCC.exe /DAppVersion=1.0.0 installer\icdk.iss
; Ставится в %LOCALAPPDATA%\Programs\ICDK, прав администратора не просит.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "ICDK"
#define AppExe "ICDK.exe"
#define AppPublisher "ICDK"
#define AppUrl "https://github.com/"
#define WebView2Url "https://go.microsoft.com/fwlink/p/?LinkId=2124703"

[Setup]
AppId={{7A3E1C52-9F4B-4C1E-9A6D-2B4F8E10A000}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=ICDK_Setup_v{#AppVersion}
SetupIconFile=..\src\Wibe.Server\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=2
WizardStyle=modern
WizardSizePercent=110
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableWelcomePage=no
LicenseFile=license_ru.txt
ShowLanguageDialog=auto
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.LaunchApp=Запустить {#AppName} после установки
russian.DesktopIcon=Создать ярлык на рабочем столе
russian.MissingWebView2=Нужен Microsoft Edge WebView2 Runtime — он скачивается автоматически (около 2 МБ), подождите…
russian.WebView2Failed=Не удалось поставить WebView2 Runtime. Скачайте его вручную: https://developer.microsoft.com/microsoft-edge/webview2/
english.LaunchApp=Launch {#AppName} after installation
english.DesktopIcon=Create a desktop shortcut
english.MissingWebView2=Microsoft Edge WebView2 Runtime is required — downloading it now (about 2 MB), please wait…
english.WebView2Failed=Could not install the WebView2 Runtime. Install it manually: https://developer.microsoft.com/microsoft-edge/webview2/

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\Wibe.Server\Assets\app.ico"; DestDir: "{app}"; DestName: "ICDK.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\ICDK.ico"; Comment: "Примерочная Dota 2: скины, арканы, ландшафты"
Name: "{group}\Удалить {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\ICDK.ico"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Windows\Start Menu\Programs\{#AppName}.lnk"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\ICDK.ico"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait runhidden; Check: ShouldSilentLaunch

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  WebView2Client = '\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2Present: Boolean;
var
  Value: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Value) and (Value <> '0.0.0.0') or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Value) and (Value <> '0.0.0.0') or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Value) and (Value <> '0.0.0.0');
end;

function ShouldSilentLaunch: Boolean;
begin
  Result := WizardSilent;
end;

procedure InstallWebView2;
var
  ResultCode: Integer;
  Target: String;
begin
  if WebView2Present then
    exit;
  Target := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
  try
    DownloadTemporaryFile('{#WebView2Url}', 'MicrosoftEdgeWebview2Setup.exe', '', nil);
  except
    MsgBox(CustomMessage('WebView2Failed'), mbError, MB_OK);
    exit;
  end;
  if FileExists(Target) then
    if not Exec(Target, '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox(CustomMessage('WebView2Failed'), mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    InstallWebView2;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
end;
