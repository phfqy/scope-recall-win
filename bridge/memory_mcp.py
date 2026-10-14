#!/usr/bin/env python3
"""memory_mcp.py — Agent 端记忆 MCP Server。

模拟 scope-recall 的 Codex 记忆接口（工具名/语义对齐），但数据源是"云端镜像记忆文件"：
远端 Agent 通过 MCP（stdio）读写云端记忆；每次写入后自动同步上传，也可手动 sync 拉取云端新记忆。

工具：
  status      — 库状态（条数/最近同步）
  recall      — 检索记忆（关键词/模糊）
  inspect     — 按实体列出记忆
  remember    — 写入一条新记忆（agent 端）
  revise      — 修改一条记忆
  forget      — 废弃一条记忆
  sync        — 手动双向同步（拉云端 + 推本地）

用法：
  python3 memory_mcp.py --db ../test/mirror.sqlite3 --backend local --root ../cloud-root
  python3 memory_mcp.py --db ../test/mirror.sqlite3 --backend webdav --url http://127.0.0.1:8800 --user admin --pwd admin123
"""
import os, sys, json, sqlite3, time, argparse, threading

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sync_engine import make_backend, Syncer, hashfile

DB_SCHEMA = """
CREATE TABLE IF NOT EXISTS claims(
  ref TEXT PRIMARY KEY,
  subject TEXT, predicate TEXT, value_text TEXT, kind TEXT,
  claim_state TEXT DEFAULT 'active',
  created_at TEXT, updated_at TEXT,
  source_agent TEXT DEFAULT 'remote-agent');
CREATE TABLE IF NOT EXISTS sources(
  ref TEXT PRIMARY KEY, content TEXT, platform TEXT, chat_type TEXT);
"""

def now_iso():
    return time.strftime("%Y-%m-%dT%H:%M:%S.000000+00:00", time.gmtime())

def ensure_db(path):
    new = not os.path.exists(path)
    con = sqlite3.connect(path)
    con.executescript(DB_SCHEMA)
    con.commit()
    con.close()
    return new

class MemoryStore:
    """镜像记忆库读写（简化 scope-recall 语义）"""
    def __init__(self, db_path):
        ensure_db(db_path)
        self.db = db_path

    def _conn(self):
        con = sqlite3.connect(self.db)
        con.row_factory = sqlite3.Row
        return con

    def count(self):
        con = self._conn()
        n = con.execute("SELECT COUNT(*) c FROM claims WHERE claim_state='active'").fetchone()["c"]
        con.close()
        return n

    def recall(self, query, limit=10):
        con = self._conn()
        q = f"%{query}%"
        rows = con.execute(
            "SELECT ref, subject, predicate, value_text, kind, updated_at FROM claims "
            "WHERE claim_state='active' AND (subject LIKE ? OR value_text LIKE ? OR predicate LIKE ?) "
            "ORDER BY updated_at DESC LIMIT ?", (q, q, q, limit)).fetchall()
        con.close()
        return [dict(r) for r in rows]

    def inspect(self, subject, limit=50):
        con = self._conn()
        rows = con.execute(
            "SELECT ref, subject, predicate, value_text, kind, updated_at FROM claims "
            "WHERE subject=? AND claim_state='active' ORDER BY updated_at DESC LIMIT ?",
            (subject, limit)).fetchall()
        con.close()
        return [dict(r) for r in rows]

    def remember(self, subject, predicate, value, kind="fact"):
        ref = f"claim-{int(time.time()*1000)}-{os.getpid()}"
        ts = now_iso()
        con = self._conn()
        con.execute("INSERT INTO claims(ref,subject,predicate,value_text,kind,claim_state,created_at,updated_at,source_agent) "
                    "VALUES(?,?,?,?,?,?,?,?,?)",
                    (ref, subject, predicate, value, kind, "active", ts, ts, "remote-agent"))
        con.commit(); con.close()
        return {"ref": ref, "updated_at": ts}

    def revise(self, ref, value_text):
        ts = now_iso()
        con = self._conn()
        cur = con.execute("UPDATE claims SET value_text=?, updated_at=? WHERE ref=? AND claim_state='active'",
                          (value_text, ts, ref))
        con.commit(); con.close()
        if cur.rowcount == 0:
            return None
        return {"ref": ref, "updated_at": ts, "value_text": value_text}

    def forget(self, ref):
        ts = now_iso()
        con = self._conn()
        cur = con.execute("UPDATE claims SET claim_state='superseded', updated_at=? WHERE ref=? AND claim_state='active'",
                          (ts, ref))
        con.commit(); con.close()
        return cur.rowcount > 0

    def dump_jsonl(self, path):
        """导出镜像库为 jsonl（记忆文件可读格式）"""
        con = self._conn()
        rows = con.execute("SELECT ref,subject,predicate,value_text,kind,claim_state,created_at,updated_at FROM claims").fetchall()
        with open(path, "w", encoding="utf-8") as f:
            for r in rows:
                f.write(json.dumps(dict(r), ensure_ascii=False) + "\n")
        con.close()
        return len(rows)

    def load_jsonl(self, path):
        """从 jsonl 合并（upsert by ref）"""
        con = self._conn()
        n = 0
        for line in open(path, encoding="utf-8"):
            row = json.loads(line)
            n += con.execute(
                "INSERT INTO claims(ref,subject,predicate,value_text,kind,claim_state,created_at,updated_at) "
                "VALUES(?,?,?,?,?,?,?,?) "
                "ON CONFLICT(ref) DO UPDATE SET subject=excluded.subject,predicate=excluded.predicate,"
                "value_text=excluded.value_text,kind=excluded.kind,claim_state=excluded.claim_state,updated_at=excluded.updated_at",
                (row["ref"], row["subject"], row["predicate"], row["value_text"], row["kind"],
                 row.get("claim_state", "active"), row.get("created_at", now_iso()), row.get("updated_at", now_iso()))).rowcount
        con.commit(); con.close()
        return n


def build_server(db_path, backend):
    store = MemoryStore(db_path)
    syncer = Syncer(backend, local_dir=os.path.dirname(os.path.abspath(db_path)))
    remote_key = "memory.sqlite3"

    # 首次启动：云端有库则拉取；否则保持本地库（后续 push 上云）
    if backend.meta(remote_key):
        syncer.sync_key(db_path, remote_key)

    def sync_now():
        acts = []
        # 1) 拉取云端合并内容（jsonl，含插件侧经云桥上云的记忆）→ 合入本镜像库
        jl = db_path + ".jsonl"
        act0, msg0 = syncer.sync_key(jl, "memory.jsonl")
        if act0 == "pull":
            n = store.load_jsonl(jl)
            acts.append((act0, f"云端→本地 合并 {n} 条"))
        # 2) 推送本库（sqlite + jsonl 双文件，与桥/远端其他实例互通）
        act, msg = syncer.sync_key(db_path, remote_key)
        acts.append((act, msg))
        store.dump_jsonl(jl)
        act2, msg2 = syncer.sync_key(jl, "memory.jsonl")
        acts.append((act2, msg2))
        return acts

    from mcp.server import MCPServer
    mcp = MCPServer("scope-recall-memory")

    @mcp.tool()
    def status() -> str:
        """记忆库状态：活跃记忆条数、云端同步状态"""
        acts = sync_now()
        return json.dumps({
            "active_claims": store.count(),
            "db": db_path,
            "backend": type(backend).__name__,
            "last_sync": acts,
        }, ensure_ascii=False, indent=1)

    @mcp.tool()
    def recall(query: str, limit: int = 10) -> str:
        """检索记忆（按关键词匹配主题/内容/谓词）。query=关键词, limit=返回条数"""
        rows = store.recall(query, limit)
        return json.dumps(rows, ensure_ascii=False, indent=1)

    @mcp.tool()
    def inspect(subject: str, limit: int = 50) -> str:
        """按实体（subject）列出该实体的所有记忆"""
        rows = store.inspect(subject, limit)
        return json.dumps(rows, ensure_ascii=False, indent=1)

    @mcp.tool()
    def remember(subject: str, predicate: str, value: str, kind: str = "fact") -> str:
        """写入一条新记忆并同步云端。subject=实体, predicate=关系, value=内容, kind=fact|preference|constraint|decision"""
        r = store.remember(subject, predicate, value, kind)
        acts = sync_now()
        return json.dumps({"written": r, "sync": acts}, ensure_ascii=False, indent=1)

    @mcp.tool()
    def revise(ref: str, value_text: str) -> str:
        """按 ref 修改一条记忆的内容并同步云端"""
        r = store.revise(ref, value_text)
        if r is None:
            return json.dumps({"error": f"ref 不存在或已废弃: {ref}"}, ensure_ascii=False)
        acts = sync_now()
        return json.dumps({"revised": r, "sync": acts}, ensure_ascii=False, indent=1)

    @mcp.tool()
    def forget(ref: str) -> str:
        """按 ref 废弃一条记忆（软删除）并同步云端"""
        ok = store.forget(ref)
        acts = sync_now()
        return json.dumps({"forgot": ok, "sync": acts}, ensure_ascii=False, indent=1)

    @mcp.tool()
    def sync() -> str:
        """手动双向同步：拉取云端更新 + 推送本地写入"""
        acts = sync_now()
        return json.dumps({"synced": acts}, ensure_ascii=False, indent=1)

    return mcp


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="Agent 端记忆 MCP Server（对齐 scope-recall Codex 接口）")
    ap.add_argument("--db", required=True, help="本地镜像记忆库路径 (mirror.sqlite3)")
    ap.add_argument("--backend", default="local", choices=["local", "webdav", "s3"])
    ap.add_argument("--root", default="", help="local: 云端目录 / webdav: 根路径 / s3: prefix")
    ap.add_argument("--url", help="webdav: 服务 URL")
    ap.add_argument("--user", help="webdav: 用户名")
    ap.add_argument("--pwd", help="webdav: 密码")
    ap.add_argument("--bucket", "--endpoint", "--access-key", "--secret-key", dest="bucket", help="s3 参数")
    a = ap.parse_args()

    if a.backend == "local":
        bk = make_backend("local", root=a.root or os.path.join(os.path.dirname(os.path.abspath(__file__)), "cloud-root"))
    elif a.backend == "webdav":
        bk = make_backend("webdav", url=a.url, user=a.user, pwd=a.pwd, root=a.root)
    else:
        bk = make_backend("s3", bucket=a.bucket, endpoint=a.endpoint,
                          access_key=a.access_key or os.environ.get("S3_ACCESS_KEY", ""),
                          secret_key=a.secret_key or os.environ.get("S3_SECRET_KEY", ""), prefix=a.root)
    mcp = build_server(os.path.abspath(a.db), bk)
    mcp.run()  # stdio transport（Codex / Hermes 标准接入）
