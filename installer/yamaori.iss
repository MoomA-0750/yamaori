; Yamaori のインストーラー（Inno Setup 6）。
; 管理者権限なしで、本人のアカウントだけに入れる（%LOCALAPPDATA%\Programs\Yamaori）。ログオン時に起こし、入れ終えたら起こす。
; 途中で何も聞かない: Yamaori-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART（winget もこの形で呼ぶ）
; 設定は引数で渡せる: /PORT=47800 /LISTEN=local（入れたあとは WebUI の設定で変えられる）
;
; 作り方（Windows で）:
;   powershell -File installer\build.ps1      … 画面と本体を発行してから、iscc で Yamaori-Setup-<版>.exe を作る
;
; 2026-11-09 に書いた。まだ Windows で作って試していない。

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{6B4E1E0C-3C8D-4F5B-9B1F-2F4D9A6C7E11}
AppName=Yamaori
AppVersion={#AppVersion}
AppPublisher=MoomA
AppPublisherURL=https://github.com/MoomA-0750/yamaori
DefaultDirName={localappdata}\Programs\Yamaori
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
OutputDir=..\artifacts
OutputBaseFilename=Yamaori-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 窓を持たない Yamaori は Windows の閉じる合図（Restart Manager）に応えないので、それに頼らず PrepareToInstall で止める
CloseApplications=no
RestartApplications=no
UninstallDisplayName=Yamaori
UninstallDisplayIcon={app}\Yamaori.exe
LicenseFile=..\LICENSE

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Yamaori"; Filename: "http://localhost:{code:GetPort}/"; IconFilename: "{app}\Yamaori.exe"

[Registry]
; ログオン時に起こす（本人のアカウントだけ）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Yamaori"; ValueData: """{app}\Yamaori.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Yamaori.exe"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser
Filename: "http://localhost:{code:GetPort}/"; Description: "Yamaori を開く"; Flags: postinstall shellexec skipifsilent nowait

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Yamaori.exe /F"; Flags: runhidden; RunOnceId: "StopYamaori"

[Code]
// /PORT=… /LISTEN=… を、データのフォルダの yamaori.json に書く（すでにあれば触らない）
function GetPort(Param: String): String;
begin
  Result := ExpandConstant('{param:PORT|47800}');
end;

// 入れ替える前に、動いている Yamaori を止める
// 止める指示のあと、プロセスが消え切る（部品のファイルが放される）まで待つ。消え切る前に入れ替えると、使用中で失敗する
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Log('Yamaori を止める');
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "Get-Process Yamaori -ErrorAction SilentlyContinue | Stop-Process -Force; Wait-Process -Name Yamaori -Timeout 15 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('Yamaori を止めた: ' + IntToStr(ResultCode));
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir, Config: String;
begin
  if CurStep = ssPostInstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Yamaori');
    Config := DataDir + '\yamaori.json';
    if not FileExists(Config) then
    begin
      ForceDirectories(DataDir);
      SaveStringToFile(Config, '{ "Port": ' + GetPort('') + ', "Listen": "' + ExpandConstant('{param:LISTEN|local}') + '" }', False);
    end;
  end;
end;
