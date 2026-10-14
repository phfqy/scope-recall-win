---
name: sr-cloud-memory
description: >-
  读写 Scope Recall 云端共享记忆（远端 / 跨会话 agent 用）。当你要读取主人记忆库同步到
  云端的背景知识，或要把新发现的事实、偏好、决策写回主人记忆库时使用。两个等价入口：
  sr_cloud_remote.py（零依赖 CLI）与 memory_mcp.py（MCP Server，7 工具）。
  关键词：远端记忆、云端记忆、共享记忆、scope-recall、跨端记忆、记忆同步。
---

# 云端记忆读写（Scope Recall）

本技能让你读写**主人记忆库同步到云端的那份记忆**。写入不会直接落库，
而是上云为 `proposed`，由主人侧云桥经官方 `propose_memory` 转写（带证据累积与去重）。

## 两个入口，选一个

| 入口 | 适合 | 依赖 |
|---|---|---|
| `sr_cloud_remote.py`（CLI） | 任何环境，**首选**；脚本、cron、无 MCP 客户端 | **零第三方依赖**（local 后端）；webdav 需 `pip install webdav4`，s3 需 `boto3` |
| `memory_mcp.py`（MCP Server） | 客户端原生支持 MCP（Codex / Claude / Hermes） | `pip install "mcp>=2.0"` |

**两者读写同一份云端账本**（`state.json` + `records/<sha1(ref)>.json`），
效果完全等价——任选其一，不要同时开两个写通道做同一件事。

## 准备（凭据走环境变量，别写进命令行）

```bash
export SR_CLOUD_BACKEND=webdav            # local | webdav | s3
export SR_CLOUD_URL=https://dav.jianguoyun.com/dav/
export SR_CLOUD_USER=<账号邮箱>
export SR_CLOUD_PWD=<应用密码>             # 坚果云只认应用密码，登录密码会 401
export SR_CLOUD_ROOT=scope-recall         # 云端根路径/prefix
export SR_CLOUD_DB=./sr-cloud-mirror.sqlite3
```

## CLI 用法

```bash
python sr_cloud_remote.py sync     # 拉云端 → 本地镜像（增量 + SHA256 校验）
python sr_cloud_remote.py recall "关键词" [--limit 20] [--kind fact] [--json]
python sr_cloud_remote.py show <ref>
python sr_cloud_remote.py status --cloud
python sr_cloud_remote.py propose --subject "DSH" --predicate "uses" --value "Zen browser" --kind fact
python sr_cloud_remote.py revise <ref> --value "新内容"     # 默认只改自己提议的
python sr_cloud_remote.py forget <ref>                      # 软删除，主人侧不再转写
```

- `propose` 后拿到的 `ref` 可用于后续 `revise` / `forget`。
- 典型顺序：**先 `sync`，再 `recall`**。

## MCP 用法

```bash
python memory_mcp.py --db $SR_CLOUD_DB --backend webdav \
  --url $SR_CLOUD_URL --user $SR_CLOUD_USER --pwd $SR_CLOUD_PWD --root $SR_CLOUD_ROOT
```

7 个工具：`status` / `recall` / `inspect` / `remember` / `revise` / `forget` / `sync`
（`remember` 即 CLI 的 `propose`）。

无 `mcp` 包时可用 `--selftest` 直接验证全部逻辑（不起服务）：

```bash
python memory_mcp.py --backend local --root ./cloud --selftest
```

## 边界（重要）

- 写入是**提议**，是否转正由主人侧记忆系统规则决定，**不要期望即时可见**。
- `revise` / `forget` 默认只作用于**你自己（`origin_agent`）提议的**记录；
  动别人的记录要显式 `--force` / `force=true`，且应当先问过主人。
- 云端 `state.json` 是共享协调账本：读改写、保留其他写入者条目，**不要手工编辑**。
- 只有词法检索（LIKE），**没有向量语义检索**。
- 坚果云免费版高频会 503 限流；写入天然低频，大批量操作请分批。
