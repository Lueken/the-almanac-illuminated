using System.Collections.Generic;

namespace AlmanacIlluminated;

/// <summary>What a named bookmark was torn from. Whatever the reader had to hand.</summary>
public enum ScrapMaterial { Parchment, Cloth, Leather }

/// <summary>
/// One generated bookmark face: a small grid of texels, the material it was made
/// from, and the ink colour that reads on it. Row-major, 0x00000000 where the scrap
/// is not there, so the silhouette lives in the alpha rather than in a mask.
/// </summary>
public sealed class ScrapSkin
{
    public readonly ScrapMaterial Material;
    public readonly int Width, Height;
    public readonly int[] Argb;

    /// <summary>Label colour that reads against this material: dark ink on the pale
    /// materials, cream on leather.</summary>
    public readonly double[] Ink;

    /// <summary>Stroke behind the label, for the 3D pass where the text sits on a
    /// mid-tone at a distance. The opposite end of the ramp from <see cref="Ink"/>.</summary>
    public readonly double[] InkStroke;

    public ScrapSkin(ScrapMaterial material, int w, int h, int[] argb, double[] ink, double[] inkStroke)
    {
        Material = material; Width = w; Height = h; Argb = argb; Ink = ink; InkStroke = inkStroke;
    }
}

/// <summary>
/// Named tabs are bookmarks, not printed index tabs: a scrap of parchment, cloth or
/// leather shoved between the pages because it was what the reader had to hand. This
/// builds one, as pixel art at texel grain, so both renderers draw the same thing.
///
/// Everything is deterministic from the label. The same chapter keeps the same scrap
/// across sessions and across the two presentations, and the strip never reshuffles
/// itself between frames. No Random without a seed, and nothing reads the clock.
///
/// Every colour below was sampled from a real texture on disk. Nothing here is an
/// invented palette. Provenance is cited at each ramp.
/// </summary>
public static class ScrapBookmark
{
    // Parchment. Sampled from resources/assets/almanacilluminated/textures/gui/bookframe.png
    // (page interior and the aged rim just inside the frame), plus the dominant tone of
    // textures/shape/book/paper.png and the shadow tone of textures/shape/book/scrap-cream.png.
    private static readonly int[] Parchment =
    {
        unchecked((int)0xFFF4EBDC),   // bookframe page interior, highlight
        unchecked((int)0xFFF0E7D8),   // bookframe page interior, dominant
        unchecked((int)0xFFEEE5CD),   // paper.png, 94.9% of the sheet
        unchecked((int)0xFFE9D9C0),   // bookframe page rim, aged
    };
    private static readonly int ParchmentEdge = unchecked((int)0xFFBAAA84);   // scrap-cream.png shadow tone

    // Cloth. Sampled from the vanilla linen block texture at
    // C:\Users\31686\AppData\Roaming\Vintagestory\assets\survival\textures\block\linen.png.
    // Warp and weft are the two dominant tones; alternating them by texel parity is the
    // weave, which is exactly how the source reads at 32x32.
    private static readonly int[] Cloth =
    {
        unchecked((int)0xFFD6CCAF),   // linen highlight
        unchecked((int)0xFFCDC4A6),   // linen dominant, warp
        unchecked((int)0xFFBAB192),   // linen weft
        unchecked((int)0xFFC3BA9C),   // linen mid
    };
    private static readonly int ClothEdge = unchecked((int)0xFFAEA283);       // linen darkest sampled tone

    // Leather. Sampled from resources/assets/almanacilluminated/textures/shape/book/cover.png
    // (the book's own cover, mottled dark leather), with the highlight taken from the
    // bookframe.png outer frame band so a leather scrap belongs to the same board.
    private static readonly int[] Leather =
    {
        unchecked((int)0xFF755245),   // bookframe outer frame band, highlight
        unchecked((int)0xFF66412F),   // cover.png
        unchecked((int)0xFF5B3828),   // cover.png, dominant
        unchecked((int)0xFF4D2C1D),   // cover.png
        unchecked((int)0xFF392219),   // cover.png
    };
    private static readonly int LeatherEdge = unchecked((int)0xFF2E1912);     // cover.png, deepest tone

    // Ink. Dark ink is the book's own body colour; the cream is the dominant tone of
    // textures/shape/book/scrap-cream.png, which is what pale writing on this book's
    // dark leather already looks like.
    private static readonly double[] DarkInk = { 0.13, 0.09, 0.05, 1.0 };
    private static readonly double[] CreamInk = { 0.886, 0.824, 0.675, 1.0 };

    /// <summary>Deepest bite any of the free-end routines takes out of a scrap, in texels.
    /// A renderer insets its label by at least this much from the free end so a name never
    /// lies across a tear. Raise it here and both renderers follow.</summary>
    public const int MaxEndBite = 5;

    private static readonly Dictionary<string, ScrapSkin> cache = new();

    /// <summary>
    /// What a reader reaches for, in the proportions they would actually have it. Paper
    /// is what the book is made of and what is lying about, so half the bookmarks are
    /// offcuts of it; a rag is common enough; a strap of leather is the odd one out. An
    /// even third each was tried first and read wrong, because a thumb strip that came
    /// out mostly dark leather looks like a decision rather than an accident.
    /// </summary>
    private static readonly ScrapMaterial[] Draw =
    {
        ScrapMaterial.Parchment, ScrapMaterial.Parchment, ScrapMaterial.Parchment, ScrapMaterial.Parchment,
        ScrapMaterial.Cloth, ScrapMaterial.Cloth, ScrapMaterial.Cloth,
        ScrapMaterial.Leather,
    };

    /// <summary>
    /// The material this label's bookmark is made of. Depends on the label alone, never
    /// on the grid size or the tab's position, so a chapter keeps its material at every
    /// GUI scale, in both presentations, and however the strip around it changes.
    /// </summary>
    public static ScrapMaterial MaterialFor(string label)
        => Draw[Avalanche(Hash(label)) % (uint)Draw.Length];

    /// <summary>
    /// This label's material as a plain 0..1 RGB tint, for a renderer that has modelled
    /// geometry under the generated face and needs the two to agree at the edges. The
    /// base tone of the ramp, so a sliver of mesh showing past the pixels reads as more
    /// of the same scrap rather than as a different object.
    /// </summary>
    public static double[] BaseToneFor(string label)
    {
        int argb = MaterialFor(label) switch
        {
            ScrapMaterial.Parchment => Parchment[1],
            ScrapMaterial.Cloth => Cloth[1],
            _ => Leather[2],
        };
        return new[] { ((argb >> 16) & 255) / 255.0, ((argb >> 8) & 255) / 255.0, (argb & 255) / 255.0, 1.0 };
    }

    /// <summary>
    /// Builds (or returns the cached) bookmark face at this texel size. The free end is
    /// the torn one; it sits on the right unless <paramref name="freeEndLeft"/>, which is
    /// what a left-hand ribbon needs, since it grows out of the book the other way.
    /// </summary>
    public static ScrapSkin Build(string label, int w, int h, bool freeEndLeft)
    {
        if (w < 4) w = 4;
        if (h < 3) h = 3;
        string key = label + "|" + w + "x" + h + (freeEndLeft ? "|L" : "|R");
        if (cache.TryGetValue(key, out var hit)) return hit;

        var material = MaterialFor(label);
        uint seed = Hash(label) ^ 0x9E3779B9u;

        // How many texels are missing from the free end of each row, and from the top
        // and bottom of each column. The silhouette is built facing right and mirrored
        // at the end if this is a left-hand ribbon, so one tear routine serves both sides.
        int[] cut = material switch
        {
            ScrapMaterial.Parchment => TornEnd(seed, h),
            ScrapMaterial.Cloth => PinkedEnd(seed, h),
            _ => ClippedEnd(h),
        };

        // Only paper tears along its length. Cloth is cut and leather is cut, so both
        // keep straight long edges and carry their character in the weave and the mottle.
        int[] top = material == ScrapMaterial.Parchment ? LongEdge(seed ^ 0x51ED2701u, w) : new int[w];
        int[] bot = material == ScrapMaterial.Parchment ? LongEdge(seed ^ 0x2F3B9A17u, w) : new int[w];

        var argb = new int[w * h];
        var tones = material switch
        {
            ScrapMaterial.Parchment => Parchment,
            ScrapMaterial.Cloth => Cloth,
            _ => Leather,
        };
        int edge = material switch
        {
            ScrapMaterial.Parchment => ParchmentEdge,
            ScrapMaterial.Cloth => ClothEdge,
            _ => LeatherEdge,
        };

        bool Covered(int x, int y)
            => x >= 0 && y >= 0 && x < w && y < h
            && x < w - cut[y] && y >= top[x] && y < h - bot[x];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!Covered(x, y)) continue;

                // Texels on the silhouette boundary, and the one tucked column against
                // the book, take the darkest tone. That single line is what stops the
                // scrap reading as a flat rectangle of colour.
                bool rim = x == 0
                    || !Covered(x + 1, y) || !Covered(x - 1, y)
                    || !Covered(x, y + 1) || !Covered(x, y - 1);

                argb[y * w + x] = rim ? edge : Tone(material, tones, seed, x, y);
            }
        }

        if (freeEndLeft) MirrorRows(argb, w, h);

        bool pale = material != ScrapMaterial.Leather;
        var skin = new ScrapSkin(material, w, h, argb,
            pale ? DarkInk : CreamInk,
            pale ? CreamInk : DarkInk);
        cache[key] = skin;
        return skin;
    }

    /// <summary>
    /// The fill tone for one texel. Each material mottles differently, and the difference
    /// is the point: even noise over four tones reads as static, not as a material.
    /// </summary>
    private static int Tone(ScrapMaterial material, int[] tones, uint seed, int x, int y)
    {
        uint r = Hash2(seed, x, y);
        switch (material)
        {
            case ScrapMaterial.Cloth:
            {
                // The weave, in blocks of two texels rather than single ones: at a
                // dozen texels tall a one-texel checker dissolves into noise, while a
                // coarse warp and weft still reads as woven. Warp and weft take the two
                // ENDS of the linen ramp so the crossing is actually visible; every
                // sixteenth texel slips a shade, which is the slub in a hand-woven cloth.
                bool warp = (((x >> 1) + (y >> 1)) & 1) == 0;
                if ((r & 15u) == 0) return tones[warp ? 3 : 1];
                return tones[warp ? 0 : 2];
            }

            case ScrapMaterial.Parchment:
            {
                // Paper is one colour with flecks in it, not four colours shuffled. Most
                // of the sheet is the base tone; the rest is a scatter of the neighbours.
                uint b = r & 15u;
                if (b < 10u) return tones[1];
                if (b < 12u) return tones[0];
                if (b < 14u) return tones[2];
                return tones[3];
            }

            default:
                // Leather mottles hard and evenly. That is what the cover texture does.
                return tones[(int)(r % (uint)tones.Length)];
        }
    }

    /// <summary>
    /// A torn end: a random walk in and out by a texel at a time. Paper does not tear
    /// straight, but it does not tear far either, so the walk is clamped shallow.
    /// </summary>
    private static int[] TornEnd(uint seed, int h)
    {
        var cut = new int[h];
        uint s = seed;
        int at = 2;
        for (int y = 0; y < h; y++)
        {
            s = XorShift(s);
            at += (int)(s % 3u) - 1;
            if (at < 0) at = 0;
            if (at > 5) at = 5;
            cut[y] = at;
        }
        return cut;
    }

    /// <summary>
    /// The long edge of a torn sheet: mostly straight, with the odd texel missing. One
    /// row in and out is enough. A sheet that wandered more would read as damaged rather
    /// than as torn from a larger one.
    /// </summary>
    private static int[] LongEdge(uint seed, int w)
    {
        var cut = new int[w];
        uint s = seed;
        int at = 0;
        for (int x = 0; x < w; x++)
        {
            s = XorShift(s);
            // Asymmetric on purpose. A nibble starts on one draw in eight and ends on
            // one in two, so the edge is mostly straight with a few short bites out of
            // it. Toggling on the same odds both ways spent half the length a texel
            // short, and read as chewed rather than torn.
            at = at == 0 ? ((s & 7u) == 0 ? 1 : 0) : ((s & 1u) == 0 ? 0 : 1);
            cut[x] = at;
        }
        return cut;
    }

    /// <summary>
    /// A pinked end: the sawtooth left by shears, with the odd thread still hanging on.
    /// Period four keeps the teeth legible at this grain.
    /// </summary>
    private static int[] PinkedEnd(uint seed, int h)
    {
        var cut = new int[h];
        uint s = seed;
        int phase = (int)(XorShift(seed) % 4u);
        for (int y = 0; y < h; y++)
        {
            // A four-row sawtooth, cut deep enough that the teeth survive the grain:
            // 0, 2, 4, 2 and round again.
            int t = (y + phase) & 3;
            cut[y] = t == 0 ? 0 : t == 2 ? 4 : 2;
            s = XorShift(s);
            if ((s & 7u) == 0 && cut[y] > 0) cut[y]--;   // a thread that survived the shears
        }
        return cut;
    }

    /// <summary>A cut strap: straight, with the two free corners taken off at a slant.
    /// Leather is cut, not torn, so its irregularity is in the mottle rather than the
    /// outline, and the one shape it gets has to be decisive to be seen at all.</summary>
    private static int[] ClippedEnd(int h)
    {
        var cut = new int[h];
        // Clamped to MaxEndBite so that constant is a promise the renderers can inset
        // against, not an observation that happens to hold at today's tab heights.
        int clip = Math.Clamp(h / 3, 2, MaxEndBite);
        for (int y = 0; y < h; y++)
        {
            int fromEnd = y < h - 1 - y ? y : h - 1 - y;
            cut[y] = fromEnd < clip ? clip - fromEnd : 0;
        }
        return cut;
    }

    private static void MirrorRows(int[] argb, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0, o = w - 1; x < o; x++, o--)
                (argb[row + x], argb[row + o]) = (argb[row + o], argb[row + x]);
        }
    }

    /// <summary>FNV-1a. Any stable hash would do; what matters is that it is stable.</summary>
    private static uint Hash(string s)
    {
        uint h = 2166136261u;
        foreach (char c in s) { h ^= c; h *= 16777619u; }
        return h;
    }

    private static uint Hash2(uint seed, int x, int y)
        => XorShift(seed ^ ((uint)x * 73856093u) ^ ((uint)y * 19349663u));

    /// <summary>
    /// A final mixing step before the material is picked. FNV's low bits carry too much
    /// of the last character, so a plain modulo over a real tab strip clumps hard: eight
    /// chapter names came out five cloth, two leather and one parchment. Avalanching
    /// first spreads them.
    /// </summary>
    private static uint Avalanche(uint h)
    {
        h ^= h >> 16; h *= 0x7FEB352Du;
        h ^= h >> 15; h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    private static uint XorShift(uint s)
    {
        if (s == 0) s = 0x1D872B41u;
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return s;
    }
}
