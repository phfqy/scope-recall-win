#!/usr/bin/env python3
"""incremental_sync.py — 面向**真实 scope-recall 库（schema 1110）**的增量同步引擎。

为什么不能直接用交付包的 sync_engine.py：
  1. 交付包做的是**整文件 LWW**（本地 hash 一变就整包上传）。真实共享库 71MB 且后台
     worker 持续写入 WAL，整文件同步等于每分钟上传 71MB —— 坚果云免费额度几分钟即爆。
  2. 交付包的镜像 schema 与真实库**完全不同**：真实库 claims 没有 value_text /
     claim_state / updated_at，正文在 claim_versions.payload_json 里。
  3. 交付包的 _is_empty_db() 查不存在的 claim_state 列，会把真实库误判为空库
     （已在 bridge/sync_engine.py 修复，但整文件口径本身仍不可用于真实库）。

本引擎的做法：
  - **只读**打开真实库（mode=ro），按 ref 逐条提取；
  - 每条记录一个云端对象 `records/<sha1(ref)>.json`，只传变化的记录 → 流量与变更量成正比；
  - 云端 `state.json` 记录每个 ref 的 hash + updated_ts，是 push/pull 决策依据（很小）；
  - 水位线用真实库的 claim_versions.recorded_from 与 source_events.recorded_at；
  - 冲突不静默「云端胜」，而是记入 conflicts 交给上层裁决。

边界：本引擎**从不写真实库**。云端→本机的写入一律交给 bridge（propose_memory）。
"""
import argparse
import hashlib
import json
import os
import sqlite3
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sync_engine import make_backend  # noqa: E402  复用三后端抽象（local/webdav/s3）

SCHEMA_VERSION = "scope-recall.incremental-sync/1"
RECORD_PREFIX = "records/"
STATE_KEY = "state.json"

#: 真实库中「有效记忆」的判定列（claim_state 只存在于桥的简化镜像，真实库没有）
ACTIVE_CLAIMS_SQL = "SELECT COUNT(*) FROM claims WHERE read_blocked=0 AND suppressed=0"


# ---------------------------------------------------------------- 提取层

def canonical_hash(obj) -> str:
    """记录内容的稳定哈希（键序无关），用于变更检测。"""
    blob = json.dumps(obj, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(blob.encode("utf-8")).hexdigest()


def record_key(ref: str) -> str:
    return RECORD_PREFIX + hashlib.sha1(ref.encode("utf-8")).hexdigest() + ".json"


class StoreReader:
    """真实共享库的只读提取器。任何写操作都会在连接层被 SQLite 拒绝。"""

    def __init__(self, db_path):
        self.db_path = db_path
        self._check()

    def _connect(self):
        con = sqlite3.connect("file:%s?mode=ro" % self.db_path, uri=True)
        con.row_factory = sqlite3.Row
        con.execute("PRAGMA query_only=1")
        # 强制 temp_store=MEMORY。原因（本机实测）：SQLite 排序/临时 B-tree 在默认
        # temp_store=FILE 下要写系统临时目录，而本机 TEMP/TMP 指向的
        # %LOCALAPPDATA%\Temp 对当前进程不可写（PermissionError），导致稍大的
        # ORDER BY 直接报 "unable to open database file"——这个报错完全看不出是临时目录
        # 权限问题。改为内存临时表后同一查询正常，且省掉磁盘往返。
        con.execute("PRAGMA temp_store=2")
        return con

    def _check(self):
        con = self._connect()
        try:
            row = con.execute("SELECT schema_version, memory_epoch FROM instance_meta").fetchone()
            self.schema_version = row["schema_version"]
            self.memory_epoch = row["memory_epoch"]
        finally:
            con.close()

    def is_empty(self) -> bool:
        """真实库是否无有效记忆（用真实列名，绝不误判）。"""
        con = self._connect()
        try:
            return con.execute(ACTIVE_CLAIMS_SQL).fetchone()[0] == 0
        finally:
            con.close()

    def stats(self) -> dict:
        con = self._connect()
        try:
            return {
                "schema_version": self.schema_version,
                "memory_epoch": self.memory_epoch,
                "claims_active": con.execute(ACTIVE_CLAIMS_SQL).fetchone()[0],
                "claims_total": con.execute("SELECT COUNT(*) FROM claims").fetchone()[0],
                "claim_versions": con.execute("SELECT COUNT(*) FROM claim_versions").fetchone()[0],
                "source_events": con.execute("SELECT COUNT(*) FROM source_events").fetchone()[0],
                "entries": [dict(r) for r in con.execute(
                    "SELECT entry_id, COUNT(*) n FROM source_events GROUP BY 1 ORDER BY n DESC")],
            }
        finally:
            con.close()

    # -- 提取：claims（含当前版本正文） --

    def extract_claims(self, since=None) -> list:
        """提取 claims + 当前版本 payload。since 为 recorded_from 水位线（不含）。"""
        con = self._connect()
        try:
            sql = """
                SELECT c.claim_id, c.scope_id, c.project_id, c.branch_id,
                       c.subject, c.predicate, c.kind, c.slot_key,
                       c.current_revision, c.read_blocked, c.suppressed,
                       v.revision, v.state, v.payload_json, v.recorded_from,
                       v.recorded_to, v.basis, v.qualification_reason
                  FROM claims c
                  JOIN claim_versions v
                    ON v.claim_id = c.claim_id AND v.revision = c.current_revision
            """
            params = []
            if since:
                sql += " WHERE v.recorded_from > ?"
                params.append(since)
            sql += " ORDER BY v.recorded_from"
            out = []
            for r in con.execute(sql, params):
                try:
                    payload = json.loads(r["payload_json"])
                except Exception:
                    payload = {}
                out.append({
                    "ref": r["claim_id"],
                    "record_type": "claim",
                    "scope_id": r["scope_id"],
                    "project_id": r["project_id"],
                    "branch_id": r["branch_id"],
                    "subject": r["subject"],
                    "predicate": r["predicate"],
                    "kind": r["kind"],
                    "slot_key": r["slot_key"],
                    "revision": r["revision"],
                    "state": r["state"],
                    "value_text": payload.get("value_text"),
                    "conditions": payload.get("conditions"),
                    "valid_from": payload.get("valid_from"),
                    "valid_to": payload.get("valid_to"),
                    "read_blocked": r["read_blocked"],
                    "suppressed": r["suppressed"],
                    "updated_at": r["recorded_from"],
                })
            return out
        finally:
            con.close()

    # -- 提取：source_events（原始事件，含原文） --

    def extract_events(self, since=None) -> list:
        con = self._connect()
        try:
            sql = """
                SELECT event_id, scope_id, session_id, origin, role, content,
                       entry_id, occurred_at, recorded_at, capture_state,
                       read_blocked, suppressed
                  FROM source_events
            """
            params = []
            if since:
                sql += " WHERE recorded_at > ?"
                params.append(since)
            sql += " ORDER BY recorded_at"
            out = []
            for r in con.execute(sql, params):
                out.append({
                    "ref": r["event_id"],
                    "record_type": "event",
                    "scope_id": r["scope_id"],
                    "session_id": r["session_id"],
                    "origin": r["origin"],
                    "role": r["role"],
                    "content": r["content"],
                    "entry": r["entry_id"],
                    "occurred_at": r["occurred_at"],
                    "capture_state": r["capture_state"],
                    "read_blocked": r["read_blocked"],
                    "suppressed": r["suppressed"],
                    "updated_at": r["recorded_at"],
                })
            return out
        finally:
            con.close()

    def extract_all(self, since_claims=None, since_events=None) -> list:
        return self.extract_claims(since_claims) + self.extract_events(since_events)


# ---------------------------------------------------------------- 同步层

class IncrementalSyncer:
    """逐条记录的增量同步器：云端一个对象一条记录，只传变化的部分。"""

    def __init__(self, backend, state_file, local_dir=None):
        self.backend = backend
        self.state_file = state_file
        self.local_dir = local_dir
        self.state = self._load_state()
        self.report = {"push": [], "pull": [], "none": 0, "conflicts": []}

    def _load_state(self):
        if os.path.exists(self.state_file):
            try:
                with open(self.state_file, encoding="utf-8") as f:
                    return json.load(f)
            except Exception:
                pass
        return {"schema": SCHEMA_VERSION, "records": {}, "watermark": {}}

    def _save_state(self):
        os.makedirs(os.path.dirname(os.path.abspath(self.state_file)), exist_ok=True)
        tmp = self.state_file + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(self.state, f, ensure_ascii=False, indent=1)
        os.replace(tmp, self.state_file)

    def _read_cloud_state(self) -> dict:
        raw = self.backend.get_bytes(STATE_KEY)
        if not raw:
            return {"schema": SCHEMA_VERSION, "records": {}}
        try:
            return json.loads(raw.decode("utf-8"))
        except Exception:
            return {"schema": SCHEMA_VERSION, "records": {}}

    def _write_cloud_state(self, cs: dict):
        self.backend.put_bytes(STATE_KEY, json.dumps(cs, ensure_ascii=False).encode("utf-8"))

    def _put_record(self, rec, cs, now_ts):
        ref = rec["ref"]
        key = record_key(ref)
        blob = json.dumps(rec, ensure_ascii=False, sort_keys=True).encode("utf-8")
        self.backend.put_bytes(key, blob)
        h = canonical_hash(rec)
        cs.setdefault("records", {})[ref] = {
            "hash": h, "updated_ts": now_ts, "key": key,
            "record_type": rec.get("record_type", "claim"),
        }
        self.state["records"][ref] = {"hash": h, "remote_ts": now_ts}
        return h

    def _get_record(self, ref, cs):
        meta = (cs.get("records") or {}).get(ref)
        if not meta:
            return None
        raw = self.backend.get_bytes(meta.get("key") or record_key(ref))
        if not raw:
            return None
        try:
            return json.loads(raw.decode("utf-8"))
        except Exception:
            return None

    def push_records(self, records, dry_run=False):
        """把本地记录推上云。只上传 hash 与云端不同的记录。"""
        cs = self._read_cloud_state()
        now_ts = time.time()
        uploaded = 0
        for rec in records:
            ref = rec["ref"]
            h = canonical_hash(rec)
            cloud = (cs.get("records") or {}).get(ref)
            prev = self.state["records"].get(ref)
            if cloud and cloud.get("hash") == h:
                self.report["none"] += 1
                continue
            # 云端在本端上次同步之后也被改过 → 冲突，交给上层裁决，不静默覆盖
            if cloud and prev and cloud.get("updated_ts", 0) > (prev.get("remote_ts") or 0):
                self.report["conflicts"].append({
                    "ref": ref,
                    "reason": "both_changed",
                    "local_hash": h,
                    "cloud_hash": cloud.get("hash"),
                    "cloud_updated_ts": cloud.get("updated_ts"),
                })
                continue
            if dry_run:
                self.report["push"].append({"ref": ref, "dry_run": True})
                continue
            self._put_record(rec, cs, now_ts)
            self.report["push"].append({"ref": ref, "record_type": rec.get("record_type")})
            uploaded += 1
        if not dry_run:
            self._write_cloud_state(cs)
            self._save_state()
        return uploaded

    def pull_records(self, refs=None, dry_run=False):
        """从云端拉取本端尚未见过、或云端更新的记录。返回拉到的记录列表。"""
        cs = self._read_cloud_state()
        cloud_records = cs.get("records") or {}
        pulled = []
        for ref, meta in cloud_records.items():
            if refs is not None and ref not in refs:
                continue
            prev = self.state["records"].get(ref)
            if prev and prev.get("hash") == meta.get("hash"):
                self.report["none"] += 1
                continue
            if prev and meta.get("updated_ts", 0) <= (prev.get("remote_ts") or 0):
                self.report["none"] += 1
                continue
            if dry_run:
                self.report["pull"].append({"ref": ref, "dry_run": True})
                continue
            rec = self._get_record(ref, cs)
            if rec is None:
                continue
            self.state["records"][ref] = {
                "hash": meta.get("hash"), "remote_ts": meta.get("updated_ts", 0),
            }
            self.report["pull"].append({"ref": ref, "record_type": meta.get("record_type")})
            pulled.append(rec)
        if not dry_run:
            self._save_state()
        return pulled

    def write_inbox(self, records, path):
        """把拉到的记录写成 inbox（供 bridge 转 propose_memory）。"""
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            for rec in records:
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")
        return len(records)


def main(argv=None):
    ap = argparse.ArgumentParser(description="真实库增量同步引擎（只读提取 + 逐条增量上云）")
    ap.add_argument("--db", required=True, help="真实共享库 memory.sqlite3（只读打开）")
    ap.add_argument("--backend", default="local", choices=["local", "webdav", "s3"])
    ap.add_argument("--root", default="", help="local: 云端目录 / webdav: 根 / s3: prefix")
    ap.add_argument("--url")
    ap.add_argument("--user")
    ap.add_argument("--pwd")
    ap.add_argument("--bucket")
    ap.add_argument("--endpoint")
    ap.add_argument("--access-key")
    ap.add_argument("--secret-key")
    ap.add_argument("--state", help="本地状态文件（默认与 db 同目录 .incremental-state.json）")
    ap.add_argument("--inbox", help="拉取到的记录输出为 jsonl（供 bridge 转 propose_memory）")
    ap.add_argument("--stats", action="store_true", help="只打印库统计")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--no-events", action="store_true", help="只同步 claims，不上传原始事件原文")
    a = ap.parse_args(argv)

    reader = StoreReader(a.db)
    if a.stats:
        print(json.dumps(reader.stats(), ensure_ascii=False, indent=1))
        return 0

    if a.backend == "local":
        bk = make_backend("local", root=a.root or os.path.join(os.path.dirname(os.path.abspath(a.db)), "cloud-root"))
    elif a.backend == "webdav":
        bk = make_backend("webdav", url=a.url, user=a.user, pwd=a.pwd, root=a.root)
    else:
        bk = make_backend("s3", bucket=a.bucket, endpoint=a.endpoint,
                          access_key=a.access_key or os.environ.get("S3_ACCESS_KEY", ""),
                          secret_key=a.secret_key or os.environ.get("S3_SECRET_KEY", ""),
                          prefix=a.root)

    state_file = a.state or os.path.join(os.path.dirname(os.path.abspath(a.db)), ".incremental-state.json")
    syncer = IncrementalSyncer(bk, state_file)

    # 空库保护（用真实列名）：本地无有效记忆而云端有数据 → 只拉不推
    cs = syncer._read_cloud_state()
    cloud_has_data = bool((cs.get("records") or {}))
    if reader.is_empty() and cloud_has_data:
        print("[保护] 本地库无有效记忆且云端有数据 → 仅拉取，绝不推送")
        pulled = syncer.pull_records(dry_run=a.dry_run)
        if a.inbox:
            syncer.write_inbox(pulled, a.inbox)
        print(json.dumps(syncer.report, ensure_ascii=False, indent=1))
        return 0

    records = reader.extract_all()
    if a.no_events:
        records = [r for r in records if r["record_type"] == "claim"]
    print("[提取] %d 条记录（claims+events）" % len(records))

    syncer.push_records(records, dry_run=a.dry_run)
    pulled = syncer.pull_records(dry_run=a.dry_run)
    if a.inbox:
        syncer.write_inbox(pulled, a.inbox)

    print(json.dumps({
        "pushed": len(syncer.report["push"]),
        "pulled": len(syncer.report["pull"]),
        "unchanged": syncer.report["none"],
        "conflicts": len(syncer.report["conflicts"]),
    }, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
