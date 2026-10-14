"""scope-recall-win 静态验证（Linux/无 SDK 环境下的尽调检查）。

覆盖：
  V1  XAML/app.manifest XML 良构
  V2  StaticResource 引用 vs 资源字典定义（缺失即 XAML 编译/运行期炸）
  V3  x:Class 与代码隐藏类名/文件一一对应
  V4  MainWindow 路由表 ↔ 导航项 ↔ 视图文件三者一致（数量由代码推导，不写死）
  V5  C# 括号/方括号/圆括号配平 + namespace 一致
  V6  installer/ 安装器产物存在且有效（app.ico / setup.iss）
  V7  上游口径残留扫描（mutate CLI / create 变更 / deleted 状态 等禁用表述）
  V8  维护 CLI 参数装配（CLI 调用串收口到 Infrastructure/MaintenanceCli.cs）
  V9  Python 运行时脚本（remote/ bridge/ installer 发布产物）全部 py_compile 通过
  V10 第三方 API 签名比对 + 禁误用参数名（webdav4 Client(url= 事故的守门员）
  V11 禁空 catch{}（存储层 12 处静默返回空列表事故的守门员；文档注释里的字样豁免）
"""
from __future__ import annotations

import inspect
import io
import os
import py_compile
import re
import sys
import tempfile
import tokenize
import xml.etree.ElementTree as ET
from pathlib import Path

# CI 环境（windows-latest 默认控制台为 cp1252）下中文输出会 UnicodeEncodeError，
# 强制 stdout/stderr 使用 UTF-8，保证脚本在任何 locale 下都能跑完。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except Exception:
        pass

ROOT = Path(__file__).resolve().parent
errors: list[str] = []
ok: list[str] = []


def source_files(pattern: str) -> list[Path]:
    """源码文件（排除 obj/bin/.git 等构建产物目录）。"""
    skip = {"obj", "bin", ".git", "__pycache__", "AppPackages", "publish"}
    return [
        p for p in ROOT.rglob(pattern)
        if p.is_file() and not (skip & set(p.relative_to(ROOT).parts))
    ]


def fail(msg: str) -> None:
    errors.append(msg)


# ---------- V1 XML 良构 ----------
xml_files = sorted(source_files("*.xaml")) + [
    ROOT / "app.manifest",
]
for f in xml_files:
    try:
        ET.parse(f)
        ok.append(f"V1 XML 良构: {f.relative_to(ROOT)}")
    except ET.ParseError as e:
        fail(f"V1 XML 解析失败: {f.relative_to(ROOT)}: {e}")

# ---------- V2 StaticResource 引用 ----------
defined: set[str] = set()
for f in source_files("*.xaml"):
    if f.parent.name == "Theme":
        defined |= set(re.findall(r'x:Key="([^"]+)"', f.read_text(encoding="utf-8")))
# 内置 Fluent 样式（无需自定义字典）
builtin = {"PageTitleTextStyle", "PageDescTextStyle", "SectionHeaderTextStyle", "MonoTextStyle"}
referenced: dict[str, set[str]] = {}
for f in list(source_files("*.xaml")):
    if f.parent.name == "Theme":
        continue
    keys = set(re.findall(r'StaticResource (\w+)', f.read_text(encoding="utf-8")))
    for k in keys:
        if k not in defined and k not in builtin:
            referenced.setdefault(f.name, set()).add(k)
for fname, keys in referenced.items():
    for k in keys:
        fail(f"V2 {fname} 引用未定义资源: {k}")
ok.append(f"V2 StaticResource: 自定义字典 {len(defined)} 项，视图引用全部可解析" if not referenced else "")

# ---------- V3 x:Class ↔ 代码隐藏 ----------
for f in sorted(source_files("*.xaml")):
    if f.parent.name == "Theme":
        continue
    text = f.read_text(encoding="utf-8")
    m = re.search(r'x:Class="([\w.]+)"', text)
    if not m:
        fail(f"V3 {f.name} 缺 x:Class")
        continue
    cls = m.group(1)
    cs = f.with_suffix(".xaml.cs")
    if not cs.exists():
        fail(f"V3 {f.name} 缺代码隐藏 {cs.name}")
        continue
    cs_text = cs.read_text(encoding="utf-8")
    simple = cls.split(".")[-1]
    if f"class {simple}" not in cs_text:
        fail(f"V3 {f.name} x:Class={cls} 与 {cs.name} 类名不匹配")
    ns = cls.rsplit(".", 1)[0]
    if f"namespace {ns}" not in cs_text:
        fail(f"V3 {cs.name} namespace 应为 {ns}")
ok.append("V3 x:Class ↔ 代码隐藏: 全部匹配")

# ---------- V4 路由表 ↔ 导航 ↔ 视图 ----------
# 导航为代码动态生成（unpackaged 形态自建导航）：NavGroups 元组第一项即 Tag。
mw = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")
route_tags = set(re.findall(r'\["([\w-]+)"\]\s*=\s*typeof\(Views\.(\w+)\)', mw))
nav_tags = set(re.findall(r'\(\s*"([\w-]+)"\s*,\s*"[^"]*"\s*,\s*"\\u[0-9A-Fa-f]{4}"', mw))
view_files = {p.stem for p in (ROOT / "Views").glob("*.xaml")}
for tag, view in route_tags:
    if view not in view_files:
        fail(f"V4 路由 {tag} → Views.{view} 文件缺失")
    if tag not in nav_tags:
        fail(f"V4 路由 {tag} 无对应导航项")
for tag in nav_tags:
    if tag not in {t for t, _ in route_tags}:
        fail(f"V4 导航项 {tag} 无路由")
ok.append(f"V4 路由/导航: {len(route_tags)} 路由 ↔ {len(nav_tags)} 导航项，视图文件 {len(view_files)} 个")

# ---------- V5 C# 括号配平 ----------
for f in sorted(source_files("*.cs")):
    text = f.read_text(encoding="utf-8")
    # 去掉字符字面量 → 字符串 → 注释后配平（顺序：先字符后字符串，防 '"' 误判）
    t = re.sub(r"'(?:\\.|[^'\\])'", "''", text)
    t = re.sub(r'"(?:\\.|[^"\\])*"', '""', t)
    t = re.sub(r"//[^\n]*", "", t)
    t = re.sub(r"/\*.*?\*/", "", t, flags=re.S)
    for a, b in (("{", "}"), ("(", ")"), ("[", "]")):
        if t.count(a) != t.count(b):
            fail(f"V5 {f.relative_to(ROOT)}: {a}{b} 不配平 ({t.count(a)}/{t.count(b)})")
ok.append("V5 C# 括号配平: 全部通过")

# ---------- V5b XAML 属性值不得含裸 < / & / " ----------
ENTITY = re.compile(r"&(?:#x[0-9A-Fa-f]+|#\d+|[A-Za-z]+);")
for f in sorted(source_files("*.xaml")):
    text = f.read_text(encoding="utf-8")
    for m in re.finditer(r'(\w[\w:.-]*)="([^"]*)"', text):
        val = m.group(2)
        line = text[: m.start()].count("\n") + 1
        if "<" in val:
            fail(f"V5b {f.relative_to(ROOT)} L{line} 属性 {m.group(1)} 含裸 '<'")
        for amp in re.finditer(r"&", val):
            tail = val[amp.start():]
            if not ENTITY.match(tail):
                fail(f"V5b {f.relative_to(ROOT)} L{line} 属性 {m.group(1)} 含未转义 '&'")
ok.append("V5b XAML 属性值: 无裸 < / 未转义 &")

# ---------- V6 安装器产物 ----------
ico = ROOT / "installer" / "app.ico"
iss = ROOT / "installer" / "setup.iss"
isl = ROOT / "installer" / "ChineseSimplified.isl"
for f in (ico, iss, isl):
    if not f.exists():
        fail(f"V6 缺安装器文件 {f.relative_to(ROOT)}")
if iss.exists():
    iss_text = iss.read_text(encoding="utf-8")
    # 不写死版本号：每次发版都要改这里是个维护陷阱（漏改就误报）。
    # 只校验「有版本号」+「安装包名与 MyAppVersion 一致」+ 免管理员。
    if not re.search(r'#define\s+MyAppVersion\s+"\d+\.\d+\.\d+"', iss_text):
        fail("V6 setup.iss 缺 MyAppVersion（应为 x.y.z）")
    version_match = re.search(r'#define\s+MyAppVersion\s+"([\d.]+)"', iss_text)
    if version_match:
        expected = f"ScopeRecallWin-Setup-{version_match.group(1)}"
        if expected not in iss_text:
            fail(f"V6 setup.iss 的 OutputBaseFilename 与 MyAppVersion 不一致（应为 {expected}）")
    if "PrivilegesRequired=lowest" not in iss_text:
        fail("V6 setup.iss 缺 PrivilegesRequired=lowest（per-user 免管理员安装）")
ok.append("V6 安装器: app.ico / setup.iss / ChineseSimplified.isl 齐备")

# ---------- V7 上游口径残留 ----------
banned = [
    (r"MutateOperation\b", "MutateOperation 枚举已废弃（上游无 mutate CLI）"),
    (r"maintenance\.cli[^\n]{0,40}mutate", "官方 mutate CLI 不存在（模块路径本身合法，仅禁 mutate 子命令口径）"),
    (r"revise\s*/\s*create", "旧操作枚举 revise/create/forget 已删除"),
    (r"suppressed/deleted", "deleted 非 claim 状态"),
]
for f in list(source_files("*.cs")) + list(source_files("*.xaml")) + [ROOT / "README.md"]:
    text = f.read_text(encoding="utf-8")
    for pat, why in banned:
        if re.search(pat, text):
            fail(f"V7 {f.relative_to(ROOT)}: {why} (/{pat}/)")
ok.append("V7 上游口径残留扫描: 通过")

# ---------- V8 维护 CLI 参数装配 ----------
# 上游 3.10.2 的维护 CLI 参数面曾经三处踩坑（全部为 exit 2 / 假成功）：
#   1) 绑定 store 的六个子命令 --config 是 argparse required=True，漏传即 exit 2 只打 usage
#      （曾表现为「重试失败（退出码 2）：usage: … retry-failures …」）；
#   2) 这些命令默认预演，不带 --apply 时 exit 0 但 applied=false，属假成功；
#   3) 共享库命令的 --root 是「共享库目录」本身（<记忆库根>\shared），传记忆库根即 exit 2；
#      客户端宿主 attach 还必须给 --grants-like/--capture-like（且只能指向已接入的 Hermes 成员），
#      而 --host 只有 hermes/codex/claude-code/workbuddy/dsh（workbuddy-ai 是成员标识，不是宿主）。
# 因此：**除收口文件 Infrastructure/MaintenanceCli.cs 外，任何 .cs 都不得出现 CLI 调用串**。
HANDWRITTEN_ALLOWED = {"MaintenanceCli.cs"}
cli_literal = re.compile(r"maintenance\.cli")
for f in sorted(source_files("*.cs")):
    if f.name in HANDWRITTEN_ALLOWED:
        continue
    text = f.read_text(encoding="utf-8")
    # 去掉注释再扫：文档里引用模块路径不算违规
    text = re.sub(r"//[^\n]*", "", text)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    if cli_literal.search(text):
        line = text[: cli_literal.search(text).start()].count("\n") + 1
        fail(
            f"V8 {f.relative_to(ROOT)}: 手写了官方 CLI 调用（约第 {line} 行）—— "
            f"必须经 Infrastructure/MaintenanceCli 的 Build/AttachArgs/BackupArgs/DoctorArgs/… 装配，"
            f"否则极易漏 --config/--apply、把记忆库根当共享库根传、或漏掉 attach 的授权参照"
        )
ok.append("V8 维护 CLI 参数装配: CLI 调用串已全部收口到 Infrastructure/MaintenanceCli.cs")

# ---------- V9 Python 运行时脚本语法门禁 ----------
# 背景：remote/ 与 bridge/ 曾没有任何门禁覆盖（V1~V8 全盯 C#/XAML/安装器），
# 于是 webdav4 Client(url=...) 这种「一行跑不通全部」的参数名错误能在仓库里存活、
# 还随发布产物出厂——CI 全绿而 webdav 后端 100% 不可用。
# 本项：remote/、bridge/ 及 installer 发布产物里的全部 .py 必须 py_compile 通过；
# 一个文件都没有 = 门禁形同虚设，同样算失败。
_py_dirs = [ROOT / "remote", ROOT / "bridge"]
_pub_run = ROOT / "installer" / "publish" / "run"
for _extra in ("remote", "bridge"):
    _d = _pub_run / _extra
    if _d.is_dir():
        _py_dirs.append(_d)
py_files: list[Path] = []
for _d in _py_dirs:
    if _d.is_dir():
        py_files += sorted(p for p in _d.glob("*.py") if p.is_file())
if not py_files:
    fail("V9 未找到任何 Python 运行时脚本（remote/ 与 bridge/ 均不在仓库内）——门禁形同虚设")
else:
    _bad = 0
    with tempfile.TemporaryDirectory() as _td:
        for f in py_files:
            try:
                py_compile.compile(str(f), cfile=os.path.join(_td, f.stem + ".pyc"), doraise=True)
            except py_compile.PyCompileError as e:
                fail(f"V9 Python 语法错误: {f.relative_to(ROOT)}: {e}")
                _bad += 1
    if not _bad:
        ok.append(f"V9 Python 语法: {len(py_files)} 个运行时脚本全部编译通过")

# ---------- V10 第三方 API 签名比对 + 误用参数名扫描 ----------
# webdav4.client.Client 的首参是 base_url，没有 url。复制粘贴改坏的后果是
# 「任何 webdav 调用前即 TypeError」——这类错误只有签名比对能挡住。
try:
    from webdav4.client import Client as _WdClient  # pip install webdav4（CI 未装则跳过比对）

    _params = inspect.signature(_WdClient.__init__).parameters
    if "base_url" not in _params:
        fail("V10 webdav4.client.Client.__init__ 无 base_url 参数——版本不兼容，"
             "remote/ 的 WebDAVBackend 构造方式须同步修订")
    else:
        ok.append("V10 webdav4 Client.__init__ 签名比对: 含 base_url")
except ImportError:
    ok.append("V10 webdav4 未安装，跳过签名比对（静态扫描仍生效）")

# 全仓 Python（含发布产物）禁写 Client(url=...)。注释/文档字符串里的提及不算违规
# （与 V8 同思路：先掩掉注释与字符串字面量，保留行号，只扫真实代码）。
_misuse = re.compile(r"Client\s*\(\s*url\s*=")


def _mask_strings_comments(text: str) -> str:
    """把注释与字符串字面量掩成空格，保留行结构（行号不变）。

    Python 3.12+ 的 f-string 按 PEP 701 拆成 FSTRING_START/MIDDLE/END 三个 token，
    也要一并掩掉，否则告警文案自身会误触门禁。
    """
    masked_types = {tokenize.COMMENT, tokenize.STRING}
    for name in ("FSTRING_START", "FSTRING_MIDDLE", "FSTRING_END"):
        t = getattr(tokenize, name, None)
        if t is not None:
            masked_types.add(t)
    rows = [list(ln) for ln in text.splitlines()]
    try:
        for tok in tokenize.generate_tokens(io.StringIO(text).readline):
            if tok.type not in masked_types:
                continue
            (sr, sc), (er, ec) = tok.start, tok.end
            for r in range(sr, er + 1):
                row = rows[r - 1] if r - 1 < len(rows) else []
                c0 = sc if r == sr else 0
                c1 = ec if r == er else len(row)
                for c in range(c0, min(c1, len(row))):
                    row[c] = " "
    except (tokenize.TokenError, IndentationError, SyntaxError):
        return text  # 本身就有语法问题 → 原样扫（V9 会另行报错），宁可误报
    return "\n".join("".join(r) for r in rows)


_scan = list(source_files("*.py")) + py_files
_seen: set[Path] = set()
_hits = 0
for f in _scan:
    if f in _seen:
        continue
    _seen.add(f)
    try:
        text = f.read_text(encoding="utf-8", errors="replace")
    except OSError:
        continue
    masked = _mask_strings_comments(text)
    for m in _misuse.finditer(masked):
        line = masked[: m.start()].count("\n") + 1
        fail(f"V10 {f.relative_to(ROOT)}:{line} Client(url=...) 误用——"
             f"webdav4 的 Client 首参是 base_url（写 url= 会在调用前抛 TypeError）")
        _hits += 1
if not _hits:
    ok.append(f"V10 误用参数名扫描: {len(_seen)} 个 Python 文件无 Client(url= 形态")

# ---------- V11 禁空 catch{} ----------
# 背景（评审 P0）：StorageService 曾有 12 处 `catch { } return rows;`——库读失败时
# 静默返回空列表，UI 显示「0 条记录」，用户无法区分「没数据」和「读不了」。
# 语义测试（tests/ScopeRecallWin.Tests）管行为，这道门禁管复发：一行正则拦住。
_empty_catch = re.compile(r"catch\s*\{\s*\}")
_catch_hits = 0
for f in sorted(source_files("*.cs")):
    for n, line in enumerate(f.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        if not _empty_catch.search(line):
            continue
        # 豁免：文档注释/XML doc 里描述这个模式本身的字样（如 `<c>catch { } return rows;</c>`）
        if "///" in line or "<c>" in line or "<!--" in line:
            continue
        fail(f"V11 {f.relative_to(ROOT)}:{n} 空 catch{{}} —— 异常不许静默："
             f"至少 AppLog.Warn(...) 落痕；数据读失败还应触发错误态/事件")
        _catch_hits += 1
if not _catch_hits:
    ok.append("V11 空 catch 扫描: 无（异常均有日志或错误态）")


# ---------- 报告 ----------
print("== 通过项 ==")
for line in ok:
    if line:
        print(" ✓", line)
if errors:
    print("\n== 失败项 ==")
    for e in errors:
        print(" ✗", e)
    sys.exit(1)
print("\n全部静态检查通过。")
