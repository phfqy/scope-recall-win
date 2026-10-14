---
name: sr-cloud-memory
description: >-
  读写 Scope Recall 云端同步记忆（远端 agent 用）。当 agent 位于远端机器、
  需要检索本机 Scope Recall 共享记忆库同步到云端的记忆，或要把远端的新知写回
  主人记忆库时使用。核心是一个单文件 CLI：sync（拉到本地镜像）/ recall（检索）/
  show（详情）/ propose（写回，经 home 桥转官方 propose_memory）。
---

# 云端记忆读写（远端 agent）

## 何时用

- 你在**远端机器**（非记忆库宿主机），需要主人共享记忆库里的背景知识 → 先 `sync` 再 `recall`。
- 你在远端发现了值得主人长期记住的事实/偏好 → `propose` 写回（**只走官方 propose_memory 管道**，
  落库前会经 home 桥与插件的证据累积/去重，不直写共享库）。

## 一次性准备

1. 从资料库下载 `sr_cloud_remote.py`（单文件，Python 3.10+，local 后端零依赖）。
2. 向主人要同步端点参数（backend=webdav/s3 时的 url/user/pwd 或 bucket/endpoint/key；
   与主人桌面端「云同步」页配置的是同一份云端存储）。凭据建议走环境变量：
   `SR_CLOUD_BACKEND/ROOT/URL/USER/PWD/BUCKET/ENDPOINT/ACCESS_KEY/SECRET_KEY`。
   **凭据纪律**：别写进命令行参数（同机其他用户可见）、别粘贴到聊天/AI 会话
   （记录可能被存档）；怀疑泄露时去坚果云网页版「安全选项」删掉该应用密码完成轮换。

> **根路径（`--root` / `SR_CLOUD_ROOT`）**：云端布局固定是 `state.json` + `records/<sha1>.json`，
> 但 `records/` 所在层因人而异——平铺在**云端根**就**留空**（旧文档例 `scope-recall`
> 只适用于把库放进 `scope-recall/` 目录的人，照抄会扑空）。自检：`status --cloud` 看
> `cloud.records` 是否等于主人说的记录数，偏小就先换 root 再试。

## 命令

```bash
# 拉取云端 → 本地镜像（增量，只传变化；首次全量）
python sr_cloud_remote.py sync --backend webdav --url $SR_CLOUD_URL --user $SR_CLOUD_USER --pwd $SR_CLOUD_PWD   # --root 留空=云端根

# 检索（在本地镜像上，LIKE 匹配 subject/predicate/value_text/content）
python sr_cloud_remote.py recall "坚果云" --limit 20
python sr_cloud_remote.py recall "浏览器" --kind fact --json     # 机器可读（--json 仅 recall/show 有）

# 详情
python sr_cloud_remote.py show <ref>

# 状态（镜像统计 + --cloud 附云端概览）
python sr_cloud_remote.py status --cloud --backend webdav …

# 写回：提议一条记忆（主人侧桥拉到后会转 propose_memory，经证据累积进共享库）
python sr_cloud_remote.py propose --subject "DSH" --predicate "uses" \
  --value "Zen browser 打开外部链接" --kind fact
```

## 约定与边界

- `propose` 的正文会拼成「主语 谓语 值」，经 home 桥 → 官方 `propose_memory` → 证据累积管道；
  **重复内容会被去重**（本地内容哈希 + 主人侧 source_events 精确匹配）。
- 写入的是"提议"（proposed），是否转正由主人侧记忆系统的规则决定——**不要期望即时可见**。
- `sync` 对记录做 SHA256 完整性校验，校验不过的记录宁缺毋滥直接跳过。
- 云端 `state.json` 是共享协调账本：读改写、保留其它写入者的条目；不要手工编辑。
  `state.json` 缺失而 `records/` 有记录时，工具扫描重建索引并告警；此时**写入默认被拒绝**
  （防账本被覆盖成一条），确认无虑后加 `--rebuild-state`。
- 语义检索（向量）暂不支持——云端镜像只有词法通道。
- 坚果云免费版高频会 503 限流（`BlockedTemporarily`，账号级封锁数分钟）。CLI 已内置
  429/5xx 指数退避重试（至多 6 次）与逐条节流（`--throttle`，默认 0.5s），重试耗尽会
  明确报错并提示等待，不会静默失败；大批量操作仍请分批。
