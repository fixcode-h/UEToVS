#!/usr/bin/env python3
"""Builds the fonts bundled with UEToVS into src/UEToVS.Host/Resources/Fonts/<Family>/ (TTF + the family's OFL text).

The set and the weights mirror Agent Panel's font settings (fixcode-h/agent-panel web/src/fonts*.css, v0.5.17):
  英文字体  IBM Plex Sans / Inter / Maple Mono NL / 系统字体     400 500 600
  中文字体  思源黑体 Noto Sans SC / Maple Mono CN / 系统字体     Noto 400 500 600, Maple CN 400 500 (like Agent Panel)
  代码字体  Maple Mono NL / JetBrains Mono                       400 500 600 700
WPF cannot pick weights out of a variable font, so variable sources are instanced to static TTFs.

Sources (npm tarballs pinned by version + the registry's sha512; files pinned by sha256):
  IBM Plex Sans   @ibm/plex-sans 1.1.0                     IBMPlexSans-{Regular,Medium,SemiBold}.woff2 (font 3.005), WOFF2 -> TTF
                  only (lossless): the license reserves the name "Plex", so the files are not subset or renamed.
  Noto Sans SC    @expo-google-fonts/noto-sans-sc 0.4.4    Google Fonts static 400/500/600 (font 2.004), subset to GB2312 +
                  ASCII/Latin-1 + common CJK/fullwidth punctuation (reserved name is "Source", subsetting is allowed).
  Inter           @expo-google-fonts/inter 0.4.2           Google Fonts static 400/500/600 (Inter 4), subset to Latin + Latin
                  Extended + general punctuation (Agent Panel ships the same alphabets; other scripts fall to the CJK font).
  JetBrains Mono  @expo-google-fonts/jetbrains-mono 0.4.1  Google Fonts static 400/500/600/700, untouched.
  Maple Mono NL   MapleMonoNL-wght.woff2 from Agent Panel  the official variable "NL" build (no ligatures, v7.900) Agent Panel
                  ships, instanced at 400/500/600/700. Latin only: CJK in code comes from the 中文字体, as in Agent Panel.
  Maple Mono CN   MapleMono-NF-CN-unhinted.zip (official)  Regular + Medium, subset to Latin + GB2312 + CJK punctuation (Nerd
                  Font icons and rare CJK dropped), ligature features removed, family renamed "Maple Mono CN" (the
                  official non-NF CN name; Maple Mono's OFL declares no Reserved Font Name).

Needs: pip install fonttools brotli.
Usage: python3 scripts/build_fonts.py --maple-cn-zip MapleMono-NF-CN-unhinted.zip
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
INTER = ("@expo-google-fonts/inter", "0.4.2")
JETBRAINS = ("@expo-google-fonts/jetbrains-mono", "0.4.1")
# Agent Panel's copy of the official MapleMonoNL-Variable (converted to WOFF2), pinned by commit and hash.
MAPLE_NL_URL = ("https://raw.githubusercontent.com/fixcode-h/agent-panel/2e9ca0a6d7fc2fc9a8e1397d020902c90b5496ef/"
                "web/src/assets/fonts/maple-mono-nl/")
MAPLE_NL_FILES = {
    "MapleMonoNL-wght.woff2": "4032e43f5856ea2c490329c98e293a8daf0702271111faca54c3a6c4197bd8d1",
    "OFL.txt": None,
}
STATIC_WEIGHTS = {400: "Regular", 500: "Medium", 600: "SemiBold", 700: "Bold"}


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
    for src, dst in (("400Regular", "Regular"), ("500Medium", "Medium"), ("600SemiBold", "SemiBold")):
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


def fetch_file(url, sha256, path):
    with urllib.request.urlopen(url) as r:
        data = r.read()
    if sha256 and hashlib.sha256(data).hexdigest() != sha256:
        raise SystemExit(f"sha256 mismatch for {url}")
    with open(path, "wb") as f:
        f.write(data)
    return path


def set_names(font, family, style):
    """Static-instance naming: RIBBI styles keep the family in nameID 1; Medium / SemiBold get their own legacy family
    ("Maple Mono NL Medium") plus the typographic family (16/17) WPF groups them by, like official static builds."""
    name = font["name"]
    for rec in list(name.names):
        if rec.nameID in (1, 2, 3, 4, 6, 16, 17, 21, 22, 25):
            name.removeNames(nameID=rec.nameID)
    ribbi = style in ("Regular", "Bold")
    ps = (family + "-" + style).replace(" ", "")
    values = {1: family if ribbi else f"{family} {style}", 2: style if ribbi else "Regular", 3: f"{ps};UEToVS",
              4: family if style == "Regular" else f"{family} {style}", 6: ps}
    if not ribbi:
        values.update({16: family, 17: style})
    for nid, value in values.items():
        name.setName(value, nid, 3, 1, 0x409)
        name.setName(value, nid, 1, 0, 0)
    bold = style == "Bold"
    font["OS/2"].fsSelection = (font["OS/2"].fsSelection & ~0b1100001) | (0b100000 if bold else 0b1000000 if style == "Regular" else 0)
    font["head"].macStyle = 1 if bold else 0


def subset_font(font, unicodes, drop_features=(), hinting=True):
    features = sorted({r.FeatureTag for r in font["GSUB"].table.FeatureList.FeatureRecord} - set(drop_features)) if "GSUB" in font else []
    opts = subset.Options()
    opts.layout_features = features + ["kern", "mark", "mkmk"]
    opts.name_IDs = ["*"]
    opts.name_languages = ["*"]
    opts.notdef_outline = True
    opts.glyph_names = True
    opts.hinting = hinting
    s = subset.Subsetter(opts)
    s.populate(unicodes=unicodes)
    s.subset(font)


def latin_unicodes():
    # Basic Latin, Latin-1, Latin Extended-A/B, IPA/spacing modifiers, combining marks, general punctuation, currency,
    # letterlike symbols, arrows, math operators: what Agent Panel's latin + latin-ext slices cover.
    ranges = [(0x20, 0x7E), (0xA0, 0x24F), (0x250, 0x2FF), (0x300, 0x36F), (0x1E00, 0x1EFF), (0x2000, 0x206F),
              (0x20A0, 0x20CF), (0x2100, 0x214F), (0x2190, 0x21FF), (0x2200, 0x22FF), (0xFB00, 0xFB06), (0xFEFF, 0xFEFF), (0xFFFD, 0xFFFD)]
    return sorted({cp for a, b in ranges for cp in range(a, b + 1)})


def build_inter(pkg):
    out = os.path.join(OUT, "Inter")
    reset_dir(out)
    for weight in (400, 500, 600):
        style = STATIC_WEIGHTS[weight]
        font = TTFont(os.path.join(pkg, f"{weight}{style}", f"Inter_{weight}{style}.ttf"))
        subset_font(font, latin_unicodes())
        save_ttf(font, os.path.join(out, f"Inter-{style}.ttf"))
    lic = next(f for f in ("LICENSE_FONT", "OFL.txt", "LICENSE") if os.path.exists(os.path.join(pkg, f)))
    shutil.copyfile(os.path.join(pkg, lic), os.path.join(out, "OFL.txt"))


def build_jetbrains(pkg):
    out = os.path.join(OUT, "JetBrainsMono")
    reset_dir(out)
    for weight, style in STATIC_WEIGHTS.items():
        font = TTFont(os.path.join(pkg, f"{weight}{style}", f"JetBrainsMono_{weight}{style}.ttf"))
        save_ttf(font, os.path.join(out, f"JetBrainsMono-{style}.ttf"))
    lic = next(f for f in ("LICENSE_FONT", "OFL.txt", "LICENSE") if os.path.exists(os.path.join(pkg, f)))
    shutil.copyfile(os.path.join(pkg, lic), os.path.join(out, "OFL.txt"))


def build_maple_nl(work):
    from fontTools.varLib import instancer
    out = os.path.join(OUT, "MapleMonoNL")
    reset_dir(out)
    files = {n: fetch_file(MAPLE_NL_URL + n, h, os.path.join(work, n)) for n, h in MAPLE_NL_FILES.items()}
    for weight, style in STATIC_WEIGHTS.items():
        font = instancer.instantiateVariableFont(TTFont(files["MapleMonoNL-wght.woff2"]), {"wght": weight})
        font["OS/2"].usWeightClass = weight
        set_names(font, "Maple Mono NL", style)
        save_ttf(font, os.path.join(out, f"MapleMonoNL-{style}.ttf"))
    shutil.copyfile(files["OFL.txt"], os.path.join(out, "OFL.txt"))


def build_maple_cn(cn_zip):
    out = os.path.join(OUT, "MapleMonoCN")
    reset_dir(out)
    unicodes = sorted(set(cjk_unicodes()) | set(latin_unicodes()) | set(range(0x2300, 0x23FF)))
    with zipfile.ZipFile(cn_zip) as z:
        for style in ("Regular", "Medium"):  # Agent Panel ships 400 + 500 only; heavier text uses 500 made bold
            member = next(n for n in z.namelist() if n.endswith(f"-CN-{style}.ttf"))
            font = TTFont(io.BytesIO(z.read(member)))
            subset_font(font, unicodes, NO_LIGATURES, hinting=False)
            set_names(font, "Maple Mono CN", style)
            save_ttf(font, os.path.join(out, f"MapleMonoCN-{style}.ttf"))
            print(f"  from {member}")
        lic = next(n for n in z.namelist() if n.lower().endswith("license.txt"))
        with open(os.path.join(out, "OFL.txt"), "wb") as f:
            f.write(z.read(lic))


def verify():
    for family in ("IBMPlexSans", "Inter", "NotoSansSC", "MapleMonoNL", "MapleMonoCN", "JetBrainsMono"):
        folder = os.path.join(OUT, family)
        for f in sorted(os.listdir(folder)):
            if not f.endswith(".ttf"):
                continue
            font = TTFont(os.path.join(folder, f))
            gsub = {r.FeatureTag for r in font["GSUB"].table.FeatureList.FeatureRecord} if "GSUB" in font else set()
            if family == "MapleMonoCN" and gsub & NO_LIGATURES:
                raise SystemExit(f"{f} still has ligature features {gsub & NO_LIGATURES}")
            fam = font["name"].getDebugName(16) or font["name"].getDebugName(1)
            print(f"  ok {family}/{f}: family '{fam}', weight {font['OS/2'].usWeightClass}, {len(font.getBestCmap())} chars")
    total = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT) for f in fs)
    print(f"total {total / 1024 / 1024:.2f} MB in {os.path.relpath(OUT, ROOT)}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--maple-cn-zip", required=True, help="official MapleMono-NF-CN-unhinted.zip (Maple Mono CN source)")
    args = ap.parse_args()
    work = tempfile.mkdtemp(prefix="uetovs-fonts-")
    try:
        build_plex(fetch_package(*PLEX, work))
        build_inter(fetch_package(*INTER, work))
        build_noto(fetch_package(*NOTO, work))
        build_jetbrains(fetch_package(*JETBRAINS, work))
        build_maple_nl(work)
        build_maple_cn(args.maple_cn_zip)
        verify()
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
