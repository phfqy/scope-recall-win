; Scope Recall Win — 桌面安装包（Inno Setup 6）
; 特性：per-user 安装（免管理员）、开始菜单 + 桌面快捷方式、标准卸载器
;
; 构建步骤（本机复现）：
;   1. dotnet publish -c Release -r win-x64 -p:Platform=x64
;      -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
;      -p:WindowsAppSdkBootstrapInitialize=true -o ..\publish\run
;   2. 将 publish 输出放入本目录旁的 publish\run\（或在 [Files] 中改路径）
;   3. ISCC.exe setup.iss  →  out\ScopeRecallWin-Setup-1.0.0.exe
;
; CI（GitHub Actions）同样按上述步骤先 dotnet publish 再调用 ISCC。

#define MyAppName "Scope Recall"
#define MyAppNameZh "范围检索 · Scope Recall"
#define MyAppVersion "1.0.0"
#define MyAppExeName "ScopeRecallWin.exe"
#define MyAppPublisher "phfqy"
#define MyAppURL "https://github.com/phfqy/scope-recall-win"

[Setup]
AppId={{8C7A21D4-5B9E-4F3A-9D61-2C4E8A7B5F10}
AppName={#MyAppNameZh}
AppVersion={#MyAppVersion}
AppVerName={#MyAppNameZh} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\ScopeRecallWin
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=out
OutputBaseFilename=ScopeRecallWin-Setup-1.0.0
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务："

[Files]
Source: "publish\run\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Comment: "{#MyAppNameZh}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon; Comment: "{#MyAppNameZh}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppNameZh}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载后清理运行期日志（用户数据不在安装目录，无需额外保护）
Type: files; Name: "{app}\*.log"
