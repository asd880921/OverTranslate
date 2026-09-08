"""把五份語系正文填進 template.md，輸出完整的發布說明。

    python .claude/skills/git-release-overtranslate/build.py \
        --version 2.2.1-beta.2 --bodies <目錄> --out <檔案>

<目錄> 底下要有 body.en.md、body.zh-Hant.md、body.zh-Hans.md、body.ja.md、
body.ko.md 五個檔，各自只放正文（`## ◆ …` 那幾段），不含 H1 標題與安裝章節。

存在的理由是安裝章節那五段樣板：手打會漏一個語言區塊，或在複製五段 CJK 的
過程中把某個字弄壞，而這種錯誤看起來完全正常。這裡改成填空，樣板只有
template.md 一份。
"""
import argparse
import pathlib
import sys

LANGS = {
    "BODY_EN": "en",
    "BODY_ZH_HANT": "zh-Hant",
    "BODY_ZH_HANS": "zh-Hans",
    "BODY_JA": "ja",
    "BODY_KO": "ko",
}


def outline(body):
    """(段數, 每段的條列數)。語言不同，骨架要相同。"""
    counts = []
    for line in body.splitlines():
        if line.startswith("## "):
            counts.append(0)
        elif line.startswith("- ") and counts:
            counts[-1] += 1
    return len(counts), counts


def main():
    here = pathlib.Path(__file__).parent
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True, help="目標 tag，例如 2.2.1-beta.2")
    ap.add_argument("--bodies", required=True, type=pathlib.Path)
    ap.add_argument("--out", required=True, type=pathlib.Path)
    args = ap.parse_args()

    text = (here / "template.md").read_text(encoding="utf-8")
    text = text.replace("{VERSION}", args.version)

    shape = {}
    for token, lang in LANGS.items():
        path = args.bodies / f"body.{lang}.md"
        if not path.exists():
            sys.exit(f"缺少正文：{path}")
        body = path.read_text(encoding="utf-8").strip()
        if not body:
            sys.exit(f"正文是空的：{path}")
        if "TODO" in body:
            sys.exit(f"正文還留著 TODO：{path}")
        shape[lang] = outline(body)
        text = text.replace("{" + token + "}", body)

    # 五份的骨架要一模一樣。少一段或少一條在成品裡看不出來——那是一份沒人會從頭讀到
    # 尾的長文，而讀某個語言的人只看得到自己那塊。實際發生過：英文版整段漏掉。
    reference = shape["zh-Hant"]
    for lang, got in shape.items():
        if got != reference:
            sys.exit(
                f"body.{lang}.md 的結構與 body.zh-Hant.md 不一致：\n"
                f"  zh-Hant：{reference[0]} 段，每段 {reference[1]} 條\n"
                f"  {lang}：{got[0]} 段，每段 {got[1]} 條"
            )

    left = [t for t in LANGS if "{" + t + "}" in text]
    if left or "{VERSION}" in text:
        sys.exit(f"樣板還有沒填的欄位：{left + (['VERSION'] if '{VERSION}' in text else [])}")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    # 貼進 GitHub 的東西一律 LF：CRLF 在 release body 的程式碼區塊裡會多出空行。
    args.out.write_bytes(text.replace("\r\n", "\n").encode("utf-8"))
    print(args.out)


if __name__ == "__main__":
    main()
