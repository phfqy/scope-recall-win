#!/usr/bin/env python3
"""cloudsync_run.py — 云同步「一轮」的统一入口（应用托管子进程时调用它）。

一轮做三件事，顺序固定：
  ① 本地→云：从**真实库只读**增量提取 claims/events，逐条上传变化的记录；
  ② 云→本地：拉取本端尚未见过的记录，确定性去重后经官方 propose_memory 写入共享库；
  ③ 落状态：把本轮结果写成 status.json，供控制台展示（不写任何敏感值）。

安全边界：
  - 真实库一律 mode=ro 打开，本脚本**从不写真实库**；写回只走官方 MCP propose_memory；
  - 去重直接精确查询 source_events.content（不用 recall：propose 只落候选表，
    未配 worker 时不在召回域，用 recall 查重会永远 no_match 导致重复写入）；
  - 状态文件不含口令、不含记忆正文。
"""
import argparse
import json
import os
import sqlite3
import sys
import time
import uuid

BASE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, BASE)

from incremental_sync import IncrementalSyncer, StoreReader, canonical_hash  # noqa: E402
from sync_engine import make_backend  # noqa: E402

# 桥进程级固定线程（模拟一个持续会话的 cloud agent），host=codex 才有 threadId 绑定
THREAD_ID = str(uuid.uuid4())


def _flatten_error(exc) -> str:
    """把嵌套异常压成一句可读原因。

    为什么需要：mcp 客户端内部用 anyio TaskGroup，子进程启动失败时抛的是
    ExceptionGroup("unhandled errors in a TaskGroup (1 sub-exception)")，真正的原因
    （如 ModuleNotFoundError: scope_recall）被包在里面。直接 str(exc) 会让用户看到
    一句没有任何信息量的英文，无从排查。这里递归展开 ExceptionGroup 并把最内层
    异常类型 + 消息带出来。
    """
    seen = []
    stack = [exc]
    while stack:
        cur = stack.pop(0)
        inner = getattr(cur, "exceptions", None)
        if inner:
            stack = list(inner) + stack
            continue
        text = str(cur).strip()
        if text:
            seen.append("%s: %s" % (type(cur).__name__, text))
        else:
            seen.append(type(cur).__name__)
    # 去掉与最外层重复的包裹信息，保留最具体的一条
    return " | ".join(seen[-3:]) if seen else type(exc).__name__


class PluginWriter:
    """以 cloud entry 身份把一条记忆写进共享库（官方 propose_memory）。"""

    def __init__(self, plugin_home, host="codex", python=None):
        self.plugin_home = plugin_home
        self.host = host
        self.python = python or sys.executable
        self._checked = False

    def precheck(self):
        """启动插件 MCP 之前先确认这个解释器真的装了 scope_recall。

        为什么需要：若解释器不对，stdio 子进程会立刻退出，客户端只报
        "MCPError: Connection closed" —— 完全看不出是解释器选错了。
        这里提前做一次显式导入检查，把真正原因（缺 scope_recall + 该用哪个解释器）
        直接写进错误信息。
        """
        if self._checked:
            return
        import subprocess
        probe = subprocess.run(
            [self.python, "-c", "import scope_recall; print(scope_recall.__file__)"],
            capture_output=True, text=True, encoding="utf-8",
        )
        if probe.returncode != 0:
            tail = (probe.stderr or probe.stdout or "").strip().splitlines()
            detail = tail[-1] if tail else "未知错误"
            raise RuntimeError(
                "解释器 %s 无法导入 scope_recall（%s）。"
                "写入共享库必须使用装了记忆插件的解释器（shared-venv），"
                "请检查是否误用了只装了同步依赖的专用 venv。" % (self.python, detail)
            )
        self._checked = True

    def _call(self, tool, args, timeout=60):
        import asyncio
        from mcp import ClientSession, StdioServerParameters
        from mcp.client.stdio import stdio_client

        self.precheck()

        params = StdioServerParameters(
            command=self.python,
            args=["-X", "utf8", "-m", "scope_recall.adapters.codex.mcp_entry",
                  "--home", self.plugin_home, "--host", self.host],
        )

        async def _run():
            async with stdio_client(params) as (r, w):
                async with ClientSession(r, w) as s:
                    await s.initialize()
                    res = await s.call_tool(tool, args, meta={"threadId": THREAD_ID})
                    return res.content[0].text if res.content else str(res)

        return asyncio.run(_run())

    def propose(self, content, reason=None):
        args = {"protocol_version": "1.1", "content": content}
        if reason:
            args["reason"] = reason
        return self._call("propose_memory", args)


def content_for_record(rec) -> str:
    """把一条云端记录转成写进共享库的正文。"""
    if rec.get("record_type") == "event":
        return rec.get("content") or ""
    subject = (rec.get("subject") or "").strip()
    predicate = (rec.get("predicate") or "").strip()
    value = (rec.get("value_text") or "").strip()
    if subject:
        return " ".join(p for p in (subject, predicate, value) if p)
    return value


class AlreadyInStore:
    """确定性去重：直接精确查询共享库 source_events.content。"""

    def __init__(self, store_db):
        self.store_db = store_db

    def has(self, content) -> bool:
        if not content or not os.path.exists(self.store_db):
            return False
        con = sqlite3.connect("file:%s?mode=ro" % self.store_db, uri=True)
        try:
            return con.execute(
                "SELECT 1 FROM source_events WHERE content = ? LIMIT 1", (content,)
            ).fetchone() is not None
        finally:
            con.close()


def make_backend_from_args(a):
    """构造存储后端。

    口令优先取环境变量 SR_CLOUD_PWD / S3_*（控制台从 Windows 凭据管理器读出后注入），
    这样口令**不出现在命令行参数**里（命令行对同机其他用户可见）。
    """
    pwd = a.pwd or os.environ.get("SR_CLOUD_PWD")
    if a.backend == "local":
        return make_backend("local", root=a.root)
    if a.backend == "webdav":
        return make_backend("webdav", url=a.url, user=a.user, pwd=pwd, root=a.root)
    return make_backend("s3", bucket=a.bucket, endpoint=a.endpoint,
                        access_key=a.access_key or os.environ.get("S3_ACCESS_KEY", ""),
                        secret_key=a.secret_key or os.environ.get("S3_SECRET_KEY", ""),
                        prefix=a.root)


def write_status(path, payload):
    if not path:
        return
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    payload = dict(payload)
    payload["written_at"] = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=1)
    os.replace(tmp, path)


def run_round(a, syncer, reader, writer, dedup, status_path):
    started = time.time()
    result = {
        "round_started": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "pushed": 0, "pulled": 0, "written_to_store": 0,
        "unchanged": 0, "conflicts": 0, "skipped_duplicates": 0,
        "errors": [],
    }

    try:
        stats = reader.stats()
        result["stats"] = {
            "schema_version": stats["schema_version"],
            "claims_active": stats["claims_active"],
            "source_events": stats["source_events"],
        }
    except Exception as e:
        result["errors"].append("stats: %s: %s" % (type(e).__name__, e))

    # ① 本地 → 云（增量）
    try:
        records = reader.extract_all()
        if not a.include_events:
            records = [r for r in records if r["record_type"] == "claim"]
        syncer.push_records(records)
        result["pushed"] = len(syncer.report["push"])
        result["unchanged"] = syncer.report["none"]
        result["conflicts"] = len(syncer.report["conflicts"])
        if syncer.report["conflicts"]:
            result["conflict_refs"] = [c["ref"] for c in syncer.report["conflicts"][:20]]
    except Exception as e:
        result["errors"].append("push: %s: %s" % (type(e).__name__, e))

    # ② 云 → 本地共享库
    try:
        pulled = syncer.pull_records()
        result["pulled"] = len(pulled)
        for rec in pulled:
            content = content_for_record(rec)
            if not content:
                continue
            if dedup.has(content):
                result["skipped_duplicates"] += 1
                continue
            try:
                writer.propose(content, reason="cloud-sync")
                result["written_to_store"] += 1
                time.sleep(0.2)   # 避免过密调用触发插件侧限流
            except Exception as e:
                result["errors"].append(
                    "写入共享库失败 ref=%s：%s" % (rec.get("ref"), _flatten_error(e)))
    except Exception as e:
        result["errors"].append("pull: %s: %s" % (type(e).__name__, e))

    result["duration_seconds"] = round(time.time() - started, 2)
    result["status"] = "ok" if not result["errors"] else "partial"
    write_status(status_path, result)
    return result


def main(argv=None):
    ap = argparse.ArgumentParser(description="云同步一轮（真实库只读 + 增量上云 + 云端写回共享库）")
    ap.add_argument("--db", required=True, help="真实共享库 memory.sqlite3（只读）")
    ap.add_argument("--plugin-home", required=True, help="cloud entry 的 home 目录")
    ap.add_argument("--python", help="跑插件 MCP 的解释器（默认当前解释器）")
    ap.add_argument("--backend", default="local", choices=["local", "webdav", "s3"])
    ap.add_argument("--root", default="")
    ap.add_argument("--url")
    ap.add_argument("--user")
    ap.add_argument("--pwd")
    ap.add_argument("--bucket")
    ap.add_argument("--endpoint")
    ap.add_argument("--access-key")
    ap.add_argument("--secret-key")
    ap.add_argument("--state")
    ap.add_argument("--status", help="状态 JSON 输出路径")
    ap.add_argument("--include-events", action="store_true", default=True)
    ap.add_argument("--no-events", dest="include_events", action="store_false")
    ap.add_argument("--once", action="store_true", help="只跑一轮")
    ap.add_argument("--watch", action="store_true", help="持续轮询")
    ap.add_argument("--interval", type=float, default=60.0)
    a = ap.parse_args(argv)

    if a.interval < 60 and a.watch:
        print("[警告] interval < 60s：坚果云免费版会触发 503 限流，建议 ≥60s", file=sys.stderr)

    reader = StoreReader(a.db)
    backend = make_backend_from_args(a)
    state_file = a.state or os.path.join(os.path.dirname(os.path.abspath(a.db)), ".incremental-state.json")
    syncer = IncrementalSyncer(backend, state_file)
    writer = PluginWriter(a.plugin_home, python=a.python)
    dedup = AlreadyInStore(a.db)

    if a.watch:
        print("[cloudsync] 轮询=%ss，Ctrl-C 停止" % a.interval)
        while True:
            try:
                r = run_round(a, syncer, reader, writer, dedup, a.status)
                print(json.dumps({k: r[k] for k in
                                  ("status", "pushed", "pulled", "written_to_store", "duration_seconds")},
                                 ensure_ascii=False))
            except Exception as e:
                print("[!] 本轮异常已跳过：%s: %s" % (type(e).__name__, e), file=sys.stderr)
            time.sleep(a.interval)
        return 0

    r = run_round(a, syncer, reader, writer, dedup, a.status)
    print(json.dumps(r, ensure_ascii=False, indent=1))
    return 0 if r["status"] == "ok" else 1


if __name__ == "__main__":
    raise SystemExit(main())
