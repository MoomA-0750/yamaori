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
CloseApplications=force
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

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir, Config: String;
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
    // 動いている Yamaori を止めてから入れ替える
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Yamaori.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
