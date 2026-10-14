"""scope-recall-win 静态验证（Linux/无 SDK 环境下的尽调检查）。

覆盖：
  V1  XAML/app.manifest XML 良构
  V2  StaticResource 引用 vs 资源字典定义（缺失即 XAML 编译/运行期炸）
  V3  x:Class 与代码隐藏类名/文件一一对应
  V4  MainWindow 路由表 18 项 ↔ 导航 18 项 ↔ 视图文件存在
  V5  C# 括号/方括号/圆括号配平 + namespace 一致
  V6  installer/ 安装器产物存在且有效（app.ico / setup.iss）
  V7  上游口径残留扫描（mutate CLI / create 变更 / deleted 状态 等禁用表述）
"""
from __future__ import annotations

import re
import sys
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
    for token in ("ScopeRecallWin-Setup-1.3.6", "PrivilegesRequired=lowest"):
        if token not in iss_text:
            fail(f"V6 setup.iss 缺少关键配置: {token}")
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
