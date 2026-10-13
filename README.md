# Scope Recall Win —— WinUI 3 桌面管理控制台（工程骨架）

Scope Recall v3 记忆核心的 Windows 桌面管理控制台。**WinUI 3（Windows App SDK）+ C#/XAML**，
18 视图全功能规划，UI 采用 Fluent Design 自由设计（不复制 WebUI 风格），功能覆盖以
`WIN-APP-PROMPTS.md` 的【附 1 功能全景对照表】为准。

> 本目录是 **P0 骨架**：可打开、可导航、主题可切换，服务层为接口/占位实现。
> P1（视图与交互）、P2（读写数据通道）、P3（runtime-config 编辑器 + worker 启停）、
> P4（更新/安装卸载/共享 store/托盘/自启）按提示词包逐项填充。
>
> 2026-10-13 修复轮（依据《ScopeRecall-开发测试报告》5.2 优先级）：P0-1 补齐 csproj 与
> app.manifest；P0-2 补齐 9 个缺失视图并重写 OpsView.xaml；P0-3 补齐 Assets/ 6 张 MSIX
> 图标（generate_icons.py 可复现）；P1-1 重设计 MutateService（对齐上游 revise/forget
> 工具面，删除 create 枚举）；P1-2 Claims 状态口径改五状态 + suppressed 列；P1-3 文案对齐。
>
> 2026-10-13 编译轮：装 .NET 8 SDK 后在 Windows 11 实测 **dotnet build / publish 通过**，
> 修复 3 个编译级问题（MicaKind 命名空间在 Microsoft.UI.Composition.SystemBackdrops；
> WinUI 3 无 TextCharacterSpacing 改用 CharacterSpacing；startupTask 扩展 Executable
> token 不替换改为缺省主程序 + EntryPoint=Windows.FullTrustApplication），
> WindowsAppSDK 升级 1.6→1.8，产出签名 MSIX 成品。

## 打开方式（Windows）

### 方式 A：Visual Studio 2022

1. 安装 VS2022（工作负载：**.NET 桌面开发** + **Windows 应用 SDK**，勾选"Windows 应用 SDK C# 模板"）。
2. 打开 `scope-recall-win.csproj`（首次自动 NuGet 还原）。
3. 平台选 `x64`，`F5` 运行（MSIX packaged 部署）。

### 方式 B：.NET CLI（无需 VS，已验证）

```powershell
# 1. 安装 .NET 8 SDK（用户级，无需管理员）
winget install Microsoft.DotNet.SDK.8
#    或：https://dot.net/v1/dotnet-install.ps1 脚本安装

# 2. 构建（x64）
dotnet build scope-recall-win.csproj -p:Platform=x64

# 3. 产出可安装 MSIX（已用测试证书签名）
dotnet publish scope-recall-win.csproj -p:Platform=x64 -c Release `
  -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=<你的证书指纹>
# 产物：AppPackages\scope-recall-win_1.0.0.0_x64_Test\scope-recall-win_1.0.0.0_x64.msix
```

> ⚠️ **WindowsAppSDK 版本**：工程锁定 `Microsoft.WindowsAppSDK 1.8.260921001`（1.x 末代稳定版）。
> 1.6 的外部 `XamlCompiler.exe` 在部分环境被安全策略静默拦截（exit 1 无输出），1.7+ 的
> in-proc XAML 编译已修复该问题——升级路径即此。

> ⚠️ **签名**：交付用 MSIX 需有效代码签名证书（证书 Subject 须与 `Package.appxmanifest`
> 的 `Publisher` 一致）。开发/测试可自签名：`New-SelfSignedCertificate -Type CodeSigningCert`，
> 经 `certutil -user -addstore Root <cer>` 导入当前用户根存储后即可 sideload。

### 运行/安装

- VS F5 / `dotnet build` 后由开发机直接部署（packaged 模式）。
- 分发 `.msix`：目标机安装依赖 `Microsoft.WindowsAppRuntime.1.8.msix`（随包 Dependencies 目录），
  再 `Add-AppxPackage -Path xxx.msix -DependencyPath ...\Microsoft.WindowsAppRuntime.1.8.msix`。
- 本仓库附带的 `install_msix.ps1` 演示"导入测试证书 → 安装 → 启动 → 截图"的完整冒烟流程
  （需以管理员身份运行一次）。

## 目录结构

```
scope-recall-win/
├── scope-recall-win.csproj        # WinUI 3 + WindowsAppSDK + MVVM + Microsoft.Data.Sqlite
├── Assets/                        # MSIX 图标（StoreLogo/Square44/150/Wide310/SmallTile/LargeTile）
├── generate_icons.py              # 图标生成脚本（Pillow，可复现 Assets/）
├── app.manifest                   # DPI 感知（PerMonitorV2）
├── Package.appxmanifest           # MSIX 打包；已声明 StartupTask（默认关闭）
├── App.xaml / App.xaml.cs         # 应用入口；主题字典装配
├── MainWindow.xaml / .cs          # 236px 分组导航 + Frame + Mica + ⌘K 浮层 + 页面滑动动画
├── Theme/
│   ├── ColorsAndBrushes.xaml      # 语义色（Success/Warning/Danger/Info）+ 等宽字体
│   ├── Dark.xaml / Light.xaml     # 明暗两套 token（Fluent）
│   └── FluentControls.xaml        # 卡片/导航分组/危险按钮/列表动画样式
├── Controls/
│   ├── Card.xaml/.cs              # 通用卡片（标题 + 内容）
│   ├── StatBadge.xaml/.cs         # 状态徽章（ok/warn/danger/info，入场动画）
│   └── StatusLed.xaml/.cs         # 诊断 LED 灯（语义色）
├── Views/                         # 18 个视图 UserControl
│   ├── OverviewView               # 已实现示例：统计卡 + 健康分 + 需要关注（演示数据）
│   └── Claims/Timeline/Query/Profile/Queue/Doctor/Ops/Mutate/Audit/Search/
│       AiInfo/AiChanges/AiSettings/Update/Install/SharedStore/Settings
│                                   # 占位：每个视图标注职责与对应功能点
├── ViewModels/MainViewModel.cs    # MVVM（CommunityToolkit.Mvvm）
├── Services/
│   ├── StorageService.cs          # 只读查询（P2：Microsoft.Data.Sqlite Mode=ReadOnly）
│   ├── MutateService.cs           # 人工修改（Hermes adapter 工具面 revise/forget，授权句+备份+留痕）
│   ├── WorkerService.cs           # worker 启停（P3）
│   ├── UpdateService.cs           # 版本检查（P4：GitHub release + 镜像）
│   ├── CredentialService.cs       # 凭据（P3：Windows Credential Manager，Key 不明文）
│   ├── TrayService.cs             # 托盘占位（P4）
│   └── StartupService.cs          # 开机自启占位（P4：MSIX StartupTask）
└── Helpers/
    ├── TimestampNormalizer.cs     # +00:00 微秒 与 Z 两种时间戳归一
    └── CsvExporter.cs             # CSV 转义 + 公式注入防护（= + - @ 前置单引号）
```

## 设计参考（GitHub 开源方案与动画效果）

| 参考来源 | 借鉴内容 | 落地位置 |
|---|---|---|
| [microsoft/WinUI-Gallery](https://github.com/microsoft/WinUI-Gallery) | Fluent 控件观感、导航交互、状态语义色 | Theme/、Controls/ |
| [files-community/Files](https://github.com/files-community/Files) | 紧凑卡片、工程管理信息密度、深色控制台观感 | Controls/Card、OverviewView |
| [CommunityToolkit/Windows](https://github.com/CommunityToolkit/Windows) | MVVM、托盘/通知（P4 引入） | ViewModels/、P4 挂载点 |
| MS Learn「[Motion / Page transitions](https://learn.microsoft.com/windows/apps/design/motion/page-transitions)」 | 同级视图水平滑动（Slide）、入场（Entrance） | MainWindow.xaml.cs、FluentControls.xaml |
| MS Learn「[Implicit Animations / Spring](https://learn.microsoft.com/windows/apps/develop/motion/motion-in-practice)」 | 状态元素隐式动画、呼吸灯扩展点 | StatusLed（注释）、P1 扩展 |

## 已实现的动画清单

1. **页面切换**：同级视图 `SlideNavigationTransitionInfo FromRight`（水平滑动）；启动页 `EntranceNavigationTransitionInfo`（上滑入场）。
2. **列表/卡片入场**：`EntranceThemeTransition FromHorizontalOffset=24` + `AddDeleteThemeTransition`（ListView 样式）。
3. **徽章入场**：StatBadge 自带 `EntranceThemeTransition`（12px 水平滑入）。
4. **LED 状态点**：StatusLed 入场淡入上移；P1 可扩展 Composition 呼吸灯。
5. **⌘K 命令面板**：Acrylic 浮层（配色）+ 键盘加速器 Ctrl+K（交互逻辑 P1 补全）。

## 硬性约束速记（详见 WIN-APP-PROMPTS.md 第 1 节）

- 写入只走 Hermes adapter 工具面 `revise` / `forget`（协议 v1.1，身份由 provider 绑定）；上游 CLI 无 mutate 子命令、无 create 变更；禁止裸 SQL 写库；只读用 `Mode=ReadOnly`。
- 授权句必填（不代笔/不预填）；危险操作（delete/forget/purge/迁移）二次确认红字。
- AI 只被配置、不直接写库；Key 走凭据管理器，值绝不出现在 JSON/日志。
- 更新/迁移前必备份、哈希不过必回滚、不碰 `<STORE>`；卸载默认保留数据。
- store 文本一律纯文本展示（无 XAML 注入面）；CSV 导出防公式注入。

## 下一步（按提示词包分工）

| 任务 | 对应 P | 本骨架现状 |
|---|---|---|
| 视图与交互补全（⌘K/过滤/导出/订阅） | P1 | 18 视图占位，Overview 示例 |
| 只读查询 + 查询三件套（recall/trace/profile/entity）+ 写入通道 | P2 | StorageService/MutateService 接口占位 |
| runtime-config 全量编辑器 + 模型路由 + worker/autostart | P3 | WorkerService/CredentialService 占位 |
| 一键更新 + 安装/卸载/迁移 + 共享 store + 托盘/自启 | P4 | UpdateService/StartupService/TrayService 占位 |
