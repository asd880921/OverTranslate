"""兩個 tag 之間的變更清單，先濾掉使用者看不到的東西。

    python .claude/skills/git-release-overtranslate/collect.py <from-tag> <to-tag>

輸出三段：合併進來的 PR 分支（功能的天然分組）、留下來的 feat/fix commit、
以及被濾掉的數量。發布說明是寫給使用者看的，所以這裡濾掉的是他們感覺不到的
改動——重構、測試、文件、內部量測工具。濾掉的只印數量不印內容：真的要看，
`git log` 就在手邊，把幾十行雜訊印在正文旁邊只會讓人重新讀一遍。
"""
import re
import subprocess
import sys

# 只動內部結構的類型。fix 與 feat 一律留著，由人判斷。
NOISE_TYPES = ("chore:", "test:", "docs:", "refactor:", "style:", "build:", "ci:")

# 開發自己用的工具與診斷輸出。使用者裝到的版本裡沒有這些東西。
NOISE_WORDS = (
    "OcrHarness", "harness", "LayoutProbe", "--group-explain", "--roi-",
    "--estimate-precision", "--xlate-line", "診斷", "契約測試", "守門測試",
)


def run(*args):
    out = subprocess.run(
        ["git", *args], capture_output=True, check=True,
        # Windows 的 git 預設丟 UTF-8 位元組，但 Python 在這裡會挑到 cp950，
        # 中文 commit 訊息就會炸 UnicodeDecodeError。
        encoding="utf-8", errors="replace",
    )
    return out.stdout.rstrip("\n")


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    frm, to = sys.argv[1], sys.argv[2]

    subprocess.run(["git", "fetch", "--tags", "-q"], check=False)
    for tag in (frm, to):
        if subprocess.run(["git", "rev-parse", "-q", "--verify", f"{tag}^{{}}"],
                          capture_output=True).returncode:
            sys.exit(f"找不到 tag：{tag}\n現有的：\n" + run("tag", "-l", "--sort=-creatordate"))

    rng = f"{frm}..{to}"
    lines = run("log", "--oneline", "--no-decorate", rng).splitlines()

    merges = [l for l in run("log", "--oneline", "--no-decorate", "--merges", rng).splitlines()
              if "from" in l]

    kept, dropped = [], 0
    for line in lines:
        msg = line.split(" ", 1)[1] if " " in line else ""
        if msg.startswith("Merge "):
            continue
        if msg.startswith(NOISE_TYPES) or any(w in msg for w in NOISE_WORDS):
            dropped += 1
            continue
        kept.append(line)

    print(f"# {rng}：{len(lines)} 個 commit\n")

    print(f"## 合併進來的分支（{len(merges)}）\n")
    for line in merges:
        print("  " + re.sub(r"Merge pull request ", "", line))

    print(f"\n## 使用者看得到的變更（{len(kept)}）\n")
    for line in kept:
        print("  " + line)

    print(f"\n（另有 {dropped} 個重構／測試／文件／內部工具的 commit 已濾除）")


if __name__ == "__main__":
    main()
