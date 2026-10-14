#!/usr/bin/env python3
"""sr_cloud_remote.py — 远端 agent 的云端记忆读写 CLI。

与 home 端增量同步桥（cloudsync_run.py / incremental_sync.py，协议
scope-recall.incremental-sync/1）逐字节兼容：

  云端布局   state.json（协调账本）+ records/<sha1(ref)>.json（一条记录一个对象）
  读（sync） 拉取云端记录到本地 SQLite 镜像，recall/显示都在镜像上做，不碰云端写通道
  写（propose）构造 claim 形记录上云 → home 桥拉到后经官方 propose_memory 进共享库
             （远端**从不**直接写共享库，与控制台的写边界同一铁律）
  改（revise）按 ref 修订已上云的记忆，默认只改本 agent 自己提议的（他人记录需 --force）
  删（forget）按 ref 置 superseded + suppressed=1，home 桥据此跳过，不再转写进共享库

零第三方依赖即可用 local 后端；webdav/s3 后端按需 pip install webdav4 / boto3。

用法：
  python sr_cloud_remote.py sync   --backend local --root <云端目录> [--db mirror.sqlite3]
  python sr_cloud_remote.py recall "查询词" [--db mirror.sqlite3] [--limit 20] [--kind fact] [--json]
  python sr_cloud_remote.py show   <ref> [--db mirror.sqlite3] [--json]
  python sr_cloud_remote.py status [--db mirror.sqlite3] [--cloud --backend local --root <目录>]
  python sr_cloud_remote.py propose --subject "DSH" --predicate "uses" --value "Zen browser" \
        [--kind fact] --backend webdav --url http://… --user … --pwd … [--db mirror.sqlite3]
  python sr_cloud_remote.py revise <ref> --value "新内容" [--predicate …] [--kind …] [--force]
  python sr_cloud_remote.py forget <ref> [--force]

webdav/s3 的凭据也可用环境变量：SR_CLOUD_URL/USER/PWD/BUCKET/ENDPOINT/ACCESS_KEY/SECRET_KEY。
"""
import argparse
import hashlib
import json
import os
import socket
import sqlite3
import sys
import time
import uuid

SCHEMA_VERSION = "scope-recall.incremental-sync/1"
RECORD_PREFIX = "records/"
STATE_KEY = "state.json"
DEFAULT_DB = "sr-cloud-mirror.sqlite3"


# ---------------------------------------------------------------- 云端后端

class LocalDirBackend:
    """本地目录即云端（测试/同机使用），与 home 端 LocalDirBackend 同布局。"""

    def __init__(self, root):
        self.root = os.path.abspath(root)
        os.makedirs(self.root, exist_ok=True)

    def _path(self, key):
        return os.path.join(self.root, key.replace("/", os.sep))

    def get_bytes(self, key):
        p = self._path(key)
        return open(p, "rb").read() if os.path.exists(p) else None

    def put_bytes(self, key, data: bytes):
        p = self._path(key)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        tmp = p + ".tmp"
        with open(tmp, "wb") as f:
            f.write(data)
        os.replace(tmp, p)

    def meta(self, key):
        p = self._path(key)
        return {"exists": os.path.exists(p), "size": os.path.getsize(p) if os.path.exists(p) else 0}


class WebDAVBackend:
    """WebDAV 后端（webdav4 客户端），与 home 端同名封装。"""

    def __init__(self, url, user=None, pwd=None, root=""):
        from webdav4.client import Client  # pip install webdav4
        self.client = Client(url=url, auth=(user or "", pwd or "") if user is not None else None)
        self.root = (root or "").strip("/")

    def _key(self, key):
        return f"{self.root}/{key}" if self.root else key

    def get_bytes(self, key):
        import io
        buf = io.BytesIO()
        try:
            self.client.download_fileobj(self._key(key), buf)
            return buf.getvalue()
        except Exception:
            return None

    def put_bytes(self, key, data: bytes):
        import io
        self.client.upload_fileobj(io.BytesIO(data), self._key(key), overwrite=True)

    def meta(self, key):
        try:
            info = self.client.info(self._key(key))
            return {"exists": True, "size": int(info.get("size", 0))}
        except Exception:
            return {"exists": False, "size": 0}


class S3Backend:
    """S3 兼容后端（boto3：OSS/COS/R2/MinIO），与 home 端同名封装。"""

    def __init__(self, bucket, endpoint, access_key, secret_key, region="auto", prefix=""):
        import boto3  # pip install boto3
        self.client = boto3.client("s3", endpoint_url=endpoint, aws_access_key_id=access_key,
                                   aws_secret_access_key=secret_key, region_name=region)
        self.bucket = bucket
        self.prefix = (prefix or "").strip("/")

    def _key(self, key):
        return f"{self.prefix}/{key}" if self.prefix else key

    def get_bytes(self, key):
        try:
            r = self.client.get_object(Bucket=self.bucket, Key=self._key(key))
            return r["Body"].read()
        except Exception:
            return None

    def put_bytes(self, key, data: bytes):
        self.client.put_object(Bucket=self.bucket, Key=self._key(key), Body=data)

    def meta(self, key):
        try:
            r = self.client.head_object(Bucket=self.bucket, Key=self._key(key))
            return {"exists": True, "size": r["ContentLength"]}
        except Exception:
            return {"exists": False, "size": 0}


def make_backend(kind, **kw):
    if kind == "local":
        return LocalDirBackend(kw["root"])
    if kind == "webdav":
        return WebDAVBackend(url=kw.get("url"), user=kw.get("user"), pwd=kw.get("pwd"), root=kw.get("root", ""))
    if kind == "s3":
        return S3Backend(bucket=kw["bucket"], endpoint=kw.get("endpoint"),
                         access_key=kw.get("access_key"), secret_key=kw.get("secret_key"),
                         prefix=kw.get("root", ""))
    raise SystemExit(f"未知后端 {kind}")


# ---------------------------------------------------------------- 协议（与 home 端一致）

def canonical_hash(obj) -> str:
    blob = json.dumps(obj, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(blob.encode("utf-8")).hexdigest()


def record_key(ref: str) -> str:
    return RECORD_PREFIX + hashlib.sha1(ref.encode("utf-8")).hexdigest() + ".json"


def read_cloud_state(backend) -> dict:
    raw = backend.get_bytes(STATE_KEY)
    if not raw:
        return {"schema": SCHEMA_VERSION, "records": {}}
    try:
        return json.loads(raw.decode("utf-8"))
    except Exception:
        return {"schema": SCHEMA_VERSION, "records": {}}


def write_cloud_state(backend, cs: dict):
    backend.put_bytes(STATE_KEY, json.dumps(cs, ensure_ascii=False, sort_keys=True).encode("utf-8"))


# ---------------------------------------------------------------- 本地镜像

MIRROR_DDL = """
CREATE TABLE IF NOT EXISTS claims(
  ref TEXT PRIMARY KEY, subject TEXT, predicate TEXT, kind TEXT, value_text TEXT,
  state TEXT, updated_at TEXT, scope_id TEXT, json TEXT);
CREATE TABLE IF NOT EXISTS events(
  ref TEXT PRIMARY KEY, role TEXT, origin TEXT, entry TEXT, content TEXT,
  occurred_at TEXT, updated_at TEXT, scope_id TEXT, json TEXT);
CREATE TABLE IF NOT EXISTS sync_state(
  ref TEXT PRIMARY KEY, hash TEXT, remote_ts REAL, record_type TEXT);
CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);
"""


def open_mirror(db_path):
    con = sqlite3.connect(db_path)
    con.row_factory = sqlite3.Row
    con.executescript(MIRROR_DDL)
    return con


# ---------------------------------------------------------------- 命令实现

def cmd_sync(a):
    backend = make_backend(a.backend, root=a.root, url=a.url, user=a.user, pwd=a.pwd,
                           bucket=a.bucket, endpoint=a.endpoint,
                           access_key=a.access_key, secret_key=a.secret_key)
    cs = read_cloud_state(backend)
    cloud_records = cs.get("records") or {}
    con = open_mirror(a.db)
    pulled = skipped = bad = 0
    try:
        for ref, meta in cloud_records.items():
            prev = con.execute("SELECT hash, remote_ts FROM sync_state WHERE ref=?", (ref,)).fetchone()
            if prev and prev["hash"] == meta.get("hash"):
                skipped += 1
                continue
            if prev and meta.get("updated_ts", 0) <= (prev["remote_ts"] or 0):
                skipped += 1
                continue
            raw = backend.get_bytes(meta.get("key") or record_key(ref))
            if raw is None:
                bad += 1
                continue
            try:
                rec = json.loads(raw.decode("utf-8"))
            except Exception:
                bad += 1
                continue
            if canonical_hash(rec) != meta.get("hash"):
                bad += 1  # 内容被篡改/截断，宁缺毋滥
                continue
            now_iso = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            if rec.get("record_type") == "event":
                con.execute(
                    "INSERT OR REPLACE INTO events(ref,role,origin,entry,content,occurred_at,updated_at,scope_id,json)"
                    " VALUES(?,?,?,?,?,?,?,?,?)",
                    (ref, rec.get("role"), rec.get("origin"), rec.get("entry"), rec.get("content"),
                     rec.get("occurred_at"), rec.get("updated_at"), rec.get("scope_id"),
                     json.dumps(rec, ensure_ascii=False, sort_keys=True)))
            else:
                con.execute(
                    "INSERT OR REPLACE INTO claims(ref,subject,predicate,kind,value_text,state,updated_at,scope_id,json)"
                    " VALUES(?,?,?,?,?,?,?,?,?)",
                    (ref, rec.get("subject"), rec.get("predicate"), rec.get("kind"), rec.get("value_text"),
                     rec.get("state"), rec.get("updated_at"), rec.get("scope_id"),
                     json.dumps(rec, ensure_ascii=False, sort_keys=True)))
            con.execute("INSERT OR REPLACE INTO sync_state(ref,hash,remote_ts,record_type) VALUES(?,?,?,?)",
                        (ref, meta.get("hash"), meta.get("updated_ts", 0), meta.get("record_type")))
            pulled += 1
        con.execute("INSERT OR REPLACE INTO meta(k,v) VALUES('last_sync',?)",
                    (time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),))
        con.execute("INSERT OR REPLACE INTO meta(k,v) VALUES('cloud_records',?)", (str(len(cloud_records)),))
        con.commit()
    finally:
        con.close()
    print(json.dumps({"status": "ok", "cloud_records": len(cloud_records),
                      "pulled": pulled, "unchanged": skipped, "integrity_skipped": bad},
                     ensure_ascii=False))
    return 0


def cmd_recall(a):
    con = open_mirror(a.db)
    like = f"%{a.query.strip()}%"
    rows = []
    if a.kind:
        rows += con.execute(
            "SELECT ref,subject,predicate,kind,value_text,state,updated_at FROM claims"
            " WHERE (subject LIKE ? OR predicate LIKE ? OR value_text LIKE ?) AND kind=?"
            " ORDER BY updated_at DESC LIMIT ?",
            (like, like, like, a.kind, a.limit)).fetchall()
    else:
        rows += con.execute(
            "SELECT ref,subject,predicate,kind,value_text,state,updated_at FROM claims"
            " WHERE subject LIKE ? OR predicate LIKE ? OR value_text LIKE ?"
            " ORDER BY updated_at DESC LIMIT ?",
            (like, like, like, a.limit)).fetchall()
    rows += con.execute(
        "SELECT ref,'' AS subject,'' AS predicate,'' AS kind,content AS value_text,"
        "'event' AS state,occurred_at AS updated_at FROM events"
        " WHERE content LIKE ? ORDER BY updated_at DESC LIMIT ?",
        (like, a.limit)).fetchall()
    if a.json:
        print(json.dumps([dict(r) for r in rows], ensure_ascii=False, indent=1))
        return 0
    if not rows:
        print("（镜像中无匹配记忆；先跑一次 sync）")
        return 1
    for r in rows:
        head = f"{r['subject']} {r['predicate']}".strip() if r["subject"] else "(event)"
        print(f"[{r['state']}] {head} → {str(r['value_text'])[:120]}")
        print(f"    {r['updated_at']}  {r['kind'] or 'event'}  ref={r['ref']}")
    return 0


def cmd_show(a):
    con = open_mirror(a.db)
    for table in ("claims", "events"):
        row = con.execute(f"SELECT json FROM {table} WHERE ref=?", (a.ref,)).fetchone()
        if row:
            rec = json.loads(row["json"])
            print(json.dumps(rec, ensure_ascii=False, indent=1))
            return 0
    print(f"镜像中无 {a.ref}", file=sys.stderr)
    return 1


def cmd_status(a):
    con = open_mirror(a.db)
    claims = con.execute("SELECT COUNT(*) FROM claims").fetchone()[0]
    events = con.execute("SELECT COUNT(*) FROM events").fetchone()[0]
    last = con.execute("SELECT v FROM meta WHERE k='last_sync'").fetchone()
    info = {"mirror_db": os.path.abspath(a.db), "claims": claims, "events": events,
            "last_sync": last["v"] if last else None}
    if a.cloud:
        backend = make_backend(a.backend, root=a.root, url=a.url, user=a.user, pwd=a.pwd,
                               bucket=a.bucket, endpoint=a.endpoint,
                               access_key=a.access_key, secret_key=a.secret_key)
        cs = read_cloud_state(backend)
        records = cs.get("records") or {}
        info["cloud"] = {"records": len(records),
                         "claims": sum(1 for m in records.values() if m.get("record_type") == "claim"),
                         "events": sum(1 for m in records.values() if m.get("record_type") == "event")}
    print(json.dumps(info, ensure_ascii=False, indent=1))
    return 0


def fetch_cloud_record(backend, ref):
    """从云端取一条记录的正文；不存在返回 (None, None)。

    同时返回云端账本里的 meta（hash/updated_ts/key），便于调用方做冲突判断。
    """
    cs = read_cloud_state(backend)
    meta = (cs.get("records") or {}).get(ref)
    if not meta:
        return None, None
    raw = backend.get_bytes(meta.get("key") or record_key(ref))
    if raw is None:
        return None, meta
    try:
        rec = json.loads(raw.decode("utf-8"))
    except Exception:
        return None, meta
    if canonical_hash(rec) != meta.get("hash"):
        return None, meta  # 完整性不符，宁缺毋滥
    return rec, meta


def put_cloud_record(backend, rec, cs=None):
    """把一条记录写进云端账本（记录对象 + state.json 的 hash/updated_ts）。

    读改写：先读云端 state.json，只改本 ref 的条目，**保留其他写入者的条目**。
    返回 (hash, key, updated_ts)。
    """
    if cs is None:
        cs = read_cloud_state(backend)
    ref = rec["ref"]
    key = record_key(ref)
    now_ts = time.time()
    blob = json.dumps(rec, ensure_ascii=False, sort_keys=True).encode("utf-8")
    backend.put_bytes(key, blob)
    h = canonical_hash(rec)
    cs.setdefault("records", {})[ref] = {
        "hash": h,
        "updated_ts": now_ts,
        "key": key,
        "record_type": rec.get("record_type", "claim"),
    }
    write_cloud_state(backend, cs)
    return h, key, now_ts


def upsert_mirror(con, rec, h, now_ts):
    """把一条云端记录写入本地镜像（claims/events 按 record_type 分流）。"""
    now_iso = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    if rec.get("record_type") == "event":
        con.execute(
            "INSERT OR REPLACE INTO events(ref,role,origin,entry,content,occurred_at,updated_at,scope_id,json)"
            " VALUES(?,?,?,?,?,?,?,?,?)",
            (rec.get("ref"), rec.get("role"), rec.get("origin"), rec.get("entry"), rec.get("content"),
             rec.get("occurred_at"), rec.get("updated_at"), rec.get("scope_id"),
             json.dumps(rec, ensure_ascii=False, sort_keys=True)))
    else:
        con.execute(
            "INSERT OR REPLACE INTO claims(ref,subject,predicate,kind,value_text,state,updated_at,scope_id,json)"
            " VALUES(?,?,?,?,?,?,?,?,?)",
            (rec.get("ref"), rec.get("subject"), rec.get("predicate"), rec.get("kind"),
             rec.get("value_text"), rec.get("state"), rec.get("updated_at"), rec.get("scope_id"),
             json.dumps(rec, ensure_ascii=False, sort_keys=True)))
    con.execute("INSERT OR REPLACE INTO sync_state(ref,hash,remote_ts,record_type) VALUES(?,?,?,?)",
                (rec.get("ref"), h, now_ts, rec.get("record_type", "claim")))
    return now_iso


def cmd_propose(a):
    """远端写通道：上云一条 claim 形记录，home 桥会转官方 propose_memory 进共享库。"""
    backend = make_backend(a.backend, root=a.root, url=a.url, user=a.user, pwd=a.pwd,
                           bucket=a.bucket, endpoint=a.endpoint,
                           access_key=a.access_key, secret_key=a.secret_key)
    now_ts = time.time()
    now_iso = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    ref = "remote-%s-%s-%s" % (socket.gethostname().lower().replace(" ", "-"),
                               time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()),
                               uuid.uuid4().hex[:6])
    rec = {
        "ref": ref,
        "record_type": "claim",
        "scope_id": None,
        "project_id": None,
        "branch_id": None,
        "subject": a.subject.strip(),
        "predicate": (a.predicate or "").strip(),
        "kind": a.kind,
        "slot_key": None,
        "revision": None,
        "state": "proposed",
        "value_text": a.value_text.strip(),
        "conditions": None,
        "valid_from": a.valid_from,
        "valid_to": None,
        "read_blocked": 0,
        "suppressed": 0,
        "updated_at": now_iso,
        "origin_agent": socket.gethostname(),
        "origin_note": a.note or "proposed by remote agent",
    }
    con = open_mirror(a.db)
    try:
        # 内容去重：同 subject+predicate+value 已提议过就拒绝（home 端还会再做一次精确去重）
        content_hash = canonical_hash({"subject": rec["subject"], "predicate": rec["predicate"],
                                       "kind": rec["kind"], "value_text": rec["value_text"]})
        seen = con.execute("SELECT v FROM meta WHERE k=?", (f"content:{content_hash}",)).fetchone()
        if seen and not a.force:
            print(json.dumps({"status": "duplicate", "ref": seen["v"],
                              "hint": "同一内容已提议过；确要重复请加 --force"}, ensure_ascii=False))
            return 1
        h, key, ts = put_cloud_record(backend, rec)
        upsert_mirror(con, rec, h, ts)
        con.execute("INSERT OR REPLACE INTO meta(k,v) VALUES(?,?)", (f"content:{content_hash}", ref))
        con.commit()
        print(json.dumps({"status": "proposed", "ref": ref, "cloud_key": key,
                          "next": "home 桥下一轮会把这条转为官方 propose_memory 进共享库"},
                         ensure_ascii=False))
        return 0
    finally:
        con.close()


def _load_writable_record(a, backend):
    """取出一条「本 agent 有权改」的云端记录；无权/不存在时返回 (None, 错误 dict)。"""
    rec, meta = fetch_cloud_record(backend, a.ref)
    if rec is None:
        return None, {"status": "not_found", "ref": a.ref,
                      "hint": "云端没有这条 ref，或对象完整性校验失败；先跑一次 sync 再试"}
    me = socket.gethostname()
    owner = rec.get("origin_agent")
    if owner and owner != me and not a.force:
        return None, {"status": "denied", "ref": a.ref, "owner": owner,
                      "hint": "这条不是本 agent 写的；确要改动请加 --force"}
    if rec.get("state") == "superseded" and not a.force:
        return None, {"status": "already_forgotten", "ref": a.ref,
                      "hint": "这条已废弃；确要改动请加 --force"}
    return rec, None


def cmd_revise(a):
    """按 ref 修订一条已上云的记忆（默认只改本 agent 自己提议的）。"""
    backend = make_backend(a.backend, root=a.root, url=a.url, user=a.user, pwd=a.pwd,
                           bucket=a.bucket, endpoint=a.endpoint,
                           access_key=a.access_key, secret_key=a.secret_key)
    rec, err = _load_writable_record(a, backend)
    if err:
        print(json.dumps(err, ensure_ascii=False))
        return 1
    me = socket.gethostname()
    now_iso = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    if a.value_text is not None:
        rec["value_text"] = a.value_text.strip()
    if a.predicate is not None:
        rec["predicate"] = a.predicate.strip()
    if a.kind:
        rec["kind"] = a.kind
    if a.subject:
        rec["subject"] = a.subject.strip()
    rec["revision"] = (rec.get("revision") or 0) + 1
    rec["updated_at"] = now_iso
    rec["last_editor"] = me
    rec["origin_note"] = a.note or "revised by remote agent"
    h, key, ts = put_cloud_record(backend, rec)
    con = open_mirror(a.db)
    try:
        upsert_mirror(con, rec, h, ts)
        con.commit()
    finally:
        con.close()
    print(json.dumps({"status": "revised", "ref": rec["ref"], "revision": rec["revision"],
                      "value_text": rec.get("value_text"), "cloud_key": key,
                      "next": "home 桥下一轮会拉到新版本"}, ensure_ascii=False))
    return 0


def cmd_forget(a):
    """按 ref 废弃一条记忆（软删除：state=superseded + suppressed=1，home 桥不再转写）。"""
    backend = make_backend(a.backend, root=a.root, url=a.url, user=a.user, pwd=a.pwd,
                           bucket=a.bucket, endpoint=a.endpoint,
                           access_key=a.access_key, secret_key=a.secret_key)
    rec, err = _load_writable_record(a, backend)
    if err:
        print(json.dumps(err, ensure_ascii=False))
        return 1
    me = socket.gethostname()
    now_iso = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    rec["state"] = "superseded"
    rec["suppressed"] = 1
    rec["updated_at"] = now_iso
    rec["last_editor"] = me
    rec["origin_note"] = a.note or "forgotten by remote agent"
    h, key, ts = put_cloud_record(backend, rec)
    con = open_mirror(a.db)
    try:
        upsert_mirror(con, rec, h, ts)
        con.commit()
    finally:
        con.close()
    print(json.dumps({"status": "forgotten", "ref": rec["ref"], "cloud_key": key,
                      "note": "已置 superseded；home 桥不会再把它转写进共享库"}, ensure_ascii=False))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description="远端 agent 的云端记忆读写 CLI（协议 scope-recall.incremental-sync/1）")
    sub = ap.add_subparsers(dest="cmd", required=True)

    # 公共参数（后端与凭据）挂到每个子命令上，允许 `sync --backend …` 或 `--backend … sync` 两种写法
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--db", default=DEFAULT_DB, help="本地镜像 SQLite（默认 ./sr-cloud-mirror.sqlite3）")
    common.add_argument("--backend", default=os.environ.get("SR_CLOUD_BACKEND", "local"),
                        choices=["local", "webdav", "s3"])
    common.add_argument("--root", default=os.environ.get("SR_CLOUD_ROOT", ""),
                        help="local: 云端目录 / webdav: 根路径 / s3: prefix")
    common.add_argument("--url", default=os.environ.get("SR_CLOUD_URL"))
    common.add_argument("--user", default=os.environ.get("SR_CLOUD_USER"))
    common.add_argument("--pwd", default=os.environ.get("SR_CLOUD_PWD"))
    common.add_argument("--bucket", default=os.environ.get("SR_CLOUD_BUCKET"))
    common.add_argument("--endpoint", default=os.environ.get("SR_CLOUD_ENDPOINT"))
    common.add_argument("--access-key", default=os.environ.get("SR_CLOUD_ACCESS_KEY"))
    common.add_argument("--secret-key", default=os.environ.get("SR_CLOUD_SECRET_KEY"))

    p = sub.add_parser("sync", parents=[common], help="拉取云端记录到本地镜像")
    p.set_defaults(fn=cmd_sync)

    p = sub.add_parser("recall", parents=[common], help="检索镜像记忆")
    p.add_argument("query")
    p.add_argument("--limit", type=int, default=20)
    p.add_argument("--kind")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_recall)

    p = sub.add_parser("show", parents=[common], help="显示一条记忆的完整 JSON")
    p.add_argument("ref")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_show)

    p = sub.add_parser("status", parents=[common], help="镜像统计；--cloud 附云端概览")
    p.add_argument("--cloud", action="store_true")
    p.set_defaults(fn=cmd_status)

    p = sub.add_parser("propose", parents=[common], help="提议一条记忆（上云，home 桥转官方 propose_memory）")
    p.add_argument("--subject", required=True)
    p.add_argument("--predicate", default="")
    p.add_argument("--value", required=True, dest="value_text")
    p.add_argument("--kind", default="fact",
                   choices=["fact", "preference", "constraint", "decision", "procedure", "intention", "alias"])
    p.add_argument("--valid-from")
    p.add_argument("--note")
    p.add_argument("--force", action="store_true", help="允许与已提议内容重复")
    p.set_defaults(fn=cmd_propose)

    p = sub.add_parser("revise", parents=[common], help="修订一条已上云的记忆（默认只改本 agent 提议的）")
    p.add_argument("ref")
    p.add_argument("--value", dest="value_text", default=None, help="新的记忆内容")
    p.add_argument("--subject", default=None)
    p.add_argument("--predicate", default=None)
    p.add_argument("--kind", default=None,
                   choices=["fact", "preference", "constraint", "decision", "procedure", "intention", "alias"])
    p.add_argument("--note")
    p.add_argument("--force", action="store_true", help="允许改动他人/已废弃的记录")
    p.set_defaults(fn=cmd_revise)

    p = sub.add_parser("forget", parents=[common], help="废弃一条记忆（软删除，home 桥不再转写）")
    p.add_argument("ref")
    p.add_argument("--note")
    p.add_argument("--force", action="store_true", help="允许废弃他人的记录")
    p.set_defaults(fn=cmd_forget)

    a = ap.parse_args(argv)
    return a.fn(a)


if __name__ == "__main__":
    raise SystemExit(main())
