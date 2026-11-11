"""把 docs/article.md 转成公众号排版的可复制 HTML。

用法：python tools/md2wechat.py
输出：docs/wechat-article.html

设计约束：
- 正文栏宽 375px（手机屏宽），字号/行距按公众号阅读习惯调过；
- 单文件、样式全内联，不依赖外部 CSS/JS，草稿结尾的「配图清单」不进入正文；
- 图片走相对路径 images/xxx.png（比 base64 更容易随复制带进位图）。
"""
from __future__ import annotations

import html
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "docs" / "article.md"
OUT = ROOT / "docs" / "wechat-article.html"


def inline(text: str) -> str:
    """行内标记：先转义，再按固定顺序还原成标签。"""
    t = html.escape(text, quote=False)
    codes: list[str] = []

    def stash(m: re.Match) -> str:
        codes.append(m.group(1))
        return f"\x00{len(codes) - 1}\x00"

    t = re.sub(r"`([^`]+)`", stash, t)
    t = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", t)
    t = re.sub(r"\[([^\]]+)\]\((https?://[^)]+)\)", r'<a href="\2">\1</a>', t)
    t = re.sub(r"&lt;(https?://[^&]+?)&gt;", r'<a href="\1">\1</a>', t)
    t = re.sub(r"(?<![\w*])\*([^*\n]+)\*(?![\w*])", r"<em>\1</em>", t)

    def unstash(m: re.Match) -> str:
        return f"<code>{codes[int(m.group(1))]}</code>"

    return re.sub(r"\x00(\d+)\x00", unstash, t)


def table(rows: list[str]) -> str:
    cells = [[c.strip() for c in r.strip().strip("|").split("|")] for r in rows]
    head, body = cells[0], [r for r in cells[1:] if not re.fullmatch(r"[:\-\s|]+", "|".join(r))]
    out = [f"<table{' class=\"compact\"' if len(head) >= 5 else ''}><thead><tr>"]
    out += [f"<th>{inline(c)}</th>" for c in head]
    out.append("</tr></thead><tbody>")
    for r in body:
        out.append("<tr>" + "".join(f"<td>{inline(c)}</td>" for c in r) + "</tr>")
    out.append("</tbody></table>")
    return "".join(out)


def convert(md: str) -> str:
    lines = md.splitlines()
    body: list[str] = []
    i = 0
    while i < len(lines):
        raw = lines[i]
        line = raw.rstrip()

        if not line.strip():
            i += 1
            continue

        # 代码块
        if line.startswith("```"):
            i += 1
            buf = []
            while i < len(lines) and not lines[i].startswith("```"):
                buf.append(lines[i])
                i += 1
            i += 1
            body.append("<pre><code>" + html.escape("\n".join(buf)) + "</code></pre>")
            continue

        # 表格
        if line.lstrip().startswith("|"):
            rows = []
            while i < len(lines) and lines[i].lstrip().startswith("|"):
                rows.append(lines[i])
                i += 1
            if len(rows) >= 2:
                body.append(table(rows))
            continue

        # 分隔线
        if re.fullmatch(r"[-*_]{3,}", line.strip()):
            body.append('<div class="divider">· · ·</div>')
            i += 1
            continue

        # 标题
        m = re.match(r"^(#{1,3})\s+(.*)$", line)
        if m:
            level = len(m.group(1))
            body.append(f"<h{level}>{inline(m.group(2).strip())}</h{level}>")
            i += 1
            continue

        # 引用块
        if line.startswith(">"):
            buf = []
            while i < len(lines) and lines[i].startswith(">"):
                buf.append(lines[i].lstrip(">").strip())
                i += 1
            text = " ".join(x for x in buf if x)
            cls = ' class="note"' if text.startswith("【") else ""
            body.append(f"<blockquote{cls}>{inline(text)}</blockquote>")
            continue

        # 图片 + 图注
        m = re.match(r"^!\[([^\]]*)\]\(([^)]+)\)\s*$", line.strip())
        if m:
            alt, src = m.group(1), m.group(2)
            cap = ""
            if i + 2 < len(lines) and not lines[i + 1].strip():
                nxt = lines[i + 2].strip()
                cm = re.fullmatch(r"\*([^*]+)\*", nxt)
                if cm:
                    cap = cm.group(1)
                    i += 2
            body.append(
                f'<figure><img src="{html.escape(src)}" alt="{html.escape(alt)}">'
                + (f"<figcaption>{inline(cap)}</figcaption>" if cap else "")
                + "</figure>"
            )
            i += 1
            continue

        # 无序列表
        if re.match(r"^[-*]\s+", line):
            items = []
            while i < len(lines) and re.match(r"^[-*]\s+", lines[i]):
                items.append(re.sub(r"^[-*]\s+", "", lines[i]))
                i += 1
            body.append("<ul>" + "".join(f"<li>{inline(x)}</li>" for x in items) + "</ul>")
            continue

        # 有序列表
        if re.match(r"^\d+\.\s+", line):
            items = []
            while i < len(lines) and re.match(r"^\d+\.\s+", lines[i]):
                items.append(re.sub(r"^\d+\.\s+", "", lines[i]))
                i += 1
            body.append("<ol>" + "".join(f"<li>{inline(x)}</li>" for x in items) + "</ol>")
            continue

        # 普通段落（连续非空行合并）
        buf = [line.strip()]
        i += 1
        while i < len(lines):
            nxt = lines[i].strip()
            if not nxt or nxt.startswith(("#", ">", "|", "```", "- ", "* ", "![")) or re.match(r"^\d+\.\s", nxt):
                break
            buf.append(nxt)
            i += 1
        body.append(f"<p>{inline(''.join(buf))}</p>")

    return "\n".join(body)


CSS = """
*{box-sizing:border-box}
body{margin:0;background:#eef1f5;font-family:"PingFang SC","Microsoft YaHei",system-ui,sans-serif;color:#3f3f3f}
.page{max-width:375px;margin:32px auto;background:#fff;border-radius:10px;box-shadow:0 6px 28px rgba(20,40,80,.10);overflow:hidden}
.meta{padding:18px 22px 14px;background:#f8fafc;border-bottom:1px solid #e8edf3;font-size:12px;line-height:1.9;color:#7a8798}
.meta b{color:#334155;font-weight:600}
.meta .hint{display:block;margin-top:6px;color:#b06a00;background:#fff7e6;border:1px dashed #f0c37a;border-radius:6px;padding:6px 9px}
.wx-article{max-width:375px;margin:0 auto;padding:26px 20px 38px;background:#fff;
  font-family:"PingFang SC","Microsoft YaHei",system-ui,sans-serif;font-size:16px;line-height:1.9;color:#3f3f3f;letter-spacing:.3px}
.wx-article h1{font-size:22px;line-height:1.55;color:#14171c;font-weight:700;letter-spacing:0;margin:0 0 18px}
.wx-article h2{font-size:18px;line-height:1.6;color:#1a56a8;font-weight:700;margin:34px 0 14px;padding-left:11px;border-left:4px solid #2f7be0}
.wx-article h3{font-size:16px;color:#1f2937;font-weight:700;margin:24px 0 10px}
.wx-article p{margin:0 0 18px}
.wx-article strong{color:#14171c;font-weight:700}
.wx-article em{font-style:normal;color:#5b6b7f}
.wx-article a{color:#2f7be0;text-decoration:none;word-break:break-all}
.wx-article blockquote{margin:18px 0 22px;padding:12px 14px;background:#f6f9fd;border-left:3px solid #6aa9e9;border-radius:0 6px 6px 0;color:#4d6076;font-size:15px;line-height:1.85}
.wx-article blockquote.note{background:#fffbf2;border-left-color:#e8b45c;color:#8a6524;font-size:14px}
.wx-article blockquote p{margin:0}
.wx-article ul,.wx-article ol{margin:0 0 18px;padding-left:22px}
.wx-article li{margin:6px 0;line-height:1.85}
.wx-article code{background:#f2f5f9;color:#c0392b;padding:1px 5px;border-radius:4px;font-size:14px;
  font-family:Consolas,"Courier New",monospace;word-break:break-all}
.wx-article pre{margin:14px 0 20px;background:#f6f8fa;border:1px solid #e6ebf1;border-radius:8px;padding:12px 14px;overflow-x:auto}
.wx-article pre code{background:none;color:#2d3748;padding:0;font-size:12.5px;line-height:1.7;white-space:pre-wrap;word-break:break-all}
.wx-article table{width:100%;border-collapse:collapse;margin:16px 0 22px;font-size:12px;line-height:1.6;table-layout:auto}
.wx-article th{background:#eef4fb;color:#274b7a;font-weight:700;text-align:left;padding:6px 4px;border-bottom:1px solid #dbe6f2;word-break:break-word}
.wx-article td{padding:6px 4px;border-bottom:1px solid #edf1f6;color:#4a5a6d;word-break:break-word;vertical-align:top}
.wx-article table.compact{font-size:11px;line-height:1.55}
.wx-article tr:last-child td{border-bottom:none}
.wx-article figure{margin:20px 0 24px}
.wx-article img{display:block;width:100%;border-radius:8px;border:1px solid #e8edf3}
.wx-article figcaption{margin-top:8px;font-size:13px;line-height:1.7;color:#8a94a6;text-align:center}
.wx-article .divider{margin:30px 0;text-align:center;color:#c3ccd8;font-size:13px;letter-spacing:6px}
"""


def main() -> None:
    md = SRC.read_text(encoding="utf-8")
    # 草稿末尾的发布清单不进正文
    md = md.split("## 附：配图清单")[0].rstrip().rstrip("-").rstrip()
    body = convert(md)

    title = md.lstrip().splitlines()[0].lstrip("# ").strip()
    images = len(re.findall(r"!\[[^\]]*\]\(", md))
    words = len(re.findall(r"[\u4e00-\u9fff]", md))   # 按中文字符计，与公众号字数接近

    page = f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(title)} — 公众号排版稿</title>
<style>{CSS}</style>
</head>
<body>
<div class="page">
  <div class="meta">
    <b>公众号排版稿</b> · 正文约 {words} 字 · 配图 {images} 张<br>
    标题：{html.escape(title)}<br>
    摘要：绿色单文件、不联网、不用装运行时，框选一下自动滚到底，拼成一张不叠字、不断行的长图。工具在外网用 AI 辅助写成，代码开源免费。<br>
    封面：<code>docs/images/wechat-cover.png</code>（900×383）
    <span class="hint">复制方法：从下面大标题开始，拖到文末全选 → Ctrl+C → 粘进公众号编辑器。图片会随选择一起复制，粘贴后逐张确认是否上传成功。</span>
  </div>
  <article class="wx-article">
{body}
  </article>
</div>
</body>
</html>
"""
    OUT.write_text(page, encoding="utf-8")
    print("html:", OUT, len(page), "chars,", images, "images,", words, "words")


if __name__ == "__main__":
    main()
