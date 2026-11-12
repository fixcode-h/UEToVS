#!/usr/bin/env python3
"""Release helper used by CI (works on Windows and Linux, stdlib only).

  release.py version                      -> prints the top version in CHANGELOG.md (e.g. 0.2.0)
  release.py stamp                        -> writes that version into Directory.Build.props and the VSIX manifest
  release.py notes --out FILE [--prev TAG] [--repo owner/name] [--vsix NAME]
                                          -> release notes markdown for the top version
  release.py check-pr --base REF          -> fails if src/ changed but the top CHANGELOG version is already tagged
"""
import argparse
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CHANGELOG = os.path.join(ROOT, "CHANGELOG.md")
PROPS = os.path.join(ROOT, "Directory.Build.props")
MANIFEST = os.path.join(ROOT, "src", "UEToVS.Host", "source.extension.vsixmanifest")
VERSION_RE = re.compile(r"^## v(\d+\.\d+\.\d+)\s*$", re.M)


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def top_section(text=None):
    text = text if text is not None else read(CHANGELOG)
    m = VERSION_RE.search(text)
    if not m:
        sys.exit("CHANGELOG.md has no '## vX.Y.Z' section")
    nxt = VERSION_RE.search(text, m.end())
    body = text[m.end(): nxt.start() if nxt else len(text)].strip()
    return m.group(1), body


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True).stdout.strip()


def tags():
    out = git("tag", "--list", "v*", "--sort=-v:refname")
    return [t for t in out.splitlines() if t]


def cmd_stamp(_):
    version, _ = top_section()
    props = read(PROPS)
    props = re.sub(r"<Version>[^<]*</Version>", f"<Version>{version}</Version>", props)
    write(PROPS, props)
    man = read(MANIFEST)
    man, n = re.subn(r'(<Identity\b[^>]*\bVersion=")[^"]*(")', rf"\g<1>{version}\g<2>", man)
    if n != 1:
        sys.exit("could not stamp the VSIX manifest version")
    write(MANIFEST, man)
    print(version)


def render_notes(version, body, prev, repo, vsix):
    # Changelog sub-headings (### x) become the release's sections; demote nothing else.
    lines = [f"## UEToVS v{version} 更新", "", body, "", "### 下载", ""]
    lines.append(f"- Windows — `{vsix}`：关闭 Visual Studio 后双击安装（VS 2022 17.x / VS 2026 18.x）")
    lines += [
        "",
        "运行需要 **.NET 8 运行时**（缺失时“输出 > UEToVS”里会提示）。VSIX 没有代码签名，安装器提示无法验证发布者时继续安装即可。",
        "已经装过的直接安装新包即可覆盖升级；设置、`.vs\\UEToVS` 下的索引和日志都会保留。包的 SHA256 在 `SHA256SUMS` 里。",
    ]
    if prev and repo:
        lines += ["", f"完整改动：[{prev}...v{version}](https://github.com/{repo}/compare/{prev}...v{version})"]
    return "\n".join(lines).rstrip() + "\n"


def cmd_notes(a):
    version, body = top_section()
    prev = a.prev
    if prev is None:
        prev = next((t for t in tags() if t != "v" + version), None)
    vsix = a.vsix or f"UEToVS-{version}.vsix"
    write(a.out, render_notes(version, body, prev, a.repo, vsix))
    print(a.out)


def cmd_version(_):
    print(top_section()[0])


def cmd_check_pr(a):
    changed = git("diff", "--name-only", f"{a.base}...HEAD").splitlines()
    code = [p for p in changed if p.startswith("src/") or p.startswith("ue/")]
    if not code:
        print("no product code changed; changelog not required")
        return
    version, body = top_section()
    if ("v" + version) in tags():
        sys.exit(f"src/ changed but CHANGELOG.md's top version v{version} is already released: add a new '## vX.Y.Z' section")
    base_text = subprocess.run(["git", "show", f"{a.base}:CHANGELOG.md"], cwd=ROOT, capture_output=True, text=True)
    if base_text.returncode == 0:
        try:
            base_version, _ = top_section(base_text.stdout)
            if base_version == version and ("v" + version) in tags():
                sys.exit("CHANGELOG.md top version unchanged")
        except SystemExit:
            pass
    if not body.strip():
        sys.exit(f"CHANGELOG.md section v{version} is empty")
    print(f"ok: will release v{version}")


def main():
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("version").set_defaults(fn=cmd_version)
    sub.add_parser("stamp").set_defaults(fn=cmd_stamp)
    n = sub.add_parser("notes")
    n.add_argument("--out", required=True)
    n.add_argument("--prev")
    n.add_argument("--repo")
    n.add_argument("--vsix")
    n.set_defaults(fn=cmd_notes)
    c = sub.add_parser("check-pr")
    c.add_argument("--base", required=True)
    c.set_defaults(fn=cmd_check_pr)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
