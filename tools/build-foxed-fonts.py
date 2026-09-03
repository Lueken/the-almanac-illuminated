"""Build the mark-capable derivative fonts for THE SPOTTED PAGE.

The game's Lora and Almendra carry ZERO combining marks (cmap verified
2026-09-03), so the book could not garble the way vanilla chat does — every
mark drew as a tofu box. This script grafts the nine combining marks the
game's own Montserrat carries (the same set vanilla's storm scramble can
actually render) into derivatives of Lora and Almendra:

    Lora     -> "Illuminated Serif"       (4 faces, outlines scaled 2048/1000)
    Almendra -> "Illuminated Decorative"  (3 faces, matching UPM)

Renamed per the SIL OFL: both families declare Reserved Font Names, so the
derivatives must not carry them. Copyright and license records are retained;
a modification note is appended to the copyright string. Montserrat is OFL
with no RFN declared, so its glyphs may be merged under the same license.

Run from the mod root against the game install named by VINTAGE_STORY:
    python tools/build-foxed-fonts.py
Outputs into resources/assets/almanacilluminated/fonts/.
"""
import os
from fontTools.ttLib import TTFont
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.recordingPen import DecomposingRecordingPen

GAMEDIR = os.environ.get("VINTAGE_STORY", r"C:/Users/31686/AppData/Roaming/Translocator/Versions/1.22.5")
FONTDIR = os.path.join(GAMEDIR, "assets", "game", "fonts")
OUTDIR = os.path.join(os.path.dirname(__file__), "..", "resources", "assets", "almanacilluminated", "fonts")

MARKS = [0x031B, 0x0300, 0x0301, 0x0327, 0x0328, 0x0335, 0x0336, 0x0338, 0x0337]

# (source face, donor face, out family, out file)
BUILDS = [
    ("Lora-Regular.ttf",     "Montserrat-Regular.ttf", "Illuminated Serif", "IlluminatedSerif-Regular.ttf"),
    ("Lora-Italic.ttf",      "Montserrat-Italic.ttf",  "Illuminated Serif", "IlluminatedSerif-Italic.ttf"),
    ("Lora-Bold.ttf",        "Montserrat-Bold.ttf",    "Illuminated Serif", "IlluminatedSerif-Bold.ttf"),
    ("Lora-BoldItalic.ttf",  "Montserrat-Italic.ttf",  "Illuminated Serif", "IlluminatedSerif-BoldItalic.ttf"),
    ("Almendra-Regular.ttf", "Montserrat-Regular.ttf", "Illuminated Decorative", "IlluminatedDecorative-Regular.ttf"),
    ("Almendra-Italic.ttf",  "Montserrat-Italic.ttf",  "Illuminated Decorative", "IlluminatedDecorative-Italic.ttf"),
    ("Almendra-Bold.ttf",    "Montserrat-Bold.ttf",    "Illuminated Decorative", "IlluminatedDecorative-Bold.ttf"),
]


def graft(src_path, donor_path, family, out_path):
    target = TTFont(src_path)
    donor = TTFont(donor_path)
    scale = target["head"].unitsPerEm / donor["head"].unitsPerEm
    dcmap = donor.getBestCmap()
    dset = donor.getGlyphSet()
    dhmtx = donor["hmtx"]

    order = list(target.getGlyphOrder())
    glyf = target["glyf"]
    hmtx = target["hmtx"]

    added = []
    for cp in MARKS:
        if cp not in dcmap:
            continue
        dname = dcmap[cp]
        gname = "uni%04X" % cp
        if gname in order:
            continue
        # Decompose (marks are simple today, but be safe), then scale into
        # the target's units-per-em.
        rec = DecomposingRecordingPen(dset)
        dset[dname].draw(rec)
        pen = TTGlyphPen(None)
        rec.replay(TransformPen(pen, (scale, 0, 0, scale, 0, 0)))
        glyph = pen.glyph()

        order.append(gname)
        glyf.glyphs[gname] = glyph
        adv, lsb = dhmtx[dname]
        hmtx.metrics[gname] = (round(adv * scale), round(lsb * scale))
        added.append(cp)

    target.setGlyphOrder(order)
    if hasattr(glyf, "glyphOrder"):
        glyf.glyphOrder = order

    # Map the new glyphs in every unicode cmap subtable.
    for table in target["cmap"].tables:
        if table.isUnicode():
            for cp in added:
                table.cmap[cp] = "uni%04X" % cp

    # OFL rename: drop the Reserved Font Name from every visible record.
    name = target["name"]
    sub = name.getDebugName(2) or "Regular"
    ps_family = family.replace(" ", "")
    copyright_old = name.getDebugName(0) or ""
    note = " Modified for The Almanac: Illuminated (combining marks merged from Montserrat, also OFL); renamed per the OFL."
    for rec in list(name.names):
        if rec.nameID == 7:
            name.removeNames(nameID=7, platformID=rec.platformID, platEncID=rec.platEncID, langID=rec.langID)
    plats = {(r.platformID, r.platEncID, r.langID) for r in name.names if r.nameID in (1, 4, 6)}
    for pid, eid, lid in plats:
        name.setName(copyright_old + note, 0, pid, eid, lid)
        name.setName(family, 1, pid, eid, lid)
        name.setName(f"{family} {sub}", 4, pid, eid, lid)
        name.setName(f"{ps_family}-{sub.replace(' ', '')}", 6, pid, eid, lid)
        name.setName(f"{ps_family}-{sub.replace(' ', '')};spotted-page", 3, pid, eid, lid)
        if name.getName(16, pid, eid, lid) is not None:
            name.setName(family, 16, pid, eid, lid)

    target.save(out_path)
    return added, scale


if __name__ == "__main__":
    os.makedirs(OUTDIR, exist_ok=True)
    for src, dn, fam, out in BUILDS:
        added, scale = graft(os.path.join(FONTDIR, src), os.path.join(FONTDIR, dn), fam, os.path.join(OUTDIR, out))
        print(f"{out}: +{len(added)} marks from {dn} (scale {scale:.3f})")
