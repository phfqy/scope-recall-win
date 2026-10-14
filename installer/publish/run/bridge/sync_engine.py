#!/usr/bin/env python3
"""sync_engine.py — 记忆文件双向同步引擎（侧端用）。

存储后端抽象 + 双向同步 + 变更检测。
- 本地→云端：本地镜像记忆文件更新（hash/mtime 变化）→ 自动上传
- 云端→本地：云端文件更新 → 自动下载合并
- 冲突规则：比较 (mtime, hash)，云端新则拉、本地新则推、同时则云端胜（保守）
- 后端：LocalDir（测试/自建）/ WebDAV（AList·QDAV·公共点）/ S3（OSS·COS·R2）
- 网络健壮性：webdav/s3 的全部网络调用对 429/500/502/503/504 与连接抖动指数退避重试
  （1.5→3→6→12→24s，至多 6 次）；坚果云免费版的 503 BlockedTemporarily 是账号级
  封锁，重试耗尽后抛**可操作**的 RuntimeError（watch 模式跳过本轮，绝不死扛成静默失败）
"""
import os, sys, json, time, hashlib, argparse, socket

# ---------------------------------------------------------------- 网络重试
RETRY_STATUS_CODES = frozenset({429, 500, 502, 503, 504})
RETRY_DELAYS = (1.5, 3.0, 6.0, 12.0, 24.0)  # 共 len+1 = 6 次尝试


def _status_of(exc):
    """尽最大努力从异常里挖 HTTP 状态码（requests/httpx 在 response 上，botocore 在 dict 里）。"""
    for attr in ("status_code", "code"):
        v = getattr(exc, attr, None)
        if isinstance(v, int):
            return v
    resp = getattr(exc, "response", None)
    if isinstance(resp, dict):
        v = (resp.get("ResponseMetadata") or {}).get("HTTPStatusCode")
        return v if isinstance(v, int) else None
    v = getattr(resp, "status_code", None)
    return v if isinstance(v, int) else None


def _is_retryable(exc) -> bool:
    if isinstance(exc, (socket.timeout, TimeoutError, ConnectionError)):
        return True
    if type(exc).__name__ in ("ConnectionError", "Timeout", "ReadTimeout",
                              "ConnectTimeout", "ChunkedEncodingError", "ProtocolError", "SSLError"):
        return True
    resp = getattr(exc, "response", None)
    if isinstance(resp, dict):
        if (resp.get("Error") or {}).get("Code") in ("RequestTimeout", "Throttling",
                                                     "ThrottlingException", "SlowDown",
                                                     "ServiceUnavailable", "InternalError"):
            return True
    return _status_of(exc) in RETRY_STATUS_CODES


def net_retry(fn, what, delays=RETRY_DELAYS):
    """带指数退避的网络调用。404 原样抛出（调用方按「不存在」处理）。"""
    attempts = len(delays) + 1
    last = None
    for i in range(attempts):
        try:
            return fn()
        except Exception as e:  # noqa: BLE001 —— 网络异常形态太多，统一收敛
            last = e
            st = _status_of(e)
            if st == 404:
                raise
            if st in (401, 403) or not _is_retryable(e) or i == attempts - 1:
                body = ""
                text = getattr(getattr(e, "response", None), "text", None)
                if isinstance(text, str):
                    body = text[:300]
                if st == 503 or "BlockedTemporarily" in body or "Too many requests" in body:
                    raise RuntimeError(
                        f"{what}：云端限流（HTTP 503 BlockedTemporarily，坚果云免费版为账号级封锁，"
                        f"通常持续数分钟）。已退避重试 {len(delays)} 次仍失败——请等待几分钟后重试。")
                if st in (401, 403):
                    raise RuntimeError(
                        f"{what}：云端认证失败（HTTP {st}）。坚果云须用应用密码而非登录密码。")
                raise RuntimeError(
                    f"{what}：网络错误 {type(e).__name__}: {e}（已退避重试 {len(delays)} 次）")
            time.sleep(delays[i])
    raise RuntimeError(f"{what}：网络错误 {type(last).__name__}: {last}")

class StorageBackend:
    """云端文件存储抽象：以 'key'（相对路径）为单位"""
    def meta(self, key):
        """返回 dict(size, mtime, etag) 或 None（不存在）"""
        raise NotImplementedError
    def upload(self, local_path, key):
        raise NotImplementedError
    def download(self, key, local_path):
        raise NotImplementedError
    def list(self, prefix=""):
        """返回 [(key, meta)]"""
        raise NotImplementedError
    def put_bytes(self, key, data: bytes):
        """写字节内容（云端状态文件用；默认经临时文件）"""
        import tempfile
        fd, p = tempfile.mkstemp(suffix=".tmp")
        with os.fdopen(fd, "wb") as f:
            f.write(data)
        try:
            self.upload(p, key)
        finally:
            os.remove(p)
    def get_bytes(self, key) -> bytes | None:
        """读字节内容（云端状态文件用；不存在返回 None）"""
        import tempfile
        fd, p = tempfile.mkstemp(suffix=".tmp")
        os.close(fd)
        try:
            self.download(key, p)
            with open(p, "rb") as f:
                return f.read()
        except Exception:
            return None
        finally:
            if os.path.exists(p):
                os.remove(p)

class LocalDirBackend(StorageBackend):
    """本地目录模拟云端（测试 / 自建目录同步）"""
    def __init__(self, root):
        self.root = os.path.abspath(root)
        os.makedirs(self.root, exist_ok=True)
    def _p(self, key):
        return os.path.join(self.root, key.replace("/", os.sep))
    def meta(self, key):
        p = self._p(key)
        if not os.path.exists(p):
            return None
        st = os.stat(p)
        return {"size": st.st_size, "mtime": st.st_mtime, "etag": hashfile(p)}
    def upload(self, local_path, key):
        p = self._p(key)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(local_path, "rb") as f: data = f.read()
        with open(p, "wb") as f: f.write(data)
        return self.meta(key)
    def download(self, key, local_path):
        shutil = __import__("shutil")
        shutil.copyfile(self._p(key), local_path)
        return self.meta(key)
    def list(self, prefix=""):
        out = []
        for root, _, files in os.walk(self.root):
            for fn in files:
                key = os.path.relpath(os.path.join(root, fn), self.root).replace(os.sep, "/")
                if key.startswith(prefix):
                    out.append((key, self.meta(key)))
        return out

class WebDAVBackend(StorageBackend):
    """WebDAV 后端（webdav4 客户端）"""
    def __init__(self, base_url, user=None, pwd=None, root=""):
        from webdav4.client import Client
        self.client = Client(base_url, auth=(user, pwd) if user else None)
        self.root = root.strip("/")
    def _key(self, key):
        return f"{self.root}/{key}" if self.root else key
    def meta(self, key):
        k = self._key(key)
        try:
            infos = net_retry(lambda: self.client.ls(os.path.dirname(k) or "/", detail=True),
                              f"枚举 {k}")
        except RuntimeError:
            raise                       # 重试耗尽：不许伪装成「不存在」（会误导成 push 覆盖）
        except Exception:
            return None                 # 404/不存在
        for i in infos:
            if i["name"] == os.path.basename(k) or i["name"].rstrip("/") == k.rstrip("/"):
                return {"size": i.get("size", 0), "mtime": i.get("last_modified", 0),
                        "etag": i.get("etag", "")}
        return None
    def upload(self, local_path, key):
        k = self._key(key)
        d = os.path.dirname(k)
        if d and not net_retry(lambda: self.client.exists(d), f"探测 {d}"):
            net_retry(lambda: self.client.mkdir(d), f"建目录 {d}")
        net_retry(lambda: self.client.upload_file(local_path, k, overwrite=True), f"上传 {k}")
        return self.meta(key)
    def download(self, key, local_path):
        net_retry(lambda: self.client.download_file(self._key(key), local_path), f"下载 {key}")
        return self.meta(key)
    def list(self, prefix=""):
        k = self._key(prefix)
        try:
            infos = net_retry(lambda: self.client.ls(k, detail=True), f"枚举 {k}")
        except RuntimeError:
            raise                       # 重试耗尽不许伪装成「空前缀」
        except Exception:
            return []                   # 404/目录不存在 = 空
        return [(i["name"], {"size": i.get("size", 0), "mtime": i.get("last_modified", 0), "etag": i.get("etag", "")})
                for i in infos]

class S3Backend(StorageBackend):
    """S3 兼容后端（boto3：阿里 OSS / 腾讯 COS / Cloudflare R2 / MinIO）"""
    def __init__(self, bucket, endpoint, access_key, secret_key, region="auto", prefix=""):
        import boto3
        self.client = boto3.client("s3", endpoint_url=endpoint,
                                   aws_access_key_id=access_key, aws_secret_access_key=secret_key,
                                   region_name=region)
        self.bucket = bucket
        self.prefix = prefix.strip("/")
    def _key(self, key):
        return f"{self.prefix}/{key}" if self.prefix else key
    def meta(self, key):
        try:
            r = net_retry(lambda: self.client.head_object(Bucket=self.bucket, Key=self._key(key)),
                          f"探测 {key}")
            return {"size": r["ContentLength"], "mtime": r["LastModified"].timestamp(), "etag": r.get("ETag", "")}
        except RuntimeError:
            raise                       # 重试耗尽：不许伪装成「不存在」
        except Exception:
            return None
    def upload(self, local_path, key):
        net_retry(lambda: self.client.upload_file(local_path, self.bucket, self._key(key)),
                  f"上传 {key}")
        return self.meta(key)
    def download(self, key, local_path):
        net_retry(lambda: self.client.download_file(self.bucket, self._key(key), local_path),
                  f"下载 {key}")
        return self.meta(key)
    def list(self, prefix=""):
        try:
            r = net_retry(lambda: self.client.list_objects_v2(Bucket=self.bucket, Prefix=self._key(prefix)),
                          f"枚举 {prefix}")
        except RuntimeError:
            raise                       # 重试耗尽不许伪装成「空前缀」
        except Exception:
            return []
        return [(o["Key"], {"size": o["Size"], "mtime": o["LastModified"].timestamp(), "etag": o.get("ETag", "")})
                for o in r.get("Contents", [])]

def hashfile(path, chunk=1 << 20):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            b = f.read(chunk)
            if not b: break
            h.update(b)
    return h.hexdigest()

class Syncer:
    """双向同步器：管理本地↔云端一对 key 的状态，做 push/pull 决策"""
    def __init__(self, backend, state_file=None, local_dir=None):
        self.backend = backend
        if state_file is None:
            if local_dir:
                state_file = os.path.join(local_dir, ".sync-state.json")
            else:
                state_file = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".sync-state.json")
        self.state_file = state_file
        self.state = self._load_state()

    # 真实 scope-recall 库（schema 1110）的有效记忆判定列。交付包原用
    # claims.claim_state='active'，该列在真实库中**不存在**（claim_state 只在桥自己的
    # 简化镜像里有），异常被吞成 n=0 → 真实库被误判为「空库」→ _decision 永远走 pull，
    # 云端稍旧就整库覆盖本地活库。真实库用 read_blocked / suppressed 两个标志位。
    _ACTIVE_SQL = "SELECT COUNT(*) FROM claims WHERE read_blocked=0 AND suppressed=0"

    def _is_empty_db(self, path):
        """空库保护：本地 sqlite 无任何有效记忆时视为空库
        （防止新初始化的空库以'mtime 最新'为由 push 覆盖云端已有记忆）

        修复（交付包 bug 2）：先按真实库列名（read_blocked/suppressed）判定；只有在该
        查询同样失败时才退回桥镜像的 claim_state 口径。**判定失败一律返回 False
        （视为非空）**，宁可少推也绝不让空库保护把有数据的库判成空。"""
        if not os.path.exists(path) or os.path.getsize(path) < 4096:
            return True
        import sqlite3 as _s
        con = _s.connect(f"file:{path}?mode=ro", uri=True)
        try:
            for sql in (self._ACTIVE_SQL,
                        "SELECT COUNT(*) FROM claims WHERE claim_state='active'"):
                try:
                    return con.execute(sql).fetchone()[0] == 0
                except _s.OperationalError:
                    continue
            # 两种口径都不适用（未知 schema）：无法证明是空库 → 保守视为非空
            return False
        finally:
            con.close()

    def _load_state(self):
        if os.path.exists(self.state_file):
            try:
                return json.load(open(self.state_file))
            except Exception:
                pass
        return {}

    def _save_state(self):
        with open(self.state_file, "w") as f:
            json.dump(self.state, f, indent=1)

    def _local_meta(self, path):
        if not os.path.exists(path):
            return None
        st = os.stat(path)
        return {"size": st.st_size, "mtime": st.st_mtime, "etag": hashfile(path)}

    CLOUD_STATE_KEY = ".sync-state.json"  # 云端状态文件（内容 hash + 时间戳，mtime 不可靠后端的省流量核心）

    def _read_cloud_state(self) -> dict:
        raw = self.backend.get_bytes(self.CLOUD_STATE_KEY)
        if not raw:
            return {}
        try:
            return json.loads(raw.decode("utf-8"))
        except Exception:
            return {}

    def _write_cloud_state(self, cloud_state: dict) -> None:
        self.backend.put_bytes(self.CLOUD_STATE_KEY, json.dumps(cloud_state).encode("utf-8"))

    def _decision(self, local_path, key):
        """决策：None=不动, 'push'=本地推云端, 'pull'=云端拉本地。
        基于云端状态文件（hash+时间戳），对 mtime/size 不可靠的 WebDAV 后端同样省流量。"""
        lm = self._local_meta(local_path)
        rm = self.backend.meta(key)
        prev = self.state.get(key)
        if lm is None:
            return None  # 本地删除不传播（保守）
        cloud_state = self._read_cloud_state()
        cs = cloud_state.get(key)
        # 一致短路：云端状态 hash == 本地 hash → 不动（即使远端元数据 mtime 无效）
        if cs and cs.get("hash") == lm["etag"]:
            return None
        if rm is None and cs is None:
            return "push"  # 云端无此文件 → 上传
        # 空库保护：本地空库而云端有数据 → 强制拉取，绝不覆盖
        if self._is_empty_db(local_path):
            if (rm and rm.get("size", 0) > 4096) or (cs and prev is None):
                return "pull"
        if prev is None:
            # 首次接触且云端已有 → 保守拉取（不覆盖云端）；否则上传
            return "pull" if (rm is not None or cs is not None) else "push"
        local_changed = lm["etag"] != prev.get("hash")          # 本地自上次同步后改过
        remote_changed = (cs or {}).get("updated_ts", 0) > (prev.get("remote_ts") or 0)  # 云端自上次拉取后改过
        if remote_changed and not local_changed:
            return "pull"
        if local_changed and not remote_changed:
            return "push"
        # 双方都改过或都无法判定 → 云端胜（保守）
        return "pull" if remote_changed or (cs is not None) else "push"

    def sync_key(self, local_path, key):
        """同步单个 key，返回 ('push'|'pull'|'none', 信息)"""
        d = self._decision(local_path, key)
        now_ts = time.time()
        if d == "push":
            meta = self.backend.upload(local_path, key)
            self.state[key] = {"hash": hashfile(local_path), "remote_ts": now_ts}
            self._save_state()
            cs = self._read_cloud_state()
            cs[key] = {"hash": hashfile(local_path), "updated_ts": now_ts}
            self._write_cloud_state(cs)
            return ("push", f"本地更新 → 已上传云端 ({key})")
        if d == "pull":
            os.makedirs(os.path.dirname(os.path.abspath(local_path)), exist_ok=True)
            meta = self.backend.download(key, local_path)
            cs = self._read_cloud_state()
            self.state[key] = {"hash": hashfile(local_path), "remote_ts": (cs.get(key) or {}).get("updated_ts", now_ts)}
            self._save_state()
            return ("pull", f"云端更新 → 已下载到本地 ({key})")
        return ("none", f"无变化 ({key})")

    def sync_all(self, local_dir, remote_prefix=""):
        """同步目录下所有文件（本地文件名 = 云端 key；忽略本引擎状态文件）"""
        results = []
        for fn in sorted(os.listdir(local_dir)):
            if fn == ".sync-state.json" or fn.endswith(".sync-state.json"):
                continue
            p = os.path.join(local_dir, fn)
            if os.path.isfile(p):
                key = f"{remote_prefix}/{fn}" if remote_prefix else fn
                results.append(self.sync_key(p, key))
        return results

    def watch(self, local_dir, remote_prefix="", interval=5.0, once=False):
        """变更监视：轮询本地目录与云端，双向自动同步。
        单轮任何异常（网络抖动/超时/5xx）只跳过本轮，不中断 watch。"""
        print(f"[watch] 本地={local_dir} 云端={type(self.backend).__name__} 轮询={interval}s (Ctrl-C 停止)")
        while True:
            try:
                for act, msg in self.sync_all(local_dir, remote_prefix):
                    if act != "none":
                        print(f"  [{act}] {msg}")
                # 云端→本地：拉取云端新增（本地不存在的 key）
                for key, meta in self.backend.list(remote_prefix):
                    if key == self.CLOUD_STATE_KEY or key.endswith("/" + self.CLOUD_STATE_KEY):
                        continue
                    local_path = os.path.join(local_dir, os.path.basename(key))
                    if not os.path.exists(local_path):
                        act, msg = self.sync_key(local_path, key)
                        if act != "none":
                            print(f"  [{act}] {msg}")
            except Exception as e:
                print(f"  [!] 本轮异常已跳过（继续 watch）: {type(e).__name__}: {e}")
            if once:
                return
            time.sleep(interval)


def make_backend(kind, **kw):
    if kind == "local":
        return LocalDirBackend(kw["root"])
    if kind == "webdav":
        return WebDAVBackend(kw["url"], kw.get("user"), kw.get("pwd"), kw.get("root", ""))
    if kind == "s3":
        return S3Backend(kw["bucket"], kw["endpoint"], kw["access_key"], kw["secret_key"], kw.get("region", "auto"), kw.get("prefix", ""))
    raise ValueError(f"未知后端: {kind}")

if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="记忆文件双向同步引擎")
    ap.add_argument("--backend", default="local", choices=["local", "webdav", "s3"])
    ap.add_argument("--local-dir", required=True)
    ap.add_argument("--remote-key", default="memory.sqlite3")
    ap.add_argument("--url", help="WebDAV URL")
    # 修复（交付包 bug 1）：原为 `add_argument("--user", "--pwd", dest="user")` 紧跟
    # `add_argument("--pwd")`，--pwd 被重复注册，argparse 在**构建解析器时**就抛
    # ArgumentError: conflicting option string: --pwd —— 连 `--help` 都跑不起来。
    # 文档 §6.4 声称已修，但交付代码未修。此处按原意恢复为两个独立开关。
    ap.add_argument("--user", help="WebDAV 用户名")
    ap.add_argument("--pwd", help="WebDAV 密码（坚果云须用应用密码）")
    ap.add_argument("--root", default="")
    ap.add_argument("--bucket", help="S3 bucket")
    ap.add_argument("--endpoint", help="S3 endpoint")
    ap.add_argument("--access-key")
    ap.add_argument("--secret-key")
    ap.add_argument("--once", action="store_true", help="只同步一次")
    ap.add_argument("--watch", action="store_true", help="持续监视")
    ap.add_argument("--interval", type=float, default=5.0)
    a = ap.parse_args()

    kw = {"root": a.local_dir}
    if a.backend == "webdav":
        kw = {"url": a.url, "user": a.user, "pwd": a.pwd, "root": a.root}
    if a.backend == "s3":
        kw = {"bucket": a.bucket, "endpoint": a.endpoint, "access_key": a.access_key, "secret_key": a.secret_key, "prefix": a.root}
    bk = make_backend(a.backend, **kw)
    s = Syncer(bk)
    if a.watch:
        s.watch(a.local_dir, a.remote_key, interval=a.interval)
    else:
        for act, msg in s.sync_all(a.local_dir, a.remote_key):
            print(f"[{act}] {msg}")
