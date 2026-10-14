#!/usr/bin/env python3
"""memory_mcp.py — 远端 agent 的记忆 MCP Server（与 sr_cloud_remote.py 同源）。

为什么不是 Notion 交付包那一份：
  交付包的 memory_mcp.py 走**整文件 LWW**（云端 memory.sqlite3 + memory.jsonl），
  与本机增量桥的云端账本（state.json + records/<sha1>.json）**不是同一批对象**。
  后果很实际：远端经 MCP 写的东西，控制台的「远端 agent 链接状态」看不见
  （它按 origin_agent 扫账本），home 桥也拉不到（它只读账本）。
  本文件改为**直接读写同一份云端账本**，因此：

      MCP 写  ┐
      CLI 写  ┴─► 同一个 state.json + records/ ─► home 桥 propose_memory ─► 共享库
                                              └─► remote_status.py（控制台链接状态）

  即 MCP 与 CLI/skill 是**同一能力的两个入口**，任选其一，效果等价。

工具（语义对齐 scope-recall 的 Codex 记忆接口）：
  status / recall / inspect / remember / revise / forget / sync

实现要点：工具内部直接调用 sr_cloud_remote 的命令函数并捕获其 JSON 输出，
不复制任何业务逻辑 —— 保证 MCP 与 CLI 行为逐字一致，不会出现两套漂移的实现。

依赖：mcp>=2.0（仅本文件需要；CLI 零依赖）。
  python -m pip install "mcp>=2.0"
  若解释器没有 mcp，本文件会明确报错并提示改用 CLI（CLI 不需要任何第三方包）。

用法（stdio，Codex / Claude / Hermes 标准接入）：
  python memory_mcp.py --db mirror.sqlite3 --backend local --root <云端目录>
  python memory_mcp.py --db mirror.sqlite3 --backend webdav \
        --url https://dav.jianguoyun.com/dav/ --user <邮箱> --pwd <应用密码>
"""
import argparse
import contextlib
import io
import json
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sr_cloud_remote as cli  # noqa: E402


def _base_args(a):
    """把 MCP 启动参数摊平成 CLI 命令需要的 Namespace 底座。"""
    return dict(
        db=a.db, backend=a.backend, root=a.root, url=a.url, user=a.user, pwd=a.pwd,
        bucket=a.bucket, endpoint=a.endpoint, access_key=a.access_key, secret_key=a.secret_key,
    )


def run_cli(fn, base, **kw):
    """调用一个 CLI 命令函数并捕获它的 stdout（命令函数都是 print JSON 后返回码）。"""
    ns = argparse.Namespace(**{**base, **kw})
    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf):
            rc = fn(ns)
    except Exception as e:                      # 命令内部异常也要变成 MCP 可见的 JSON
        return {"ok": False, "error": f"{type(e).__name__}: {e}"}
    except SystemExit as e:                     # CLI 的 fail-fast（写前校验/认证失败/限流）
        return {"ok": False, "error": str(e) or "SystemExit"}  # 否则会掀翻 MCP server 进程
    out = buf.getvalue().strip()
    if out[:1] in ("{", "["):          # 数组（如 recall 的 --json）也要结构化，不能退化成文本
        try:
            parsed = json.loads(out)
        except Exception:
            parsed = {"raw": out}
        if isinstance(parsed, dict):
            parsed.setdefault("ok", rc == 0)
            return parsed
        return {"ok": rc == 0, "items": parsed}
    return {"ok": rc == 0, "text": out}


def build_server(a):
    try:
        from mcp.server import MCPServer
    except Exception as e:                      # 没有 mcp 包时给出可执行的替代方案
        raise SystemExit(
            "本 MCP Server 需要 mcp>=2.0：python -m pip install \"mcp>=2.0\"\n"
            "或者直接改用零依赖的 CLI（同一份云端账本，效果等价）：\n"
            "  python sr_cloud_remote.py sync|recall|propose|revise|forget ...\n"
            f"（原始错误：{e}）")

    base = _base_args(a)
    mcp = MCPServer("scope-recall-cloud-memory")

    @mcp.tool()
    def status() -> str:
        """记忆库状态：镜像条数 + 云端账本概览（记录/claims/events）。"""
        return json.dumps(run_cli(cli.cmd_status, base, cloud=True), ensure_ascii=False, indent=1)

    @mcp.tool()
    def recall(query: str, limit: int = 20, kind: str = "") -> str:
        """检索记忆（镜像内 LIKE 匹配 subject/predicate/value_text/事件正文）。
        query=关键词, limit=条数, kind=可选过滤(fact/preference/constraint/decision/…)"""
        return json.dumps(run_cli(cli.cmd_recall, base, query=query, limit=limit,
                                  kind=kind or None, json=True), ensure_ascii=False, indent=1)

    @mcp.tool()
    def inspect(subject: str, limit: int = 50) -> str:
        """按实体（subject）列出该实体的全部记忆。"""
        con = cli.open_mirror(a.db)
        try:
            rows = con.execute(
                "SELECT ref,subject,predicate,kind,value_text,state,updated_at FROM claims"
                " WHERE subject LIKE ? ORDER BY updated_at DESC LIMIT ?",
                (f"%{subject}%", limit)).fetchall()
            return json.dumps({"ok": True, "items": [dict(r) for r in rows]},
                              ensure_ascii=False, indent=1)
        finally:
            con.close()

    @mcp.tool()
    def remember(subject: str, value: str, predicate: str = "", kind: str = "fact",
                 note: str = "", force: bool = False) -> str:
        """写入一条记忆（上云为 proposed，home 桥转官方 propose_memory 进共享库）。
        subject=实体, value=内容, predicate=关系, kind=fact|preference|constraint|decision,
        note=可选备注, force=允许与已提议内容重复"""
        res = run_cli(cli.cmd_propose, base, subject=subject, value_text=value,
                      predicate=predicate, kind=kind, valid_from=None,
                      note=note or None, force=force)
        return json.dumps(res, ensure_ascii=False, indent=1)

    @mcp.tool()
    def revise(ref: str, value: str = "", predicate: str = "", kind: str = "",
               subject: str = "", note: str = "", force: bool = False) -> str:
        """按 ref 修订一条已上云的记忆。默认只允许改本 agent 自己提议的；改他人记录需 force=true。"""
        res = run_cli(cli.cmd_revise, base, ref=ref,
                      value_text=value or None, subject=subject or None,
                      predicate=predicate or None, kind=kind or None,
                      note=note or None, force=force)
        return json.dumps(res, ensure_ascii=False, indent=1)

    @mcp.tool()
    def forget(ref: str, note: str = "", force: bool = False) -> str:
        """按 ref 废弃一条记忆（软删除：state=superseded，home 桥不再转写进共享库）。"""
        res = run_cli(cli.cmd_forget, base, ref=ref, note=note or None, force=force)
        return json.dumps(res, ensure_ascii=False, indent=1)

    @mcp.tool()
    def sync() -> str:
        """手动拉取云端最新记录到本地镜像（增量，只传变化；带 SHA256 完整性校验）。"""
        return json.dumps(run_cli(cli.cmd_sync, base), ensure_ascii=False, indent=1)

    return mcp


def main(argv=None):
    ap = argparse.ArgumentParser(description="远端 agent 记忆 MCP Server（与 sr_cloud_remote.py 同源云端账本）")
    ap.add_argument("--db", default=os.environ.get("SR_CLOUD_DB", cli.DEFAULT_DB))
    ap.add_argument("--backend", default=os.environ.get("SR_CLOUD_BACKEND", "local"),
                    choices=["local", "webdav", "s3"])
    ap.add_argument("--root", default=os.environ.get("SR_CLOUD_ROOT", ""))
    ap.add_argument("--url", default=os.environ.get("SR_CLOUD_URL"))
    ap.add_argument("--user", default=os.environ.get("SR_CLOUD_USER"))
    ap.add_argument("--pwd", default=os.environ.get("SR_CLOUD_PWD"))
    ap.add_argument("--bucket", default=os.environ.get("SR_CLOUD_BUCKET"))
    ap.add_argument("--endpoint", default=os.environ.get("SR_CLOUD_ENDPOINT"))
    ap.add_argument("--access-key", default=os.environ.get("SR_CLOUD_ACCESS_KEY"))
    ap.add_argument("--secret-key", default=os.environ.get("SR_CLOUD_SECRET_KEY"))
    ap.add_argument("--selftest", action="store_true",
                    help="不起 MCP，直接跑一遍 7 个工具的自检（用于没有 mcp 包的环境验证逻辑）")
    a = ap.parse_args(argv)

    if a.selftest:
        return selftest(a)

    mcp = build_server(a)
    mcp.run()  # stdio transport
    return 0


def selftest(a):
    """不依赖 mcp 包，直接验证「工具会调到的那批 CLI 逻辑」是否可用。"""
    base = _base_args(a)
    report = {}
    report["sync"] = run_cli(cli.cmd_sync, base)
    report["remember"] = run_cli(cli.cmd_propose, base, subject="MCP自检", value_text="selftest",
                                 predicate="ping", kind="fact", valid_from=None,
                                 note="mcp selftest", force=True)
    ref = report["remember"].get("ref")
    if ref:
        report["revise"] = run_cli(cli.cmd_revise, base, ref=ref, value_text="selftest-2",
                                   subject=None, predicate=None, kind=None,
                                   note="mcp selftest", force=False)
        report["forget"] = run_cli(cli.cmd_forget, base, ref=ref, note="mcp selftest", force=False)
    report["recall"] = run_cli(cli.cmd_recall, base, query="selftest", limit=5, kind=None, json=True)
    report["status"] = run_cli(cli.cmd_status, base, cloud=True)
    con = cli.open_mirror(a.db)
    try:
        report["inspect"] = con.execute(
            "SELECT COUNT(*) c FROM claims WHERE subject LIKE ?", ("%MCP自检%",)).fetchone()["c"]
    finally:
        con.close()
    print(json.dumps(report, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
