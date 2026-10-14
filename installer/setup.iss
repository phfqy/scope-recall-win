; Scope Recall Win — 桌面安装包（Inno Setup 6）
; 特性：per-user 安装（免管理员）、开始菜单 + 桌面快捷方式、标准卸载器
;
; 构建步骤（本机复现）：
;   1. dotnet publish -c Release -r win-x64 -p:Platform=x64
;      -p:WindowsAppSDKSelfContained=false -o installer\publish\run
;      注意两点（实测踩坑）：
;        a) 必须用「框架依赖」模式（SelfContained=false）：self-contained 在本机会
;           以 CoreMessagingXP.dll 触发 fail-fast（0xC0000602）；
;        b) RID publish 不会复制 XAML 编译产物，需手动补齐：
;           bin\x64\Release\<tfm>\win-x64\**\*.xbf  →  publish\run\（保持相对路径）
;           bin\x64\Release\<tfm>\win-x64\*.pri      →  publish\run\
;   2. ISCC.exe setup.iss  →  out\ScopeRecallWin-Setup-<版本>.exe
;
; 目标机需已安装 Windows App Runtime 1.8（框架依赖模式的前提；
; 随本仓库 Release 提供 Microsoft.WindowsAppRuntime.1.8.msix）。
; CI（GitHub Actions）按上述步骤先 dotnet publish 再调用 ISCC。

#define MyAppName "Scope Recall"
#define MyAppNameZh "范围检索 · Scope Recall"
#define MyAppVersion "1.3.10"
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
OutputBaseFilename=ScopeRecallWin-Setup-1.3.10
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
