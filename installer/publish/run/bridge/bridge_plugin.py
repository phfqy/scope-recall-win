#!/usr/bin/env python3
"""bridge_plugin.py — 云桥 entry：插件共享库 ↔ 云端镜像记忆 双向同步。

本机侧部署一个"云桥 agent"：
  - 左侧：scope-recall 插件共享库（本机）。本桥以 cloud entry 身份，通过插件官方 MCP
    server（codex 适配器）读写 —— 插件完全视本桥为一个 attached agent：
    * 云端新记忆 → propose_memory 写入插件库（走"证据累积"管道，插件 recall 可见）
    * 插件库新事件 → 只读提取 → 转云端镜像 → 上传（远端 agent 可见）
  - 右侧：云端镜像（memory.sqlite3 + memory.jsonl，简化 claims 格式，与远端 memory_mcp 互通）

用法：
  python3 bridge_plugin.py --backend local --root ../cloud-root
  python3 bridge_plugin.py --backend webdav --url http://127.0.0.1:8800 --user admin --pwd admin123
"""
import os, sys, json, sqlite3, time, uuid, argparse, threading

BASE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, BASE)
from sync_engine import make_backend, Syncer, hashfile
from memory_mcp import MemoryStore, DB_SCHEMA

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLUGIN_ENTRY_HOME = os.environ.get("SR_PLUGIN_HOME", os.path.join(PROJECT_ROOT, "home-cloud"))
PLUGIN_STORE = os.environ.get("SR_PLUGIN_STORE", os.path.join(PROJECT_ROOT, "shared-store", "memory.sqlite3"))
BRIDGE_LOCAL = os.environ.get("SR_BRIDGE_LOCAL", os.path.join(BASE, "bridge-local"))
BRIDGE_DB = os.path.join(BRIDGE_LOCAL, "memory.sqlite3")
BRIDGE_JSONL = os.path.join(BRIDGE_LOCAL, "memory.jsonl")

THREAD_ID = str(uuid.uuid4())  # 桥进程级固定线程，模拟一个持续会话的 cloud agent


class PluginConn:
    """插件官方 MCP server（cloud entry）客户端：常驻 stdio 会话"""
    def __init__(self, home=PLUGIN_ENTRY_HOME, host="codex"):
        self.home, self.host = home, host
        self.session = None

    async def _ensure(self):
        if self.session is None:
            from mcp import ClientSession, StdioServerParameters
            from mcp.client.stdio import stdio_client
            import anyio
            self._read, self._write = await anyio.open_process(
                [sys.executable, "-X", "utf8", "-m", "scope_recall.adapters.clients.mcp_entry",
                 "--home", self.home, "--host", self.host],
                stderr=None)
            # 简单方案：每次调用临时建会话（插件 server 无状态，代价可接受）
        return self.session

    def call(self, tool, args, timeout=60):
        """同步包装：起一次性 stdio 会话调用插件 MCP 工具"""
        import asyncio
        from mcp import ClientSession, StdioServerParameters
        from mcp.client.stdio import stdio_client
        from mcp import StdioServerParameters as SP
        params = SP(command=sys.executable,
                    args=["-X", "utf8", "-m", "scope_recall.adapters.clients.mcp_entry",
                          "--home", self.home, "--host", self.host])
        async def _run():
            async with stdio_client(params) as (r, w):
                async with ClientSession(r, w) as s:
                    await s.initialize()
                    res = await s.call_tool(tool, args, meta={"threadId": THREAD_ID})
                    return res.content[0].text if res.content else str(res)
        return asyncio.run(_run())

    def propose(self, content, reason=None):
        """写入一条记忆到插件共享库（cloud entry 身份）"""
        args = {"protocol_version": "1.1", "content": content}
        if reason:
            args["reason"] = reason
        return self.call("propose_memory", args)

    def recall(self, query, max_items=3):
        """插件侧查重/召回"""
        return json.loads(self.call("recall", {"protocol_version": "1.1", "query": query,
                                               "mode": "auto", "max_items": max_items}))


def read_plugin_events(store_path):
    """只读提取插件共享库全部 source_events → 简化记忆行"""
    if not os.path.exists(store_path):
        return []
    con = sqlite3.connect(f"file:{store_path}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    rows = con.execute(
        "SELECT event_id, content, origin, role, entry_id, occurred_at, recorded_at "
        "FROM source_events WHERE suppressed=0 AND read_blocked=0 ORDER BY recorded_at").fetchall()
    con.close()
    out = []
    for r in rows:
        out.append({
            "ref": r["event_id"],
            "subject": "",
            "predicate": "notes",
            "value_text": r["content"],
            "kind": r["origin"] or "event",
            "entry": r["entry_id"],
            "created_at": r["occurred_at"],
            "updated_at": r["recorded_at"],
        })
    return out


def plugin_rows_to_jsonl(rows, path):
    """把插件事件行写入 jsonl（ref 稳定，供 Syncer 上传）"""
    with open(path, "w", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
    return len(rows)


class Bridge:
    def __init__(self, backend):
        os.makedirs(BRIDGE_LOCAL, exist_ok=True)
        self.backend = backend
        self.syncer = Syncer(backend, local_dir=BRIDGE_LOCAL)
        self.plugin = PluginConn()
        self.store = MemoryStore(BRIDGE_DB)

    def plugin_has_content(self, value_text, plugin=None):
        """确定性去重：直接查插件库 source_events.content 精确匹配。
        不用插件 recall——propose 只落 source_events（候选），未配 worker 时不在 recall 召回域，
        recall 会永远 no_match 导致每轮重复写入。"""
        try:
            con = sqlite3.connect(PLUGIN_STORE)
            hit = con.execute("SELECT 1 FROM source_events WHERE content = ? LIMIT 1", (value_text,)).fetchone()
            con.close()
            return hit is not None
        except Exception as e:
            print(f"  [查重失败] {e}")
            return False

    def cloud_to_plugin(self):
        """PULL 云端→插件：桥镜像 jsonl 中未入插件库的记忆 → propose_memory"""
        # 1) 云端→桥镜像（拉取远端写入；本地缺失的 jsonl 首次直接下载）
        for act, msg in self.syncer.sync_all(BRIDGE_LOCAL):
            if act != "none":
                print(f"  [云拉] {msg}")
        if not os.path.exists(BRIDGE_JSONL) and self.backend.meta("memory.jsonl"):
            meta = self.backend.download("memory.jsonl", BRIDGE_JSONL)
            print(f"  [云拉] 首次拉取 memory.jsonl")
        # 2) 读桥镜像 jsonl（云端合并内容）
        if not os.path.exists(BRIDGE_JSONL):
            return 0
        written = 0
        for line in open(BRIDGE_JSONL, encoding="utf-8"):
            row = json.loads(line)
            if row.get("entry", "").startswith(("cloud:", "cloud-")) or row.get("entry") == "cloud":
                continue  # 桥自己写的不重复回灌
            content = row["value_text"]
            if row.get("subject"):
                content = f"{row['subject']} {row.get('predicate','')} {row['value_text']}"
            if self.plugin_has_content(content, self.plugin):
                continue
            r = self.plugin.propose(content, reason="cloud-sync")
            print(f"  [云→插件] 写入: {content[:50]}… -> {r[:120]}")
            written += 1
            time.sleep(0.2)  # 避免过密调用
        return written

    def plugin_to_cloud(self):
        """PUSH 插件→云端：插件库新事件 → 桥镜像（合并进 memory.sqlite3/jsonl）→ 上传"""
        rows = read_plugin_events(PLUGIN_STORE)
        # 合并进镜像库（upsert by ref，不破坏远端行；events 不单独上云）
        con = sqlite3.connect(BRIDGE_DB)
        # 自愈：远端库可能缺桥结构列（如 source_agent），缺则补列
        cols = [r[1] for r in con.execute("PRAGMA table_info(claims)")]
        if "source_agent" not in cols:
            con.execute("ALTER TABLE claims ADD COLUMN source_agent TEXT DEFAULT 'remote-agent'")
            print("  [!] 桥镜像 claims 缺 source_agent 列，已自愈补列")
        added = 0
        for r in rows:
            cur = con.execute(
                "INSERT OR IGNORE INTO claims(ref,subject,predicate,value_text,kind,claim_state,created_at,updated_at,source_agent) "
                "VALUES(?,?,?,?,?,?,?,?,?)",
                (r["ref"], r["subject"], r["predicate"], r["value_text"], r["kind"],
                 "active", r["created_at"], r["updated_at"], "plugin-entry"))
            added += cur.rowcount
        con.commit(); con.close()
        if added:
            print(f"  [插件→桥] 新增 {added} 条插件事件入桥镜像库")
            # dump 完整 jsonl（桥库全部行）
            self.store.dump_jsonl(BRIDGE_JSONL)
        # 上传云端（只同步 memory.sqlite3 + memory.jsonl）
        for act, msg in self.syncer.sync_all(BRIDGE_LOCAL):
            if act != "none":
                print(f"  [云推] {msg}")
        return added

    def run_once(self):
        print("── 轮次 ──")
        self.cloud_to_plugin()
        self.plugin_to_cloud()

    def watch(self, interval=5.0):
        print(f"[bridge] 云桥运行中（插件库={PLUGIN_STORE} 云端={type(self.backend).__name__} 轮询={interval}s, Ctrl-C 停止）")
        while True:
            try:
                self.run_once()
            except Exception as e:
                print(f"  [!] 本轮异常已跳过（继续 watch）: {type(e).__name__}: {e}")
            time.sleep(interval)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="云桥：插件共享库 ↔ 云端镜像记忆")
    ap.add_argument("--backend", default="local", choices=["local", "webdav", "s3"])
    ap.add_argument("--root", default="", help="local: 云端目录 / webdav: 根 / s3: prefix")
    ap.add_argument("--url", help="webdav 服务 URL")
    ap.add_argument("--user", dest="user", help="webdav 用户")
    ap.add_argument("--pwd", help="webdav 密码")
    ap.add_argument("--bucket", "--endpoint", "--access-key", "--secret-key", dest="bucket")
    ap.add_argument("--plugin-home", default=PLUGIN_ENTRY_HOME, help="cloud entry 指针目录")
    ap.add_argument("--plugin-store", default=PLUGIN_STORE, help="插件共享库路径")
    ap.add_argument("--bridge-local", default=BRIDGE_LOCAL, help="桥镜像本地目录（多实例需隔离）")
    ap.add_argument("--once", action="store_true")
    ap.add_argument("--interval", type=float, default=5.0)
    a = ap.parse_args()

    if a.plugin_home:
        PLUGIN_ENTRY_HOME = a.plugin_home
    if a.plugin_store:
        PLUGIN_STORE = a.plugin_store
    if a.backend == "local":
        bk = make_backend("local", root=a.root or os.path.join(BASE, "cloud-root"))
    elif a.backend == "webdav":
        bk = make_backend("webdav", url=a.url, user=a.user, pwd=a.pwd, root=a.root)
    else:
        bk = make_backend("s3", bucket=a.bucket, endpoint=a.endpoint,
                          access_key=a.access_key or os.environ.get("S3_ACCESS_KEY", ""),
                          secret_key=a.secret_key or os.environ.get("S3_SECRET_KEY", ""), prefix=a.root)
    bridge = Bridge(bk)
    if a.once:
        bridge.run_once()
    else:
        bridge.watch(a.interval)
