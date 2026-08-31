# -*- coding: utf-8 -*-
"""Generera OMP:s egna Winamp-classic-skins (.wsz) for dashboardspelaren.

Rattighetslaget ar hela poangen: varje pixel ritas HAR ur ett fargtema — inga
sprites, fonter eller bilddata lanas fran nagot befintligt skin. Geometrin
(arknamn, sprite-positioner, matt) lases ur sprite-geometri.json bredvid
skriptet; den ar exporterad ur webamps kallkod (MIT) och beskriver bara VAR
saker ritas, aldrig HUR de ser ut. Se SKINS-README.md i skins-katalogen.

Korning:  python generate-omp-skins.py   (kraver Pillow)
Utdata:   ../../OpenModulePlatform.Portal/wwwroot/lib/webamp/skins/*.wsz
"""
import io
import json
import os
import re
import zipfile

from PIL import Image, ImageDraw

HAR = os.path.dirname(os.path.abspath(__file__))
GEOMETRI = os.path.join(HAR, "sprite-geometri.json")
UT = os.path.normpath(os.path.join(
    HAR, "..", "..", "OpenModulePlatform.Portal", "wwwroot", "lib", "webamp", "skins"))

CHAR_W, CHAR_H = 5, 6


def las_geometri():
    d = json.load(open(GEOMETRI, encoding="utf-8"))
    font = {k: tuple(v) for k, v in d["font_lookup"].items()}
    return d["ark"], font


# ---------------------------------------------------------------- 5x6-pixelfont
# Varje glyf: 6 rader à 5 bitar (MSB = vänster). Egen design — inga lånade fonter.
G = {
    "a": [0b01110, 0b00001, 0b01111, 0b10001, 0b10001, 0b01111],
    "b": [0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b11110],
    "c": [0b01110, 0b10001, 0b10000, 0b10000, 0b10001, 0b01110],
    "d": [0b00001, 0b00001, 0b01111, 0b10001, 0b10001, 0b01111],
    "e": [0b01110, 0b10001, 0b11111, 0b10000, 0b10001, 0b01110],
    "f": [0b00110, 0b01001, 0b01000, 0b11100, 0b01000, 0b01000],
    "g": [0b01111, 0b10001, 0b01111, 0b00001, 0b10001, 0b01110],
    "h": [0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b10001],
    "i": [0b00100, 0b00000, 0b01100, 0b00100, 0b00100, 0b01110],
    "j": [0b00010, 0b00000, 0b00010, 0b00010, 0b10010, 0b01100],
    "k": [0b10000, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010],
    "l": [0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
    "m": [0b00000, 0b11010, 0b10101, 0b10101, 0b10101, 0b10101],
    "n": [0b00000, 0b11110, 0b10001, 0b10001, 0b10001, 0b10001],
    "o": [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
    "p": [0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000],
    "q": [0b01111, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001],
    "r": [0b00000, 0b10110, 0b11001, 0b10000, 0b10000, 0b10000],
    "s": [0b01111, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110],
    "t": [0b01000, 0b11100, 0b01000, 0b01000, 0b01001, 0b00110],
    "u": [0b00000, 0b10001, 0b10001, 0b10001, 0b10001, 0b01111],
    "v": [0b00000, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100],
    "w": [0b00000, 0b10101, 0b10101, 0b10101, 0b10101, 0b01010],
    "x": [0b00000, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001],
    "y": [0b10001, 0b10001, 0b01111, 0b00001, 0b10001, 0b01110],
    "z": [0b11111, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111],
    "0": [0b01110, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
    "1": [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b01110],
    "2": [0b01110, 0b10001, 0b00010, 0b00100, 0b01000, 0b11111],
    "3": [0b11110, 0b00001, 0b00110, 0b00001, 0b00001, 0b11110],
    "4": [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010],
    "5": [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b11110],
    "6": [0b01110, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
    "7": [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000],
    "8": [0b01110, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
    "9": [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b01110],
    "-": [0b00000, 0b00000, 0b01110, 0b00000, 0b00000, 0b00000],
    "_": [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b11111],
    ".": [0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100],
    ",": [0b00000, 0b00000, 0b00000, 0b00110, 0b00100, 0b01000],
    ":": [0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100],
    ";": [0b00000, 0b01100, 0b00000, 0b01100, 0b00100, 0b01000],
    "!": [0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100],
    "?": [0b01110, 0b10001, 0b00010, 0b00100, 0b00000, 0b00100],
    "(": [0b00010, 0b00100, 0b01000, 0b01000, 0b00100, 0b00010],
    ")": [0b01000, 0b00100, 0b00010, 0b00010, 0b00100, 0b01000],
    "[": [0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110],
    "]": [0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110],
    "/": [0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b00000],
    "\\": [0b10000, 0b01000, 0b00100, 0b00010, 0b00001, 0b00000],
    "+": [0b00000, 0b00100, 0b01110, 0b00100, 0b00000, 0b00000],
    "=": [0b00000, 0b01110, 0b00000, 0b01110, 0b00000, 0b00000],
    "*": [0b00000, 0b10101, 0b01110, 0b10101, 0b00000, 0b00000],
    "&": [0b01100, 0b10010, 0b01100, 0b10101, 0b10010, 0b01101],
    "%": [0b11001, 0b11010, 0b00100, 0b01011, 0b10011, 0b00000],
    "$": [0b00100, 0b01111, 0b10100, 0b01110, 0b00101, 0b11110],
    "#": [0b01010, 0b11111, 0b01010, 0b01010, 0b11111, 0b01010],
    "@": [0b01110, 0b10001, 0b10111, 0b10101, 0b10111, 0b01110],
    "'": [0b00100, 0b00100, 0b01000, 0b00000, 0b00000, 0b00000],
    '"': [0b01010, 0b01010, 0b00000, 0b00000, 0b00000, 0b00000],
    "<": [0b00010, 0b00100, 0b01000, 0b00100, 0b00010, 0b00000],
    ">": [0b01000, 0b00100, 0b00010, 0b00100, 0b01000, 0b00000],
}


def hexf(c):
    return "#%02X%02X%02X" % c


class Tema:
    def __init__(self, namn, titel, bg, panel, kant, accent, accent2, text, dim, tryckt):
        self.namn, self.titel = namn, titel
        self.bg, self.panel, self.kant = bg, panel, kant
        self.accent, self.accent2, self.text, self.dim, self.tryckt = accent, accent2, text, dim, tryckt


MATRIX = Tema("omp-matrix", "OMP MATRIX",
              bg=(4, 12, 6), panel=(10, 24, 12), kant=(0, 60, 24),
              accent=(0, 255, 102), accent2=(0, 150, 60), text=(180, 255, 200),
              dim=(60, 110, 75), tryckt=(0, 40, 16))
LJUS = Tema("omp-ljus", "OMP PLAYER",
            bg=(238, 240, 242), panel=(220, 224, 228), kant=(150, 158, 166),
            accent=(0, 102, 204), accent2=(0, 70, 140), text=(30, 34, 40),
            dim=(120, 128, 136), tryckt=(190, 196, 202))


def rita_text(d, x, y, s, farg):
    for i, ch in enumerate(s.lower()):
        gl = G.get(ch)
        if not gl:
            continue
        for ry, rad in enumerate(gl):
            for rx in range(5):
                if rad & (1 << (4 - rx)):
                    d.point((x + i * (CHAR_W + 1) + rx, y + ry), fill=farg)


def panelplatta(d, x, y, w, h, t, nedtryckt=False, aktiv=False):
    fyll = t.tryckt if nedtryckt else (t.kant if aktiv else t.panel)
    d.rectangle([x, y, x + w - 1, y + h - 1], fill=fyll, outline=t.kant)
    if not nedtryckt:
        d.line([x + 1, y + 1, x + w - 2, y + 1], fill=t.dim)


def symbol(d, namn, x, y, w, h, t, nedtryckt):
    if w < 6 or h < 6:
        return
    cx, cy = x + w // 2, y + h // 2
    f = t.accent if not nedtryckt else t.accent2
    n = namn.upper()
    if "PREVIOUS" in n:
        d.polygon([(cx + 3, cy - 4), (cx + 3, cy + 4), (cx - 2, cy)], fill=f)
        d.rectangle([cx - 5, cy - 4, cx - 4, cy + 4], fill=f)
    elif "PAUSE" in n:
        d.rectangle([cx - 4, cy - 4, cx - 2, cy + 4], fill=f)
        d.rectangle([cx + 1, cy - 4, cx + 3, cy + 4], fill=f)
    elif "PLAY" in n:
        d.polygon([(cx - 3, cy - 4), (cx - 3, cy + 4), (cx + 4, cy)], fill=f)
    elif "STOP" in n:
        d.rectangle([cx - 3, cy - 3, cx + 3, cy + 3], fill=f)
    elif "NEXT" in n:
        d.polygon([(cx - 4, cy - 4), (cx - 4, cy + 4), (cx + 1, cy)], fill=f)
        d.rectangle([cx + 3, cy - 4, cx + 4, cy + 4], fill=f)
    elif "EJECT" in n:
        d.polygon([(cx - 4, cy + 1), (cx + 4, cy + 1), (cx, cy - 4)], fill=f)
        d.rectangle([cx - 4, cy + 3, cx + 4, cy + 4], fill=f)
    elif "CLOSE" in n:
        d.line([x + 2, y + 2, x + w - 3, y + h - 3], fill=f)
        d.line([x + w - 3, y + 2, x + 2, y + h - 3], fill=f)
    elif "MINIMIZE" in n:
        d.rectangle([x + 2, y + h - 4, x + w - 3, y + h - 3], fill=f)
    elif "SHADE" in n:
        d.rectangle([x + 2, y + 2, x + w - 3, y + 3], fill=f)
    elif "OPTIONS" in n or "MENU" in n:
        d.rectangle([x + 2, cy - 1, x + w - 3, cy], fill=f)


def etikett_for(namn):
    n = namn.upper()
    for nyckel, txt in (("SHUFFLE", "shuf"), ("REPEAT", "rep"),
                        ("PRESET", "pre"), ("AUTO", "auto"), ("_ON_", "on"),
                        ("GRAPH", ""), ("EQ_BUTTON", "eq"),
                        ("PLAYLIST", "pl"),
                        ("MONO", "mono"), ("STEREO", "stereo"),
                        ("EQ_", "eq")):
        if nyckel in n:
            return txt
    return None


def digit(d, x, y, w, h, tal, farg, bg):
    d.rectangle([x, y, x + w - 1, y + h - 1], fill=bg)
    seg = {0: "abcdef", 1: "bc", 2: "abged", 3: "abgcd", 4: "fgbc",
           5: "afgcd", 6: "afgedc", 7: "abc", 8: "abcdefg", 9: "abcfgd"}[tal]
    x0, x1 = x + 1, x + w - 2
    ym, y0, y1 = y + h // 2, y + 1, y + h - 2
    if "a" in seg: d.line([x0, y0, x1, y0], fill=farg, width=2)
    if "b" in seg: d.line([x1, y0, x1, ym], fill=farg, width=2)
    if "c" in seg: d.line([x1, ym, x1, y1], fill=farg, width=2)
    if "d" in seg: d.line([x0, y1, x1, y1], fill=farg, width=2)
    if "e" in seg: d.line([x0, ym, x0, y1], fill=farg, width=2)
    if "f" in seg: d.line([x0, y0, x0, ym], fill=farg, width=2)
    if "g" in seg: d.line([x0, ym, x1, ym], fill=farg, width=2)


def blanda(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def bygg_ark(arknamn, poster, t, font_lookup):
    W = max(p["x"] + p["w"] for p in poster)
    H = max(p["y"] + p["h"] for p in poster)
    img = Image.new("RGB", (W, H), t.bg)
    d = ImageDraw.Draw(img)

    if arknamn == "MAIN":
        d.rectangle([0, 0, W - 1, H - 1], fill=t.bg, outline=t.kant)
        for yy in range(0, H, 4):
            d.line([1, yy, W - 2, yy], fill=blanda(t.bg, t.panel, 0.55))
        d.rectangle([23, 42, 23 + 76, 42 + 16], outline=t.kant)   # vis-omrade
        d.rectangle([110, 22, 264, 40], outline=t.kant)           # infotext-omrade
        return img

    if arknamn == "TEXT":
        d.rectangle([0, 0, W - 1, H - 1], fill=t.bg)
        for ch, (rad, kol) in font_lookup.items():
            gl = G.get(ch.lower())
            if not gl:
                continue
            for ry, rd in enumerate(gl):
                for rx in range(5):
                    if rd & (1 << (4 - rx)):
                        d.point((kol * CHAR_W + rx, rad * CHAR_H + ry), fill=t.text)
        return img

    if arknamn in ("NUMBERS", "NUMS_EX"):
        d.rectangle([0, 0, W - 1, H - 1], fill=t.bg)
        for p in poster:
            m = re.search(r"DIGIT_(\d)", p["name"])
            if m:
                digit(d, p["x"], p["y"], p["w"], p["h"], int(m.group(1)), t.accent, t.bg)
            elif "MINUS" in p["name"]:
                d.line([p["x"] + 1, p["y"] + p["h"] // 2, p["x"] + p["w"] - 2, p["y"] + p["h"] // 2],
                       fill=t.accent, width=2)
        return img

    if arknamn in ("VOLUME", "BALANCE"):
        d.rectangle([0, 0, W - 1, H - 1], fill=t.bg)
        bgr = [p for p in poster if "BACKGROUND" in p["name"]]
        for p in bgr:
            m = re.search(r"_(\d+)$", p["name"])
            niva = int(m.group(1)) / max(1, len(bgr) - 1) if m else 0
            fyll = blanda(t.panel, t.accent2, niva)
            d.rectangle([p["x"], p["y"], p["x"] + p["w"] - 1, p["y"] + p["h"] - 1],
                        fill=t.bg, outline=t.kant)
            bredd = int((p["w"] - 4) * niva) if arknamn == "VOLUME" else (p["w"] - 4)
            if bredd > 0:
                d.rectangle([p["x"] + 2, p["y"] + p["h"] // 2 - 1,
                             p["x"] + 2 + bredd, p["y"] + p["h"] // 2 + 1], fill=fyll)
        for p in poster:
            if "THUMB" in p["name"]:
                panelplatta(d, p["x"], p["y"], p["w"], p["h"], t,
                            nedtryckt="SELECTED" in p["name"] or "ACTIVE" in p["name"])
                d.rectangle([p["x"] + p["w"] // 2 - 1, p["y"] + 2,
                             p["x"] + p["w"] // 2, p["y"] + p["h"] - 3], fill=t.accent)
        return img

    if arknamn == "POSBAR":
        d.rectangle([0, 0, W - 1, H - 1], fill=t.bg)
        for p in poster:
            if "BACKGROUND" in p["name"]:
                d.rectangle([p["x"], p["y"], p["x"] + p["w"] - 1, p["y"] + p["h"] - 1],
                            fill=t.bg, outline=t.kant)
                d.line([p["x"] + 2, p["y"] + p["h"] // 2, p["x"] + p["w"] - 3, p["y"] + p["h"] // 2],
                       fill=t.dim)
            else:
                panelplatta(d, p["x"], p["y"], p["w"], p["h"], t, nedtryckt="SELECTED" in p["name"])
                d.rectangle([p["x"] + p["w"] // 2 - 1, p["y"] + 2,
                             p["x"] + p["w"] // 2, p["y"] + p["h"] - 3], fill=t.accent)
        return img

    # ---- generisk vag: plattor + symboler/etiketter per sprite ----
    d.rectangle([0, 0, W - 1, H - 1], fill=t.bg)
    for p in poster:
        n = p["name"].upper()
        nedtryckt = ("SELECTED" in n or "PRESSED" in n or "ACTIVE" in n or "DEPRESSED" in n)
        if arknamn == "TITLEBAR":
            aktivrad = "SELECTED" in n or n.endswith("_BAR")
            d.rectangle([p["x"], p["y"], p["x"] + p["w"] - 1, p["y"] + p["h"] - 1],
                        fill=(t.kant if "SELECTED" in n else t.panel), outline=t.kant)
            if p["w"] > 100:
                tx = p["x"] + (p["w"] - len(t.titel) * 6) // 2
                rita_text(d, tx, p["y"] + (p["h"] - 6) // 2, t.titel,
                          t.accent if "SELECTED" in n else t.dim)
            else:
                symbol(d, n, p["x"], p["y"], p["w"], p["h"], t, nedtryckt)
            continue
        panelplatta(d, p["x"], p["y"], p["w"], p["h"], t, nedtryckt=nedtryckt,
                    aktiv=("SELECTED" in n and "BUTTON" not in n))
        symbol(d, n, p["x"], p["y"], p["w"], p["h"], t, nedtryckt)
        et = etikett_for(n)
        if et and p["w"] >= len(et) * 6 + 2:
            tx = p["x"] + (p["w"] - len(et) * 6) // 2
            rita_text(d, tx, p["y"] + (p["h"] - 6) // 2, et,
                      t.accent if nedtryckt else t.text)
    return img


def viscolor(t):
    rader = []
    for i in range(24):
        c = blanda(t.bg, t.accent, i / 23)
        rader.append("%d,%d,%d, // viscolor %d" % (c[0], c[1], c[2], i))
    return "\r\n".join(rader) + "\r\n"


def pledit(t):
    return ("[Text]\r\nNormal=%s\r\nCurrent=%s\r\nNormalBG=%s\r\nSelectedBG=%s\r\n"
            "Font=Arial\r\n" % (hexf(t.text), hexf(t.accent), hexf(t.bg), hexf(t.panel)))


def bygg_skin(t, ark, font_lookup):
    os.makedirs(UT, exist_ok=True)
    ut = os.path.join(UT, t.namn + ".wsz")
    onskade = ["MAIN", "TITLEBAR", "CBUTTONS", "SHUFREP", "POSBAR", "VOLUME", "BALANCE",
               "MONOSTER", "PLAYPAUS", "NUMBERS", "NUMS_EX", "TEXT", "EQMAIN", "EQ_EX", "PLEDIT"]
    with zipfile.ZipFile(ut, "w", zipfile.ZIP_DEFLATED) as z:
        for namn in onskade:
            poster = ark.get(namn)
            if namn == "TEXT":
                poster = [{"x": 0, "y": 0, "w": 31 * CHAR_W, "h": 3 * CHAR_H, "name": "TEXT"}]
            if not poster:
                continue
            img = bygg_ark(namn, poster, t, font_lookup)
            buf = io.BytesIO()
            img.save(buf, format="PNG", optimize=True)
            z.writestr(namn + ".png", buf.getvalue())
        z.writestr("VISCOLOR.TXT", viscolor(t))
        z.writestr("PLEDIT.TXT", pledit(t))
        z.writestr("README.txt",
                   "%s - eget skin for OpenModulePlatforms dashboardspelare.\r\n"
                   "Genererat 2026-08-31 av gen_omp_skins.py (DEV-repot). Varje pixel ar\r\n"
                   "ritad av generatorn; inga sprites ar lanade fran nagot befintligt skin.\r\n"
                   "Upphovsratt: Optimal2. Far anvandas fritt inom OMP-installationer.\r\n" % t.titel)
    return ut


def main():
    ark, font_lookup = las_geometri()
    print("ark i geometrin:", sorted(ark))
    for t in (MATRIX, LJUS):
        p = bygg_skin(t, ark, font_lookup)
        print("skrev", p, os.path.getsize(p), "byte")


if __name__ == "__main__":
    main()
