; WinWhisper installer (Inno Setup 6)
;
; ビルド方法:
;   1) dotnet build -c Release
;   2) iscc installer\WinWhisper.iss /DAppVersion=1.0.0
;
; /DMyAppSourceDir=... で取り込むフォルダを差し替えられる（既定は ..\bin\WinWhisper\Release\net48）。
; GitHub Actions ではこのスクリプトを iscc で呼び出している。

#define AppName "WinWhisper"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

; exe からバージョン情報を読む（/DAppVersion を指定しない場合の既定として使う）
#define AppPublisher "WinWhisper"
#define AppExeName "WinWhisper.exe"

#ifndef MyAppSourceDir
  #define MyAppSourceDir "..\bin\WinWhisper\Release\net48"
#endif

; 既定の出力先。CI からは /O で上書きする
#ifndef MyOutputDir
  #define MyOutputDir "..\bin\installer"
#endif

[Setup]
AppId={{C41B9A07-6E2D-4F58-8B31-7A9E52D46F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; .NET Framework 4.8 は Windows 11 に同梱されているため、検出のみ行い導入はしない
MinVersion=10.0.22000
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
LicenseFile=license.txt
UninstallDisplayIcon={app}\{#AppExeName}
DisableDirPage=auto
DisableReadyPage=no
; 32bit のシェルから起動されても 64bit モードで動かす
SetupLogging=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; アプリ本体（win-x64 のネイティブを含む）
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; ドキュメント
Source: "..\README.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\README.ja.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; ログと設定は残す（利用者が意図的に消せるように %LOCALAPPDATA% 側に置いてある）。
; アプリ本体のフォルダだけ消す。
Type: filesandordirs; Name: "{app}"
