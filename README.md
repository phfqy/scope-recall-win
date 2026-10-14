# Scope Recall Win

[![build](https://github.com/phfqy/scope-recall-win/actions/workflows/build.yml/badge.svg)](https://github.com/phfqy/scope-recall-win/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/phfqy/scope-recall-win)](https://github.com/phfqy/scope-recall-win/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010.0.19041%2B-blue.svg)](https://learn.microsoft.com/windows/apps/windows-app-sdk/)

**Scope Recall Win** 是 Scope Recall v3 记忆核心的 Windows 桌面管理控制台，同时提供**记忆云同步桥**：
把本机共享记忆库变成看得见、管得着的桌面应用，并让**远端 agent 也能读写同一份记忆**。

- 🖥️ **控制台**：19 个视图（总览 / 记忆 / 检索 / 档案 / 队列 / 候选 / 审计 / 诊断 / 升级 / 云同步…），
  WinUI 3（Windows App SDK）+ C#/XAML，Fluent 明暗双主题 + Mica，unpackaged 免管理员安装。
- ☁️ **云桥**：把共享记忆增量同步到云端（坚果云 WebDAV / S3 兼容），
  按 `state.json` 协调账本**逐条只传变化**，带 SHA256 完整性校验与空库保护。
- 🤖 **远端接入**：远端 agent 读写云端记忆 —— `remote/sr_cloud_remote.py`（CLI）是**唯一核心实现**，
  MCP Server 与 SKILL 是它的两种接入形式（MCP 只是 import 复用同一批命令函数，SKILL 是纯说明书）。
  写入经云桥转官方 `propose_memory` 落库，控制台按 `origin_agent` 显示「谁在连」。

> 三者共用**同一份云端账本**：`state.json` + `records/<sha1(ref)>.json`（协议 `scope-recall.incremental-sync/1`）。
> 选型判据：客户端原生支持 MCP → 用 MCP（工具签名有 schema，调用更规范）；否则 → 用 CLI
> （唯一选择，且 local 后端零第三方依赖）。MCP 底层就是调 CLI，二者共享同一份代码与账本。

## 架构

```
        ┌──────────────────────── 本机 ────────────────────────┐
        │  Scope Recall Win 控制台（只读库 + 官方 CLI 编排写） │
        │            │                                         │
        │            ▼                                         │
        │  scope-recall 共享记忆库（只读打开，从不直写）        │
        │            ▲                                         │
        │            │ 官方 propose_memory（云桥转写）          │
        │      bridge/cloudsync_run.py  ← 增量同步桥           │
        └────────────┬─────────────────────────────────────────┘
                     │ 只传变化的记录 + 协调账本
                     ▼
           云端（坚果云 WebDAV / S3）
           state.json + records/<sha1(ref)>.json
                     ▲
                     │ 读写同一份账本（三选一）
        ┌────────────┴────────────┬──────────────────┐
        │                         │                  │
   CLI sr_cloud_remote.py   MCP memory_mcp.py   SKILL sr-cloud-memory
   （零依赖，推荐）          （7 工具，stdio）     （agent 自动调用）
```

## 核心能力

### 控制台（19 视图，全部接真实数据）

- **总览**：健康分 + 待确认候选按原因明细；**记忆 / 时间线 / 检索 / 档案（关系图）**；
  **队列**（失败原因 + 一键重试）/ **候选记录** / **审计**。
- **云同步页**：环境自检（mcp/webdav4/boto3）、专用 venv 选择、备份并同步一轮、
  **远端 agent 链接状态**（读云端账本按 `origin_agent` 聚合）。
- **写通道全部走官方 CLI**（参数装配收口在 `Infrastructure/MaintenanceCli.cs`，静态检查 V8 拦截手写）：
  共享库 attach/detach、备份与回滚（先备份 → 预演 → 执行）、候选重评、失败重试、
  向量重建、**插件升级全流程**（检查 → 备份 → 下载 → 全栈清场 → 换包 → doctor 校验）。
- **诊断页**逐 entry 调官方 `doctor`；**助手连接页**三维校验（配置注册 / resident 常驻 / 进程存活）。
- **体验**：Ctrl+K 命令面板、Ctrl+T 呼出式顶栏检索、明暗主题即时切换、托盘常驻。

### 记忆云同步桥（`bridge/`）

| 脚本 | 职责 |
|---|---|
| `cloudsync_run.py` | 一轮同步：本机库 →（增量）云端，云端 →（去重后）官方 `propose_memory` |
| `incremental_sync.py` | 真实库（schema 1110）只读提取 + 逐条增量上云 + 冲突记录不静默覆盖 |
| `sync_engine.py` | 三后端抽象（local / webdav / s3）+ 状态文件协议 |
| `bridge_plugin.py` | 单机形态的独立云桥（官方 MCP 读写 + 双向同步） |
| `remote_status.py` | 远端链接状态只读探针（按 `origin_agent` 聚合，供控制台调用） |

**流量特性**：无变化时上传 0 字节；改 1 条只传该条（实测 17 字节，全量 582KB）。

### 远端接入（`remote/`）

| 入口 | 文件 | 依赖 |
|---|---|---|
| CLI | `remote/sr_cloud_remote.py` | **零依赖**（local 后端）；webdav 需 `webdav4` |
| MCP | `remote/memory_mcp.py` | `mcp>=2.0` |
| SKILL | `remote/SKILL.md` | — |

CLI 七个命令：`sync` / `recall` / `show` / `status` / `propose` / `revise` / `forget`；
MCP 七个工具：`status` / `recall` / `inspect` / `remember` / `revise` / `forget` / `sync`。
**两个入口读写同一份账本**，任选其一即可；写入是「提议」，由云桥转正，不即时可见。

📖 **完整配置（应用 / MCP / SKILL 三段）见 [`docs/远端接入.md`](docs/远端接入.md)。**

## 快速开始

### 1. 安装控制台

下载 [Release](https://github.com/phfqy/scope-recall-win/releases/latest) 里的
`ScopeRecallWin-Setup-<版本>.exe`，双击运行：

- per-user 安装到 `%LOCALAPPDATA%\Programs\ScopeRecallWin`，**无需管理员权限**
- 自动创建开始菜单 + 桌面快捷方式，自带标准卸载器
- unpackaged 自包含形态，内置 Windows App SDK 运行时

静默安装：`ScopeRecallWin-Setup-<版本>.exe /VERYSILENT`

### 2. 配置云同步

打开 **云同步** 页 → 「开始自检」（需要一个带 `mcp` / `webdav4` / `boto3` 的专用 venv，
建议装在安装目录之外，升级不丢）→ 填云端（坚果云：账号邮箱 + **应用密码**，不是登录密码）
→ 保存配置 → 填授权说明 → 「备份并同步一轮」（同步前先自动备份，备份失败即中止）。

### 3. 让远端 agent 接进来

```bash
# 最省事的一条路：CLI（零依赖）
python remote/sr_cloud_remote.py sync   --backend webdav --url $SR_CLOUD_URL \
       --user $SR_CLOUD_USER --pwd $SR_CLOUD_PWD --root scope-recall
python remote/sr_cloud_remote.py recall "关键词"
python remote/sr_cloud_remote.py propose --subject "X" --predicate "uses" --value "Y"
```

接好后回到控制台云同步页点「检查远端」，就能看到是谁在读写、最近何时。
细节见 [`docs/远端接入.md`](docs/远端接入.md)。

## 安全边界

1. **读 = 只读打开**：`Mode=ReadOnly` + `PRAGMA query_only=1` 双保险，绝不触发迁移。
2. **写 = 只走官方通道**：库写只经官方 `propose_memory` / 维护 CLI 编排；控制台不写一行记忆库 SQL。
3. **远端从不直接写共享库**：远端只能写云端账本，落库一律由本机云桥转写（含去重与证据累积）。
4. **凭据不进明文**：坚果云应用密码走 Windows 凭据管理器 / 环境变量，
   不进命令行参数（进程列表对同机其他用户可见）、不进 `settings.json`、不进日志。
5. **先备份再同步**：同步按钮先调官方 backup 做一致性快照，备份失败即中止。
6. **冲突不静默覆盖**：本地与云端同时改 → 记入 `conflicts` 并保留双方，不做「云端胜」。
7. **空库保护**：本地无有效记忆而云端有数据 → 只拉不推，绝不覆盖。

## 目录结构

```
scope-recall-win/
├── scope-recall-win.csproj      # WinUI 3 + WindowsAppSDK 1.8 + MVVM + Microsoft.Data.Sqlite（unpackaged）
├── MainWindow.xaml / .cs        # 236px 分组导航 + ContentHost + Mica + Ctrl+K 浮层
├── Views/                       # 19 个视图 UserControl（含 CloudSyncView）
├── Services/                    # StorageService（只读）/ CloudSyncService / PluginService / MutateService …
├── Infrastructure/MaintenanceCli.cs   # 官方 CLI 参数装配唯一收口（V8 门禁）
├── Controls/ Theme/ ViewModels/ Helpers/
├── bridge/                      # 记忆云同步桥（Python，随应用分发）
│   ├── cloudsync_run.py         # 一轮同步入口
│   ├── incremental_sync.py      # 真实库增量同步引擎
│   ├── sync_engine.py           # 三后端抽象
│   ├── bridge_plugin.py         # 单机形态云桥
│   └── remote_status.py         # 远端链接状态探针
├── remote/                      # 远端 agent 接入（Python）
│   ├── sr_cloud_remote.py       # CLI（7 命令，零依赖）
│   ├── memory_mcp.py            # MCP Server（7 工具）
│   └── SKILL.md                 # skill 描述
├── docs/远端接入.md              # 应用 / MCP / SKILL 三段详细配置
├── installer/                   # Inno Setup 安装包脚本与产物
└── verify_static.py             # 静态门禁（CI 必过）
```

## 从源码构建

### 方式 A：Visual Studio 2022

安装 VS2022（工作负载：**.NET 桌面开发** + **Windows 应用 SDK**）→ 打开 `scope-recall-win.csproj`
→ 平台选 `x64` → `F5`。

### 方式 B：.NET CLI（无需 VS）

```powershell
dotnet build scope-recall-win.csproj -p:Platform=x64

# 发布 unpackaged 产物（框架依赖模式，依赖本机 Windows App Runtime 1.8）
dotnet publish scope-recall-win.csproj -p:Platform=x64 -c Release -r win-x64 `
  -p:WindowsAppSDKSelfContained=false -o ..\..\publish\run

# 补齐 XAML 编译产物（RID publish 不会复制 .xbf/.pri，缺失会启动即崩）
#   把 bin\x64\Release\net8.0-windows10.0.19041.0\win-x64 下的
#   *.pri（→ 输出根）与 **\*.xbf（保持相对路径）复制进 ..\..\publish\run
```

> ⚠️ **unpackaged 三个硬约束**：`WindowsPackageType=None` 且由 `Program.cs` 显式
> `Bootstrap.TryInitialize` 激活；必须**框架依赖**模式（self-contained 在本机会以
> `CoreMessagingXP.dll` 触发 fail-fast）；发布后必须手动补齐 `.xbf` / `.pri`。

### 制作安装包（Inno Setup 6）

```powershell
ISCC.exe installer\setup.iss
# 产物：installer\out\ScopeRecallWin-Setup-<版本>.exe
```

### 静态门禁与测试

```bash
python verify_static.py            # V1–V8：XML 良构 / 资源可解析 / 路由三对齐 / 括号配平 / 安装器 / CLI 收口
python bridge/test_incremental.py  # 增量同步：零上传短路 / 单条增量 / 冲突保留
python remote/memory_mcp.py --backend local --root ./cloud --selftest   # 远端 7 工具自检
```

## 版本与提交约定

- 版本号**三处对齐**后再打 tag：`scope-recall-win.csproj`（`Version`/`InformationalVersion`）、
  `installer/setup.iss`（`MyAppVersion`/`OutputBaseFilename`）、`verify_static.py`（V6 文件名 token）。
- 安装产物入库：`installer/publish/run/` 与 `installer/out/ScopeRecallWin-Setup-<版本>.exe`。
- 改 XAML 必须同步 `.pri` 与 `*.xbf`，否则界面是旧文案。
- commit message：`fix(scope): 中文一句话（版本号）`，正文列要点。

## License

[MIT](LICENSE)
