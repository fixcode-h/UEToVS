#!/usr/bin/env python3
"""Builds the fonts bundled with UEToVS into src/UEToVS.Host/Resources/Fonts/<Family>/ (TTF + the family's OFL text).

Sources (npm registry tarballs, pinned by version and verified against the registry's sha512 integrity):
  IBM Plex Sans   @ibm/plex-sans 1.1.0                     fonts/complete/woff2/IBMPlexSans-{Regular,Medium,SemiBold}
                  (font version 3.005). Only decompressed WOFF2 -> TTF (lossless, no glyph or name change): the
                  license reserves the name "Plex", so the files are not subset or renamed.
  Noto Sans SC    @expo-google-fonts/noto-sans-sc 0.4.4    NotoSansSC_{400Regular,500Medium}.ttf (Google Fonts static
                  instances, font version 2.004). Subset to GB2312 + ASCII/Latin-1 + common CJK/fullwidth punctuation
                  (reserved name is "Source", so subsetting under the original name is allowed).
  Maple Mono NL   @fontsource/maple-mono 5.3.0             maple-mono-latin-{400,700}-normal.woff2 (Maple Mono v7.8).
                  "NL" = no ligatures: the GSUB features calt/liga/dlig (where Maple's ligatures live) are removed and
                  the family is renamed "Maple Mono NL", like upstream's own NL build.
                  NOTE: "Maple Mono" is a Reserved Font Name of the OFL. Prefer the official, unmodified NL build:
                  download MapleMonoNL-TTF.zip from https://github.com/subframe7536/maple-font/releases and pass
                  --maple-nl-zip <path>; its MapleMonoNL-Regular.ttf / MapleMonoNL-Bold.ttf are then copied untouched.

Needs: pip install fonttools brotli.  Usage: python3 scripts/build_fonts.py [--maple-nl-zip MapleMonoNL-TTF.zip]
"""
import argparse
import base64
import hashlib
import io
import json
import os
import shutil
import sys
import tarfile
import tempfile
import urllib.request
import zipfile

from fontTools import subset
from fontTools.ttLib import TTFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "UEToVS.Host", "Resources", "Fonts")
REGISTRY = "https://registry.npmjs.org"

PLEX = ("@ibm/plex-sans", "1.1.0")
NOTO = ("@expo-google-fonts/noto-sans-sc", "0.4.4")
MAPLE = ("@fontsource/maple-mono", "5.3.0")


def fetch_package(name, version, work):
    """Downloads name@version from the npm registry, checks its sha512 integrity, extracts it; returns the package dir."""
    with urllib.request.urlopen(f"{REGISTRY}/{name}/{version}") as r:
        meta = json.load(r)
    dist = meta["dist"]
    with urllib.request.urlopen(dist["tarball"]) as r:
        data = r.read()
    algo, _, expected = dist["integrity"].partition("-")
    if algo != "sha512" or base64.b64encode(hashlib.sha512(data).digest()).decode() != expected:
        raise SystemExit(f"integrity mismatch for {name}@{version}")
    target = os.path.join(work, name.replace("/", "_").replace("@", ""))
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tar:
        for m in tar.getmembers():  # no absolute paths, no "..", regular files only
            if not m.isfile() or m.name.startswith("/") or ".." in m.name.split("/"):
                continue
            tar.extract(m, target, **({"filter": "data"} if hasattr(tarfile, "data_filter") else {}))
    print(f"fetched {name}@{version} ({len(data) // 1024} KB)")
    return os.path.join(target, "package")


def reset_dir(path):
    shutil.rmtree(path, ignore_errors=True)
    os.makedirs(path)


def save_ttf(font, path):
    font.flavor = None
    font.save(path)
    print(f"  {os.path.relpath(path, ROOT)}  {os.path.getsize(path) // 1024} KB")


def build_plex(pkg):
    out = os.path.join(OUT, "IBMPlexSans")
    reset_dir(out)
    for style in ("Regular", "Medium", "SemiBold"):
        font = TTFont(os.path.join(pkg, "fonts", "complete", "woff2", f"IBMPlexSans-{style}.woff2"))
        save_ttf(font, os.path.join(out, f"IBMPlexSans-{style}.ttf"))
    shutil.copyfile(os.path.join(pkg, "LICENSE.txt"), os.path.join(out, "OFL.txt"))


def gb2312_text():
    chars = set()
    for hi in range(0xA1, 0xF8):
        for lo in range(0xA1, 0xFF):
            try:
                chars.add(bytes([hi, lo]).decode("gb2312"))
            except UnicodeDecodeError:
                pass
    return "".join(sorted(chars))


def cjk_unicodes():
    ranges = [(0x20, 0x7E), (0xA0, 0xFF), (0x2000, 0x206F), (0x2190, 0x21FF), (0x2460, 0x24FF), (0x2500, 0x257F),
              (0x25A0, 0x25FF), (0x3000, 0x303F), (0xFE30, 0xFE4F), (0xFF00, 0xFFEF)]
    return sorted({cp for a, b in ranges for cp in range(a, b + 1)} | {ord(c) for c in gb2312_text()})


def build_noto(pkg):
    out = os.path.join(OUT, "NotoSansSC")
    reset_dir(out)
    for src, dst in (("400Regular", "Regular"), ("500Medium", "Medium")):
        font = TTFont(os.path.join(pkg, src, f"NotoSansSC_{src}.ttf"))
        opts = subset.Options()
        opts.layout_features = ["ccmp", "locl", "kern", "palt", "halt", "vert", "vrt2", "fwid", "hwid"]
        opts.name_IDs = ["*"]
        opts.name_languages = ["*"]
        opts.notdef_outline = True
        opts.hinting = False  # CJK hinting is dropped by Windows rendering anyway; saves space
        s = subset.Subsetter(opts)
        s.populate(unicodes=cjk_unicodes())
        s.subset(font)
        save_ttf(font, os.path.join(out, f"NotoSansSC-{dst}.ttf"))
    shutil.copyfile(os.path.join(pkg, "LICENSE_FONT"), os.path.join(out, "OFL.txt"))


NO_LIGATURES = {"calt", "liga", "dlig", "rlig"}


def rename_family(font, family, style):
    name = font["name"]
    ps = (family + "-" + style).replace(" ", "")
    for rec in list(name.names):
        if rec.nameID in (16, 17, 21, 22):
            name.removeNames(nameID=rec.nameID)
    for nid, value in ((1, family), (2, style), (3, f"{ps};UEToVS"), (4, f"{family} {style}"), (6, ps)):
        name.setName(value, nid, 3, 1, 0x409)
        name.setName(value, nid, 1, 0, 0)


def build_maple(pkg, official_zip):
    out = os.path.join(OUT, "MapleMonoNL")
    reset_dir(out)
    if official_zip:
        with zipfile.ZipFile(official_zip) as z:
            for style in ("Regular", "Bold"):
                member = next(n for n in z.namelist() if n.endswith(f"MapleMono-NL-{style}.ttf") or n.endswith(f"MapleMonoNL-{style}.ttf"))
                with open(os.path.join(out, f"MapleMonoNL-{style}.ttf"), "wb") as f:
                    f.write(z.read(member))
                print(f"  official {member}")
    else:
        for weight, style in (("400", "Regular"), ("700", "Bold")):
            font = TTFont(os.path.join(pkg, "files", f"maple-mono-latin-{weight}-normal.woff2"))
            features = sorted({r.FeatureTag for r in font["GSUB"].table.FeatureList.FeatureRecord} - NO_LIGATURES)
            opts = subset.Options()
            opts.layout_features = features
            opts.name_IDs = ["*"]
            opts.name_languages = ["*"]
            opts.notdef_outline = True
            opts.glyph_names = True
            s = subset.Subsetter(opts)
            s.populate(unicodes=font.getBestCmap().keys())
            s.subset(font)
            rename_family(font, "Maple Mono NL", style)
            font["OS/2"].fsSelection = (font["OS/2"].fsSelection & ~0b1100001) | (0b100000 if style == "Bold" else 0b1000000)
            font["head"].macStyle = 1 if style == "Bold" else 0
            save_ttf(font, os.path.join(out, f"MapleMonoNL-{style}.ttf"))
    shutil.copyfile(os.path.join(pkg, "LICENSE"), os.path.join(out, "OFL.txt"))


def verify():
    for family, files in (("IBMPlexSans", None), ("NotoSansSC", None), ("MapleMonoNL", None)):
        folder = os.path.join(OUT, family)
        for f in sorted(os.listdir(folder)):
            if not f.endswith(".ttf"):
                continue
            font = TTFont(os.path.join(folder, f))
            gsub = {r.FeatureTag for r in font["GSUB"].table.FeatureList.FeatureRecord} if "GSUB" in font else set()
            if family == "MapleMonoNL" and gsub & NO_LIGATURES:
                raise SystemExit(f"{f} still has ligature features {gsub & NO_LIGATURES}")
            fam = font["name"].getDebugName(16) or font["name"].getDebugName(1)
            print(f"  ok {family}/{f}: family '{fam}', weight {font['OS/2'].usWeightClass}, {len(font.getBestCmap())} chars")
    total = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT) for f in fs)
    print(f"total {total / 1024 / 1024:.2f} MB in {os.path.relpath(OUT, ROOT)}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--maple-nl-zip", help="official MapleMonoNL-TTF.zip (used untouched instead of deriving NL)")
    args = ap.parse_args()
    work = tempfile.mkdtemp(prefix="uetovs-fonts-")
    try:
        build_plex(fetch_package(*PLEX, work))
        build_noto(fetch_package(*NOTO, work))
        build_maple(fetch_package(*MAPLE, work), args.maple_nl_zip)
        verify()
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
