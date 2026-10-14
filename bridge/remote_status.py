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

账本缺失回退（2026-10 修复）：state.json 缺失/损坏时，**枚举 records/ 扫描重建**
最小账本（与 remote/sr_cloud_remote.py 的 load_ledger 同款策略），不再把
「协议不一致」伪装成「云端不可达」——静默的数据不可见比报错更危险。

凭据经环境变量（与 cloudsync_run 同名）：SR_CLOUD_PWD / S3_ACCESS_KEY / S3_SECRET_KEY。
"""
import argparse
import json
import os
import sys
import time

# Windows 上 python 的 stdout 默认按 ANSI 代码页（简体中文环境为 GBK）编码，
# 而控制台侧（ProcessRunner）按 UTF-8 解码 → 中文全变乱码。显式扭正。
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sync_engine import make_backend  # noqa: E402

SAMPLE_LIMIT = 60
RECORD_PREFIX = "records/"
SCAN_THROTTLE = 0.25  # 回退扫描逐条读取的节流（秒）：200 条对象逐条 stat 易被判定为滥用


def get_json(backend, key):
    raw = backend.get_bytes(key)
    if not raw:
        return None
    try:
        return json.loads(raw.decode("utf-8"))
    except Exception:
        return None


def list_record_keys(backend):
    """枚举 records/ 下的记录对象 key（单次列表调用，失败返回空列表）。"""
    try:
        return [k for k, _m in (backend.list(RECORD_PREFIX) or []) if k.endswith(".json")]
    except Exception:
        return []


def scan_record_objects(backend, sample):
    """state.json 缺失时枚举 records/ 重建最小账本（只读）。

    返回 (ledger, total, read_n)：
      ledger 只含读过正文的前 read_n 条（ref / record_type / updated_ts / key /
              origin_agent 均取自记录本体，足够支撑 writers 聚合）；
      total 是 records/ 下对象总数（列表枚举即得，不必读正文）。
    """
    keys = list_record_keys(backend)
    ledger = {}
    read_n = 0
    for key in keys[: max(1, sample)]:
        body = get_json(backend, key)
        if body is None:
            continue
        ref = body.get("ref")
        if not ref:
            continue
        read_n += 1
        ledger[ref] = {
            "record_type": body.get("record_type", "claim"),
            "updated_ts": _parse_ts(body.get("updated_at")),
            "key": key,
            "origin_agent": (body.get("origin_agent") or "").strip(),
        }
        if read_n < len(keys):
            time.sleep(SCAN_THROTTLE)  # 节流，降低被服务端判定为滥用的风险
    return ledger, len(keys), read_n


def _parse_ts(value):
    """updated_at（ISO8601 字符串或 epoch 秒）→ 可比较的浮点秒；失败返回 0。"""
    if isinstance(value, (int, float)):
        return float(value)
    s = str(value or "").strip()
    if not s:
        return 0.0
    try:
        return float(s)
    except ValueError:
        pass
    try:
        return time.mktime(time.strptime(s[:19], "%Y-%m-%dT%H:%M:%S"))
    except Exception:
        return 0.0


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

    state = get_json(backend, "state.json")
    fallback = False
    total_objects = None
    if state is None:
        # 账本缺失/损坏：枚举 records/ 重建最小账本（缺陷2 同款 fallback）。
        ledger, total_objects, read_n = scan_record_objects(backend, args.sample)
        if total_objects == 0:
            print(json.dumps({"reachable": False,
                              "error": "读取 state.json 失败，扫描 records/ 也没有收获"
                                       "（后端不可达，或云端确实是空的）"},
                             ensure_ascii=False))
            return 0
        state = {"records": ledger}
        fallback = True

    records = state.get("records") or {}
    claims = sum(1 for m in records.values() if m.get("record_type") == "claim")
    events = sum(1 for m in records.values() if m.get("record_type") == "event")
    newest = max((m.get("updated_ts", 0) for m in records.values()), default=0)

    # 取最近 N 条的 key（updated_ts 降序），读正文做 origin_agent 聚合
    ordered = sorted(records.items(), key=lambda kv: kv[1].get("updated_ts", 0), reverse=True)
    sampled = ordered[: max(1, args.sample)] if len(ordered) > args.sample else ordered
    writers = {}
    for ref, meta in sampled:
        # 回退扫描时 origin_agent 已随正文取到，不必再读一次
        agent = (meta.get("origin_agent") or "").strip()
        if not agent:
            body = get_json(backend, meta.get("key") or "")
            if body is None:
                continue
            agent = (body.get("origin_agent") or "").strip()
        if not agent:
            agent = "home（本机主桥）"
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
        "fallback": fallback,                       # True = 账本缺失，按 records/ 扫描重建
        "total_records": total_objects if total_objects is not None else len(records),
        "claims": claims,
        "events": events,
        "sampled": len(sampled) < (total_objects if total_objects is not None else len(ordered)),
        "sample_size": len(sampled),
        "last_update_ts": newest,
        "last_update_iso": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(newest)) if newest else None,
        "writers": sorted(writers.values(), key=lambda w: w["last_ts"], reverse=True),
    }
    print(json.dumps(out, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
