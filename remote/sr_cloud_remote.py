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

健壮性约定（2026-10 修复，见仓库 issue「remote/ webdav 通道缺陷」）：
  * 网络调用（webdav/s3）对 429/500/502/503/504 与连接错误指数退避重试
    （1.5→3→6→12→24s，至多 6 次）；批量拉取每条之间默认节流 0.5s（--throttle 可调，0 关闭）。
  * state.json 缺失/损坏时不静默冒充「云端为空」：枚举 records/*.json 重建最小账本，
    并在 stderr 明示 warning；写通道（propose/revise/forget）在账本缺失而 records/ 非空时
    **拒绝写入**（防把 200 条记录的账本覆盖成 1 条），除非显式 --rebuild-state。

用法：
  python sr_cloud_remote.py sync   --backend local --root <云端目录> [--db mirror.sqlite3] [--throttle 0.5]
  python sr_cloud_remote.py recall "查询词" [--db mirror.sqlite3] [--limit 20] [--kind fact] [--json]
  python sr_cloud_remote.py show   <ref> [--db mirror.sqlite3] [--json]
  python sr_cloud_remote.py status [--db mirror.sqlite3] [--cloud --backend local --root <目录>]
  python sr_cloud_remote.py propose --subject "DSH" --predicate "uses" --value "Zen browser" \
        [--kind fact] --backend webdav --url http://… --user … --pwd … [--db mirror.sqlite3] [--rebuild-state]
  python sr_cloud_remote.py revise <ref> --value "新内容" [--predicate …] [--kind …] [--force] [--rebuild-state]
  python sr_cloud_remote.py forget <ref> [--force] [--rebuild-state]

webdav/s3 的凭据也可用环境变量：SR_CLOUD_URL/USER/PWD/BUCKET/ENDPOINT/ACCESS_KEY/SECRET_KEY；
批量节流用 SR_CLOUD_THROTTLE（秒，0 关闭）。--json 仅 recall/show 支持，其他子命令没有该开关。
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

# ---------------------------------------------------------------- 网络重试与节流

RETRY_STATUS_CODES = frozenset({429, 500, 502, 503, 504})
RETRY_DELAYS = (1.5, 3.0, 6.0, 12.0, 24.0)  # 指数退避序列；共 len+1 = 6 次尝试
THROTTLE_DEFAULT = 0.5                       # 批量拉取时每条记录之间的节流（秒）


def _status_of(exc):
    """尽最大努力从异常里挖出 HTTP 状态码（requests / httpx / botocore 形态各异）。"""
    for attr in ("status_code", "code"):
        v = getattr(exc, attr, None)
        if isinstance(v, int):
            return v
    resp = getattr(exc, "response", None)
    if isinstance(resp, dict):
        # botocore ClientError：e.response["ResponseMetadata"]["HTTPStatusCode"]
        md = resp.get("ResponseMetadata") or {}
        v = md.get("HTTPStatusCode")
        if isinstance(v, int):
            return v
    else:
        # requests / httpx：状态码在 response 对象上（HTTPError.response.status_code）
        v = getattr(resp, "status_code", None)
        if isinstance(v, int):
            return v
    return None


def _is_not_found(exc) -> bool:
    """「对象不存在」判定——各后端形态不同，必须都能认出来：

    - requests/httpx：response.status_code == 404
    - webdav4：**ResourceNotFound**（ClientError 子类，只有 .path，没有 status_code！）
    - boto3：ClientError["Error"]["Code"] in ("404", "NoSuchKey", "NotFound")
    - 本地目录：FileNotFoundError

    为什么关键：把 404 误判成网络错误会退避重试若干次（白等），更糟的是
    SystemExit 会穿透 get_bytes 的 `except Exception`，让「账本缺失」直接杀死命令，
    而不是走 records/ 回退扫描。
    """
    if isinstance(exc, FileNotFoundError):
        return True
    if _status_of(exc) == 404:
        return True
    name = type(exc).__name__
    if name in ("ResourceNotFound", "NoSuchKey", "NotFound", "NoSuchKeyError"):
        return True
    resp = getattr(exc, "response", None)
    if isinstance(resp, dict):
        code = (resp.get("Error") or {}).get("Code")
        if code in ("404", "NoSuchKey", "NotFound"):
            return True
    return False


def _is_retryable(exc) -> bool:
    """是否值得退避重试：限流/服务端 5xx/连接层抖动算；参数错误、404 不算。"""
    if isinstance(exc, (socket.timeout, TimeoutError, ConnectionError)):
        return True
    name = type(exc).__name__
    if name in ("ConnectionError", "Timeout", "ReadTimeout", "ConnectTimeout",
                "ChunkedEncodingError", "ProtocolError", "SSLError"):
        return True
    resp = getattr(exc, "response", None)
    if isinstance(resp, dict):
        code = (resp.get("Error") or {}).get("Code")
        if code in ("RequestTimeout", "Throttling", "ThrottlingException",
                    "SlowDown", "ServiceUnavailable", "InternalError"):
            return True
    return _status_of(exc) in RETRY_STATUS_CODES


def _friendly_net_error(exc, what, tried=1):
    """把网络异常翻译成**可操作**的报错（坚果云 BlockedTemporarily 特别点名）。

    tried 是真实尝试次数——此前无论是否真退避都写「已重试 5 次」，属于谎报，
    会把「立即失败的不可重试错误」误导成「等了 46 秒还是失败」。
    """
    body = ""
    resp = getattr(exc, "response", None)
    text = getattr(resp, "text", None)
    if isinstance(text, str):
        body = text[:500]
    st = _status_of(exc)
    retried = f"已自动退避重试 {tried - 1} 次" if tried > 1 else "未重试（不可重试的错误类型）"
    if st == 503 or "BlockedTemporarily" in body or "Too many requests" in body:
        return SystemExit(
            f"{what}：云端限流（HTTP 503 BlockedTemporarily）。坚果云免费版是**账号级惩罚性封锁**，"
            f"根目录列表都会不通，通常持续数分钟。{retried}仍失败——"
            f"请等待几分钟后重试，并降低频率（批量操作分批、加大 --throttle）。")
    if st in (401, 403):
        return SystemExit(
            f"{what}：云端认证失败（HTTP {st}）。坚果云只认**应用密码**（网页版「安全选项 → 添加应用密码」），"
            f"登录密码会 401。")
    return SystemExit(
        f"{what}：网络错误 {type(exc).__name__}: {exc}"
        f"（{retried}：{RETRY_DELAYS[0]}s→{RETRY_DELAYS[-1]}s 退避序列）")


def net_call(fn, what, delays=RETRY_DELAYS):
    """带指数退避的网络调用。「不存在」原样抛出（调用方按缺失处理），不重试。"""
    attempts = len(delays) + 1
    for i in range(attempts):
        try:
            return fn()
        except Exception as e:  # noqa: BLE001 —— 网络层异常形态太多，统一在此收敛
            if _is_not_found(e):
                raise  # 不存在：读=返回 None，列=空前缀，写=允许覆盖
            if i == attempts - 1 or not _is_retryable(e):
                raise _friendly_net_error(e, what, tried=i + 1)
            time.sleep(delays[i])
    raise AssertionError("unreachable")  # pragma: no cover


def resolve_throttle(value=None) -> float:
    """节流秒数：参数 > 环境变量 SR_CLOUD_THROTTLE > 默认 0.5；0 表示关闭。"""
    if value is None:
        value = os.environ.get("SR_CLOUD_THROTTLE")
    if value is None:
        return THROTTLE_DEFAULT
    try:
        return max(0.0, float(value))
    except (TypeError, ValueError):
        return THROTTLE_DEFAULT


# ---------------------------------------------------------------- 云端后端

class LocalDirBackend:
    """本地目录即云端（测试/同机使用），与 home 端 LocalDirBackend 同布局。"""

    network = False

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

    def list_keys(self, prefix=""):
        """枚举 prefix 下的直接子级 key（与 get_bytes/put_bytes 同一命名空间）。"""
        base = self._path(prefix) if prefix else self.root
        if not os.path.isdir(base):
            return []
        return [f"{prefix}{fn}" for fn in os.listdir(base)
                if os.path.isfile(os.path.join(base, fn))]

    def meta(self, key):
        p = self._path(key)
        return {"exists": os.path.exists(p), "size": os.path.getsize(p) if os.path.exists(p) else 0}


class WebDAVBackend:
    """WebDAV 后端（webdav4 客户端），与 home 端同名封装。"""

    network = True

    def __init__(self, url, user=None, pwd=None, root=""):
        from webdav4.client import Client  # pip install webdav4
        # webdav4.client.Client 的签名是 (base_url, auth=None, ...)——**没有 url 参数**，
        # 写 url= 会在任何调用前抛 TypeError（remote 层曾因此整条通道不可用）。
        self.client = Client(base_url=url, auth=(user or "", pwd or "") if user is not None else None)
        self.root = (root or "").strip("/")

    def _key(self, key):
        return f"{self.root}/{key}" if self.root else key

    def get_bytes(self, key):
        import io
        buf = io.BytesIO()
        try:
            net_call(lambda: self.client.download_fileobj(self._key(key), buf), f"下载 {key}")
            return buf.getvalue()
        except Exception:
            # 只有「不存在」会走到这里（net_call 对网络错误抛的是 SystemExit，
            # 不是 Exception 子类，不会被吞）——返回 None 让调用方按缺失处理，
            # 云端不可达时则连本命令一起快速失败，绝不静默冒充空数据。
            return None

    def put_bytes(self, key, data: bytes):
        import io
        net_call(lambda: self.client.upload_fileobj(io.BytesIO(data), self._key(key), overwrite=True),
                 f"上传 {key}")

    def list_keys(self, prefix=""):
        """单次 PROPFIND Depth:1 枚举 prefix 直接子级（records/ 平铺，无需递归）。

        返回的 name 相对 base_url，即与 get_bytes/put_bytes 同一命名空间的完整 key。
        """
        path = self._key(prefix) or "/"
        try:
            items = net_call(lambda: self.client.ls(path, detail=True), f"枚举 {path}")
        except Exception:
            return []  # 目录不存在 = 空前缀（干净的新云端）
        out = []
        for it in items or []:
            name = (it.get("name") if isinstance(it, dict) else it) or ""
            name = str(name).strip()
            if not name or name.endswith("/"):
                continue  # 目录条目（PROPFIND 对集合给带斜杠的 href）
            out.append(name)
        return out

    def meta(self, key):
        try:
            info = net_call(lambda: self.client.info(self._key(key)), f"探测 {key}")
            return {"exists": True, "size": int(info.get("size", 0))}
        except Exception:
            return {"exists": False, "size": 0}


class S3Backend:
    """S3 兼容后端（boto3：OSS/COS/R2/MinIO），与 home 端同名封装。"""

    network = True

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
            r = net_call(lambda: self.client.get_object(Bucket=self.bucket, Key=self._key(key)),
                         f"下载 {key}")
            return r["Body"].read()
        except Exception:
            return None  # NoSuchKey/404 → 按缺失处理

    def put_bytes(self, key, data: bytes):
        net_call(lambda: self.client.put_object(Bucket=self.bucket, Key=self._key(key), Body=data),
                 f"上传 {key}")

    def list_keys(self, prefix=""):
        """ListObjectsV2 分页枚举（S3 单次最多 1000 key）。"""
        p = self._key(prefix)
        out, token = [], None
        while True:
            kw = dict(Bucket=self.bucket, Prefix=p)
            if token:
                kw["ContinuationToken"] = token
            r = net_call(lambda: self.client.list_objects_v2(**kw), f"枚举 {p}")
            for o in (r.get("Contents") or []):
                k = o.get("Key")
                if k:
                    out.append(k)
            if not r.get("IsTruncated"):
                return out
            token = r.get("NextContinuationToken")

    def meta(self, key):
        try:
            r = net_call(lambda: self.client.head_object(Bucket=self.bucket, Key=self._key(key)),
                         f"探测 {key}")
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


def _parse_ts(value) -> float:
    """把 updated_at（ISO8601 字符串或 epoch 秒）转成可比较的浮点秒；失败返回 0。"""
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
        # 统一按 UTC 解析；mktime 按本地时区解释，但所有记录过同一转换，
        # 相对次序不变，足以支撑增量判断（fallback 账本专用）。
        return time.mktime(time.strptime(s[:19], "%Y-%m-%dT%H:%M:%S"))
    except Exception:
        return 0.0


def scan_record_objects(backend, throttle=None, want_bodies=False):
    """枚举 records/ 下的记录对象，重建最小账本 {ref: meta}。

    仅在 state.json 缺失/损坏时调用。记录本体的 ref / record_type / canonical_hash
    已足够重建 home 端账本的同构条目（key 即对象路径，updated_ts 取记录 updated_at）。
    want_bodies=True 时同时返回 {ref: 记录正文}——调用方（sync）随后要把这些记录
    拉进镜像，复用可避免同一轮里把每条记录 GET 两遍（200 条 ×2 的节奏足以把
    坚果云免费版打进账号级 503）。
    """
    ledger = {}
    bodies = {}
    step = resolve_throttle(throttle)
    for key in backend.list_keys(RECORD_PREFIX):
        if not key.endswith(".json"):
            continue
        rest = key[len(RECORD_PREFIX):] if key.startswith(RECORD_PREFIX) else key
        if "/" in rest:
            continue  # 只要直接子级
        if step and getattr(backend, "network", False):
            time.sleep(step)  # 批量 GET 节流，避免被判定为滥用
        raw = backend.get_bytes(key)
        if not raw:
            continue
        try:
            rec = json.loads(raw.decode("utf-8"))
        except Exception:
            continue
        ref = rec.get("ref")
        if not ref:
            continue
        ledger[ref] = {
            "hash": canonical_hash(rec),
            "updated_ts": _parse_ts(rec.get("updated_at")),
            "key": key,
            "record_type": rec.get("record_type", "claim"),
        }
        if want_bodies:
            bodies[ref] = rec
    return ledger, bodies


def load_ledger(backend, warn=True, throttle=None, want_bodies=False):
    """读云端账本。返回 (ledger, info)。

    info["source"]:
      "state" — state.json 存在且可解析（权威账本，写通道放心用）
      "scan"  — state.json 缺失/损坏，但 records/ 有 N 条对象 → 扫描重建。
                读可用（带 warning）；**写前必须校验**（见 guard_writable_ledger）。
      "empty" — state.json 缺失且 records/ 为空（干净的新云端，可直接写）
    """
    raw = backend.get_bytes(STATE_KEY)
    if raw:
        try:
            cs = json.loads(raw.decode("utf-8"))
            if isinstance(cs, dict) and isinstance(cs.get("records"), dict):
                return cs, {"source": "state", "objects": len(cs["records"]), "bodies": {}}
            why = "损坏（不是合法账本 JSON）"
        except Exception as e:
            why = f"损坏（{type(e).__name__}）"
        if warn:
            print(f"warning: {STATE_KEY} {why}，回退到 {RECORD_PREFIX} 直接扫描", file=sys.stderr)
    else:
        why = "缺失"
    scanned, bodies = scan_record_objects(backend, throttle=throttle, want_bodies=want_bodies)
    if scanned:
        if warn:
            print(f"warning: {STATE_KEY} {why}，但 {RECORD_PREFIX} 发现 {len(scanned)} 条记录对象"
                  f" —— 已回退为直接扫描重建索引（只读安全）；写入请先修复 home 端同步，"
                  f"或显式加 --rebuild-state 用扫描结果重建账本", file=sys.stderr)
        return ({"schema": SCHEMA_VERSION, "records": scanned},
                {"source": "scan", "objects": len(scanned), "bodies": bodies})
    return {"schema": SCHEMA_VERSION, "records": {}}, {"source": "empty", "objects": 0, "bodies": {}}


def read_cloud_state(backend, warn=True):
    """兼容入口：只取账本 dict。需要 source 信息（写前校验）的调用方用 load_ledger。"""
    cs, _info = load_ledger(backend, warn=warn)
    return cs


def write_cloud_state(backend, cs: dict):
    backend.put_bytes(STATE_KEY, json.dumps(cs, ensure_ascii=False, sort_keys=True).encode("utf-8"))


def guard_writable_ledger(info, rebuild: bool):
    """写前强制校验账本（缺陷3：防止把 200 条记录的账本覆盖成 1 条）。

    state.json 缺失而 records/ 非空时，read-modify-write 没有任何「其他写入者的条目」
    可保留——整份写回即数据丢失事故。此时拒绝写入，除非调用方显式确认 --rebuild-state。
    """
    if info.get("source") == "scan" and not rebuild:
        raise SystemExit(
            f"拒绝写入：云端 {STATE_KEY} 缺失，但 {RECORD_PREFIX} 有 {info.get('objects')} 条记录对象。"
            f"此时写入会把账本重建成只含本条目的内容，home 端后续按账本增量同步可能把其余记录"
            f"判为已删除。请先修复 home 端同步（跑一轮 cloudsync 生成 {STATE_KEY}），"
            f"或确认要以扫描结果重建账本后加 --rebuild-state。")

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
    cs, linfo = load_ledger(backend, throttle=getattr(a, "throttle", None), want_bodies=True)
    cloud_records = cs.get("records") or {}
    scanned_bodies = linfo.get("bodies") or {}
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
            if getattr(backend, "network", False):
                time.sleep(resolve_throttle(getattr(a, "throttle", None)))  # 逐条节流防限流
            raw = backend.get_bytes(meta.get("key") or record_key(ref))
            if raw is None:
                # 回退扫描已读过正文：直接复用，别把每条记录在一轮里 GET 两遍
                rec = scanned_bodies.get(ref)
                if rec is None:
                    bad += 1
                    continue
            else:
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
                      "state_source": linfo["source"],
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
        cs, linfo = load_ledger(backend)
        records = cs.get("records") or {}
        info["cloud"] = {"records": len(records),
                         "claims": sum(1 for m in records.values() if m.get("record_type") == "claim"),
                         "events": sum(1 for m in records.values() if m.get("record_type") == "event"),
                         "state_source": linfo["source"]}
    print(json.dumps(info, ensure_ascii=False, indent=1))
    return 0


def fetch_cloud_record(backend, ref):
    """从云端取一条记录的正文；不存在返回 (None, None)。

    同时返回云端账本里的 meta（hash/updated_ts/key），便于调用方做冲突判断。
    """
    cs, _linfo = load_ledger(backend)
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


def put_cloud_record(backend, rec, cs=None, allow_rebuild=False):
    """把一条记录写进云端账本（记录对象 + state.json 的 hash/updated_ts）。

    读改写：先读云端 state.json，只改本 ref 的条目，**保留其他写入者的条目**。
    返回 (hash, key, updated_ts)。

    allow_rebuild=False 时，账本缺失而 records/ 非空 → 拒绝写入（guard_writable_ledger）：
    那种状态下没有任何他人条目可保留，整份写回就是覆盖事故。
    """
    linfo = None
    if cs is None:
        cs, linfo = load_ledger(backend)
        guard_writable_ledger(linfo, allow_rebuild)
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
        h, key, ts = put_cloud_record(backend, rec, allow_rebuild=getattr(a, "rebuild_state", False))
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
    h, key, ts = put_cloud_record(backend, rec, allow_rebuild=getattr(a, "rebuild_state", False))
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
    h, key, ts = put_cloud_record(backend, rec, allow_rebuild=getattr(a, "rebuild_state", False))
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
                        help="local: 云端目录 / webdav: 根路径（留空=云端根，records/ 平铺时用）/ s3: prefix")
    common.add_argument("--url", default=os.environ.get("SR_CLOUD_URL"))
    common.add_argument("--user", default=os.environ.get("SR_CLOUD_USER"))
    common.add_argument("--pwd", default=os.environ.get("SR_CLOUD_PWD"))
    common.add_argument("--bucket", default=os.environ.get("SR_CLOUD_BUCKET"))
    common.add_argument("--endpoint", default=os.environ.get("SR_CLOUD_ENDPOINT"))
    common.add_argument("--access-key", default=os.environ.get("SR_CLOUD_ACCESS_KEY"))
    common.add_argument("--secret-key", default=os.environ.get("SR_CLOUD_SECRET_KEY"))

    p = sub.add_parser("sync", parents=[common], help="拉取云端记录到本地镜像")
    p.add_argument("--throttle", type=float, default=None,
                   help="批量拉取时每条记录之间的节流秒数（默认 0.5；0 关闭；环境变量 SR_CLOUD_THROTTLE）")
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
    p.add_argument("--rebuild-state", action="store_true",
                   help="云端 state.json 缺失而 records/ 非空时，确认以扫描结果重建账本后再写入")
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
    p.add_argument("--rebuild-state", action="store_true",
                   help="云端 state.json 缺失而 records/ 非空时，确认以扫描结果重建账本后再写入")
    p.set_defaults(fn=cmd_revise)

    p = sub.add_parser("forget", parents=[common], help="废弃一条记忆（软删除，home 桥不再转写）")
    p.add_argument("ref")
    p.add_argument("--note")
    p.add_argument("--force", action="store_true", help="允许废弃他人的记录")
    p.add_argument("--rebuild-state", action="store_true",
                   help="云端 state.json 缺失而 records/ 非空时，确认以扫描结果重建账本后再写入")
    p.set_defaults(fn=cmd_forget)

    a = ap.parse_args(argv)
    return a.fn(a)


if __name__ == "__main__":
    raise SystemExit(main())
