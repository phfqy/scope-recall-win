#!/usr/bin/env python3
"""remote_status.py — 云端账本「远端 agent 链接状态」探针（只读）。

与 cloudsync_run.py 用同一后端抽象（sync_engine.make_backend）读共享云端账本：
  state.json + records/<sha1(ref)>.json
输出一份 JSON 给控制台「远端 agent 链接状态」卡片：
  总记录数 / claims / events / 最近更新时间，
  以及**最近写入的记录按 origin_agent 聚合**（远端 CLI sr_cloud_remote.py propose
  会打 origin_agent 标记；本机主桥推的记录没有该字段 → 归为 home）。

只读：绝不写 state.json 或任何记录对象。
取样策略：总量 ≤ 200 时全量读取；超过则只取最近 60 条（按 updated_ts 降序），
writers 的计数只覆盖取样窗口（诚实标注 sampled=true）。

凭据经环境变量（与 cloudsync_run 同名）：SR_CLOUD_PWD / S3_ACCESS_KEY / S3_SECRET_KEY。
"""
import argparse
import json
import sys
import time

import os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sync_engine import make_backend  # noqa: E402

SAMPLE_LIMIT = 60


def main(argv=None):
    ap = argparse.ArgumentParser(description="云端账本远端链接状态探针（只读）")
    ap.add_argument("--backend", default="local", choices=["local", "webdav", "s3"])
    ap.add_argument("--root", default="", help="local: 云端目录 / webdav: 根路径 / s3: prefix")
    ap.add_argument("--url")
    ap.add_argument("--user")
    ap.add_argument("--pwd", default=os.environ.get("SR_CLOUD_PWD"))
    ap.add_argument("--bucket")
    ap.add_argument("--endpoint")
    ap.add_argument("--access-key", default=os.environ.get("S3_ACCESS_KEY"))
    ap.add_argument("--secret-key", default=os.environ.get("S3_SECRET_KEY"))
    ap.add_argument("--sample", type=int, default=SAMPLE_LIMIT)
    args = ap.parse_args(argv)

    backend = make_backend(args.backend, root=args.root, url=args.url, user=args.user,
                           pwd=args.pwd, bucket=args.bucket, endpoint=args.endpoint,
                           access_key=args.access_key, secret_key=args.secret_key)

    def get_json(key):
        raw = backend.get_bytes(key)
        if not raw:
            return None
        try:
            return json.loads(raw.decode("utf-8"))
        except Exception:
            return None

    state = get_json("state.json")
    if state is None:
        print(json.dumps({"reachable": False,
                          "error": "读取 state.json 失败（后端不可达或账本不存在）"},
                         ensure_ascii=False))
        return 0

    records = state.get("records") or {}
    claims = sum(1 for m in records.values() if m.get("record_type") == "claim")
    events = sum(1 for m in records.values() if m.get("record_type") == "event")
    newest = max((m.get("updated_ts", 0) for m in records.values()), default=0)

    # 取最近 N 条的 key（updated_ts 降序），读正文做 origin_agent 聚合
    ordered = sorted(records.items(), key=lambda kv: kv[1].get("updated_ts", 0), reverse=True)
    sampled = ordered[: max(1, args.sample)] if len(ordered) > args.sample else ordered
    writers = {}
    for ref, meta in sampled:
        body = get_json(meta.get("key") or "")
        if body is None:
            continue
        agent = (body.get("origin_agent") or "").strip() or "home（本机主桥）"
        w = writers.setdefault(agent, {"agent": agent, "records": 0, "claims": 0,
                                       "events": 0, "last_ts": 0})
        w["records"] += 1
        if meta.get("record_type") == "claim":
            w["claims"] += 1
        elif meta.get("record_type") == "event":
            w["events"] += 1
        ts = meta.get("updated_ts", 0)
        if ts > w["last_ts"]:
            w["last_ts"] = ts

    # C# 侧 RemoteLinkWriter 读 last_iso（人类可读时间），这里统一补上；
    # 与 writes 里存的 last_ts（epoch 秒）同源，避免应用层拿到空时间。
    for w in writers.values():
        ts = w.get("last_ts") or 0
        w["last_iso"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(ts)) if ts else None

    out = {
        "reachable": True,
        "total_records": len(records),
        "claims": claims,
        "events": events,
        "sampled": len(sampled) < len(ordered),
        "sample_size": len(sampled),
        "last_update_ts": newest,
        "last_update_iso": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(newest)) if newest else None,
        "writers": sorted(writers.values(), key=lambda w: w["last_ts"], reverse=True),
    }
    print(json.dumps(out, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
