"""把 ue/UELink 换成公共库 fixcode-h/UELink 的某个发布版（不写版本取最新正式版）。

用法：python scripts/sync_uelink.py [版本]
下载 manifest.json 和源码包，按清单校验 sha256，解压覆盖 ue/UELink，写 ue/uelink.json。
C++ 不在本仓库改：改动去 UELink 仓库做、发版，再跑这个脚本（见 ue/VENDORED.md）。
"""
import hashlib
import io
import json
import shutil
import sys
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEST = ROOT / "ue" / "UELink"


def fetch(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=120) as r:
        return r.read()


def main() -> int:
    version = sys.argv[1] if len(sys.argv) > 1 else None
    base = (f"https://github.com/fixcode-h/UELink/releases/download/v{version}"
            if version else "https://github.com/fixcode-h/UELink/releases/latest/download")
    manifest = json.loads(fetch(f"{base}/manifest.json"))
    data = fetch(manifest["url"])
    sha = hashlib.sha256(data).hexdigest()
    if sha != manifest["sha256"]:
        print(f"sha256 不对：{sha}，清单里是 {manifest['sha256']}", file=sys.stderr)
        return 1
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        names = z.namelist()
        # 防目录穿越：只认 UELink/ 下的相对路径
        if any(n.startswith("/") or ".." in n.split("/") or not n.startswith("UELink/") for n in names):
            print("源码包里有不认识的路径", file=sys.stderr)
            return 1
        if DEST.exists():
            shutil.rmtree(DEST)
        z.extractall(DEST.parent)
    (ROOT / "ue" / "uelink.json").write_text(
        json.dumps({"version": manifest["version"], "sha256": sha}, indent=2) + "\n", encoding="utf-8")
    print(f"ue/UELink 换成 UELink {manifest['version']}（{len(names)} 个文件）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
