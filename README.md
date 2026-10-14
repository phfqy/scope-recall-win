# Scope Recall Win —— WinUI 3 桌面管理控制台

[![build](https://github.com/phfqy/scope-recall-win/actions/workflows/build.yml/badge.svg)](https://github.com/phfqy/scope-recall-win/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/phfqy/scope-recall-win)](https://github.com/phfqy/scope-recall-win/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010.0.19041%2B-blue.svg)](https://learn.microsoft.com/windows/apps/windows-app-sdk/)

**Scope Recall Win** 是 Scope Recall v3 记忆核心的 Windows 桌面管理控制台：
把本机的共享记忆库变成一个看得见、管得着的桌面应用 —— 记忆总览与检索、候选/队列/审计、
共享库与助手接入、部署/诊断/备份/升级，全部在一个 **WinUI 3（Windows App SDK）+ C#/XAML**、
Fluent Design 明暗双主题 + Mica 的 18 视图原生应用里完成。

**读写边界（安全设计）**：读 = 直连 `memory.sqlite3`（`Mode=ReadOnly` + `query_only` 双保险，
绝不 import 上游包、绝不触发迁移）；写 = 只经官方维护 CLI 编排（部署/备份/回滚/接入/重评/升级），
控制台本身不写记忆库。**分发形态：unpackaged exe 桌面安装版**（Inno Setup，per-user 免管理员），
不产出、不分发 MSIX。

**📦 直接下载安装**：[Releases → v1.3.1](https://github.com/phfqy/scope-recall-win/releases/latest)
（`ScopeRecallWin-Setup-1.3.1.exe`，含安装说明与 SHA256）

## 当前能力（v1.3.1）

- **18 个视图全部接真实数据**：总览（健康分 + 待确认候选按原因明细）/ 记忆 / 时间线 / 检索 / 档案
  （关系图 + 多维排序）/ 队列（失败原因 + 一键重试）/ 候选记录 / 审计 / 检索定位。
- **写通道全部走官方 CLI**（参数装配统一收口在 `Infrastructure/MaintenanceCli`，静态检查 V8 拦截手写）：
  部署（init-shared / venv / wheel）、共享库 attach/detach、备份与回滚（先自动备份→预演→执行）、
  候选重评、失败重试、向量重建、插件升级（六步：检查→备份→下载→停机→换包→doctor 校验）。
- **诊断页**逐 entry 调官方 `doctor`，报告可复制/可存档；**助手连接页**三维校验（配置注册 /
  resident 常驻 / 进程存活），Hermes 通道单独识别。
- **体验**：Ctrl+K 命令面板、Ctrl+T 呼出式顶栏检索（呼出/收起，Esc 关闭，Ctrl+F 聚焦）、
  明暗主题即时切换 + 标题栏配色同步、关系图缩放、托盘常驻。

## 安装（Windows，免管理员）

下载 [Release 附件](https://github.com/phfqy/scope-recall-win/releases/latest) 的
`ScopeRecallWin-Setup-1.3.1.exe`，双击运行：

- per-user 安装到 `%LOCALAPPDATA%\Programs\ScopeRecallWin`，**无需管理员权限**
- 自动创建开始菜单 + 桌面快捷方式，自带标准卸载器（设置 → 应用 → 卸载）
- unpackaged 自包含形态，内置 Windows App SDK 运行时，不依赖 MSIX 部署

静默安装：

```powershell
ScopeRecallWin-Setup-1.3.1.exe /VERYSILENT
```

## 打开方式（源码运行 / 二次构建）

### 方式 A：Visual Studio 2022

1. 安装 VS2022（工作负载：**.NET 桌面开发** + **Windows 应用 SDK**，勾选"Windows 应用 SDK C# 模板"）。
2. 打开 `scope-recall-win.csproj`（首次自动 NuGet 还原）。
3. 平台选 `x64`，`F5` 运行（unpackaged 桌面形态）。

### 方式 B：.NET CLI（无需 VS，已验证）

```powershell
# 1. 安装 .NET 8 SDK（用户级，无需管理员）
winget install Microsoft.DotNet.SDK.8
#    或：https://dot.net/v1/dotnet-install.ps1 脚本安装

# 2. 构建（x64，unpackaged）
dotnet build scope-recall-win.csproj -p:Platform=x64

# 3. 发布 unpackaged 产物（框架依赖模式，依赖本机 Windows App Runtime 1.8）
dotnet publish scope-recall-win.csproj -p:Platform=x64 -c Release -r win-x64 `
  -p:WindowsAppSDKSelfContained=false -o ..\..\publish\run

# 4. 补齐 XAML 编译产物（RID publish 不会复制 .xbf/.pri，缺失会启动即崩）
#    把 bin\x64\Release\net8.0-windows10.0.19041.0\win-x64 下的
#    *.pri（→ 输出根）与 **\*.xbf（保持相对路径）复制进 ..\..\publish\run
```

> ⚠️ **WindowsAppSDK 版本**：工程锁定 `Microsoft.WindowsAppSDK 1.8.260921001`（1.x 末代稳定版）。
> 1.6 的外部 `XamlCompiler.exe` 在部分环境被安全策略静默拦截（exit 1 无输出），1.7+ 的
> in-proc XAML 编译已修复该问题——升级路径即此。

> ⚠️ **unpackaged 三个硬约束**（均为实机踩坑结论）：
> 1. `WindowsPackageType=None`，Windows App Runtime 由 `Program.cs` 显式
>    `Bootstrap.TryInitialize` 激活（不用自动注入的 `WindowsAppSdkBootstrapInitialize`）；
> 2. 必须用**框架依赖**模式（`WindowsAppSDKSelfContained=false`）——self-contained
>    在本机会以 `CoreMessagingXP.dll` 触发 fail-fast（`0xC0000602`）；
> 3. 发布后必须手动补齐 `.xbf` / `.pri`（见上面第 4 步，缺失则启动即崩）。
>    目标机需已安装 Windows App Runtime 1.8（随 Release 提供依赖包）。

### 从源码制作安装包（Inno Setup 6）

```powershell
# 接上面第 3 步的 publish 输出（installer/publish/run），然后：
ISCC.exe installer\setup.iss
# 产物：installer\out\ScopeRecallWin-Setup-1.0.0.exe
```

## 目录结构

```
scope-recall-win/
├── scope-recall-win.csproj        # WinUI 3 + WindowsAppSDK + MVVM + Microsoft.Data.Sqlite（unpackaged）
├── installer/                     # Inno Setup 桌面安装包
│   ├── setup.iss                  # 安装器脚本（per-user 免管理员 / 中英文 / 快捷方式）
│   ├── app.ico                    # 安装器与快捷方式图标
│   └── ChineseSimplified.isl      # 简体中文安装语言文件
├── app.manifest                   # DPI 感知（PerMonitorV2）
├── App.xaml / App.xaml.cs         # 应用入口；主题字典装配
├── MainWindow.xaml / .cs          # 236px 分组导航 + ContentHost + Mica + ⌘K 浮层 + 页面滑动动画
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
│   └── StartupService.cs          # 开机自启占位（P4：注册表 Run key）
├── Helpers/
│   ├── TimestampNormalizer.cs     # +00:00 微秒 与 Z 两种时间戳归一
│   └── CsvExporter.cs             # CSV 转义 + 公式注入防护（= + - @ 前置单引号）
└── verify_static.py               # 静态验证（7 类检查，CI 必过）
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
