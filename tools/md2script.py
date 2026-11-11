"""把 docs/video-script.md 转成便于阅读/打印的 HTML。

用法：python tools/md2script.py
输出：docs/video-script.html

只覆盖本文用到的 Markdown 子集：标题、表格、列表（含任务项）、引用块、
代码块、分割线、段落，以及行内的 **粗体**、`代码`、[链接](url)。
样式内联、无外部依赖，深色文字 + 浅底，适合拍摄时在副屏/手机上翻阅。
"""
from __future__ import annotations

import html
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "docs" / "video-script.md"
OUT = ROOT / "docs" / "video-script.html"


def inline(text: str) -> str:
    t = html.escape(text, quote=False)
    codes: list[str] = []

    def stash(m: re.Match) -> str:
        codes.append(m.group(1))
        return f"\x00{len(codes) - 1}\x00"

    t = re.sub(r"`([^`]+)`", stash, t)
    t = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", t)
    t = re.sub(r"\[([^\]]+)\]\((https?://[^)]+)\)", r'<a href="\2">\1</a>', t)
    t = re.sub(r"&lt;(https?://[^&]+?)&gt;", r'<a href="\1">\1</a>', t)

    def unstash(m: re.Match) -> str:
        return f"<code>{codes[int(m.group(1))]}</code>"

    return re.sub(r"\x00(\d+)\x00", unstash, t)


def split_row(line: str) -> list[str]:
    return [c.strip() for c in line.strip().strip("|").split("|")]


def cell(text: str) -> str:
    """时间码、纯时长一类短单元格不允许折行，否则 00:08–0:16 会被拆成两行。"""
    body = inline(text)
    if re.fullmatch(r"[\d:．.\s–—\-~约]+s?", text.strip()):
        return f'<span class="nw">{body}</span>'
    return body


def convert(md: str) -> str:
    lines = md.split("\n")
    out: list[str] = []
    i = 0
    n = len(lines)

    while i < n:
        line = lines[i]
        s = line.strip()

        if not s:
            i += 1
            continue

        if s.startswith("```"):
            i += 1
            buf = []
            while i < n and not lines[i].strip().startswith("```"):
                buf.append(lines[i])
                i += 1
            i += 1
            out.append("<pre><code>" + html.escape("\n".join(buf)) + "</code></pre>")
            continue

        if re.fullmatch(r"-{3,}", s):
            out.append("<hr>")
            i += 1
            continue

        m = re.match(r"^(#{1,4})\s+(.*)$", s)
        if m:
            lvl = len(m.group(1))
            out.append(f"<h{lvl}>{inline(m.group(2))}</h{lvl}>")
            i += 1
            continue

        # 表格：表头 + 分隔行
        if s.startswith("|") and i + 1 < n and re.match(r"^\|[\s:|-]+\|$", lines[i + 1].strip()):
            head = split_row(s)
            i += 2
            rows = []
            while i < n and lines[i].strip().startswith("|"):
                rows.append(split_row(lines[i].strip()))
                i += 1
            th = "".join(f"<th>{cell(c)}</th>" for c in head)
            body = "".join(
                "<tr>" + "".join(f"<td>{cell(c)}</td>" for c in r) + "</tr>" for r in rows
            )
            out.append(f"<table><thead><tr>{th}</tr></thead><tbody>{body}</tbody></table>")
            continue

        if s.startswith(">"):
            buf = []
            while i < n and lines[i].strip().startswith(">"):
                buf.append(re.sub(r"^>\s?", "", lines[i].strip()))
                i += 1
            out.append("<blockquote>" + "".join(
                f"<p>{inline(b)}</p>" for b in buf if b.strip()
            ) + "</blockquote>")
            continue

        if re.match(r"^[-*]\s+", s):
            items = []
            while i < n and re.match(r"^[-*]\s+", lines[i].strip()):
                item = re.sub(r"^[-*]\s+", "", lines[i].strip())
                if item.startswith("[ ]"):
                    items.append(f'<li class="task">{inline(item[3:].strip())}</li>')
                elif item.startswith("[x]"):
                    items.append(f'<li class="task done">{inline(item[3:].strip())}</li>')
                else:
                    items.append(f"<li>{inline(item)}</li>")
                i += 1
            out.append("<ul>" + "".join(items) + "</ul>")
            continue

        if re.match(r"^\d+\.\s+", s):
            items = []
            while i < n and re.match(r"^\d+\.\s+", lines[i].strip()):
                items.append(f"<li>{inline(re.sub(r'^\d+\.\s+', '', lines[i].strip()))}</li>")
                i += 1
            out.append("<ol>" + "".join(items) + "</ol>")
            continue

        out.append(f"<p>{inline(s)}</p>")
        i += 1

    return "\n".join(out)


CSS = """
:root{--ink:#1c2530;--dim:#5b6b7c;--line:#e2e8f0;--blue:#1f5fbf;--bg:#f5f7fa;--card:#fff}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);
 font:16px/1.75 "Microsoft YaHei","PingFang SC","Segoe UI",sans-serif}
.wrap{max-width:980px;margin:0 auto;padding:36px 32px 80px;background:var(--card);
 box-shadow:0 1px 24px rgba(16,32,52,.08);min-height:100vh}
h1{font-size:28px;margin:0 0 22px;padding-bottom:14px;border-bottom:3px solid var(--blue)}
h2{font-size:21px;margin:38px 0 14px;padding-left:12px;border-left:5px solid var(--blue)}
h3{font-size:17px;margin:26px 0 10px;color:var(--blue)}
p{margin:10px 0}
strong{color:#12263f}
code{background:#eef2f7;border:1px solid #dfe6ef;border-radius:4px;padding:1px 5px;
 font:13px/1.6 Consolas,"Courier New",monospace;color:#134a8e}
pre{background:#101a26;color:#dbe7f3;border-radius:8px;padding:14px 16px;overflow:auto;
 font:13px/1.7 Consolas,"Courier New",monospace}
pre code{background:none;border:0;color:inherit;padding:0}
table{width:100%;border-collapse:collapse;margin:16px 0;font-size:13.5px}
th{background:#eef4fb;color:#274b7a;text-align:left;padding:8px 10px;border-bottom:2px solid #d4e2f2;white-space:nowrap}
.nw{white-space:nowrap}
td{padding:8px 10px;border-bottom:1px solid var(--line);vertical-align:top}
tr:nth-child(even) td{background:#fafcfe}
blockquote{margin:14px 0;padding:12px 16px;background:#f0f6fd;border-left:4px solid #6ea8e8;
 border-radius:0 6px 6px 0;color:#2d4a6b}
blockquote p{margin:6px 0}
ul,ol{margin:10px 0 10px 22px;padding:0}
li{margin:5px 0}
li.task{list-style:none;position:relative;padding-left:24px;margin-left:-20px}
li.task::before{content:"☐";position:absolute;left:0;color:#8aa0b8;font-size:15px}
li.task.done::before{content:"☑";color:#2f9e6a}
hr{border:0;border-top:1px solid var(--line);margin:30px 0}
a{color:var(--blue);word-break:break-all}
@media print{body{background:#fff}.wrap{box-shadow:none;padding:0}pre{background:#f4f6f9;color:#1c2530}}
"""

page = f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>ScrollShot 演示视频 · 拍摄脚本与旁白</title>
<style>{CSS}</style>
</head>
<body>
<div class="wrap">
{convert(SRC.read_text(encoding="utf-8"))}
</div>
</body>
</html>
"""

OUT.write_text(page, encoding="utf-8")
print("html:", OUT, len(page), "chars")
