using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>Which way the Crops tab records what has been grown.</summary>
public enum GrowthMarksStyle
{
    /// <summary>A five-bar gate per five harvests, struck in the margin as they happened.</summary>
    Tally,
    /// <summary>A single rule inked as far as the reader has come.</summary>
    Fullness,
    /// <summary>Plain counts, in words. Drawn by the renderer, not by this component.</summary>
    Counts,
}

/// <summary>
/// The record of what a reader has grown, struck by hand in the margin of a crop's card.
///
/// Full-height marks are harvests of this crop. Half-height marks are what its kin have
/// carried across: the same ink, half the claim, which is exactly what family spread is.
/// The gate closes at the Versed threshold, so a complete row of fives means there is
/// nothing further this plant can teach.
///
/// Every mark is jittered in angle, length and placement from a seed made of the crop's
/// own id, so each plant's tally looks written rather than printed, and looks the SAME
/// every time the page is turned back to. A tally that reshuffles on each redraw reads as
/// a machine drawing a tally, which is the one thing it must not look like.
/// </summary>
public class GrowthMarksComponent : RichTextComponentBase
{
    private readonly GrowthMarksStyle style;
    private readonly int whole;
    private readonly int half;
    private readonly int capacity;
    private readonly double fraction;
    private readonly uint seed;
    private readonly double size;

    private const int PerGate = 5;
    private const double UnStride = 4.6;     // between marks within a gate
    private const double UnGateGap = 5.6;    // between gates
    private const double UnRuleWidth = 46;   // the fullness rule

    // Struck in full ink. The first pass drew these at the weight of a hairline rule and
    // they read as smudges on the parchment: a tally is the loudest thing on the card,
    // because it is the one part the reader put there.
    private static readonly double[] Ink = { 0.11, 0.07, 0.04, 1.00 };
    private static readonly double[] Kin = { 0.13, 0.09, 0.05, 0.68 };
    private static readonly double[] Faint = { 0.13, 0.09, 0.05, 0.16 };

    /// <param name="cropId">Seeds the hand. Any stable per-crop string.</param>
    /// <param name="whole">Marks at full height: this crop's own harvests.</param>
    /// <param name="half">Marks at half height: what the kin carried across.</param>
    /// <param name="capacity">Marks the gate holds before it is full (the Versed threshold).</param>
    /// <param name="fraction">Fullness mode only: 0 to 1 of the way to Versed.</param>
    public GrowthMarksComponent(ICoreClientAPI api, GrowthMarksStyle style, string cropId,
        int whole, int half, int capacity, double fraction, double size = 13) : base(api)
    {
        this.style = style;
        this.capacity = Math.Max(1, capacity);
        this.whole = Math.Clamp(whole, 0, this.capacity);
        this.half = Math.Clamp(half, 0, this.capacity - this.whole);
        this.fraction = Math.Clamp(fraction, 0, 1);
        this.size = size;
        seed = Hash(cropId);
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    /// <summary>FNV-1a. Any stable hash would do; this one is short and has no
    /// process-wide randomised seed to make the page differ between launches.</summary>
    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
        return h == 0 ? 1u : h;
    }

    /// <summary>One deterministic value in -1..1 for (mark, channel).</summary>
    private double Wobble(int index, int channel)
    {
        uint x = seed ^ (uint)(index * 2654435761) ^ (uint)(channel * 40503);
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return (x % 20001) / 10000.0 - 1.0;
    }

    private int MarkCount => Math.Min(capacity, whole + half);

    private double TallyWidth(double s)
    {
        int n = MarkCount;
        if (n == 0) return 0;
        int gates = (n + PerGate - 1) / PerGate;
        // A gate of five is four uprights wide; the fifth is struck across them.
        int uprights = 0;
        for (int g = 0; g < gates; g++)
        {
            int inGate = Math.Min(PerGate, n - g * PerGate);
            uprights += Math.Min(inGate, PerGate - 1);
        }
        return uprights * GuiElement.scaled(UnStride) + (gates - 1) * GuiElement.scaled(UnGateGap) + s * 0.5;
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        double s = GuiElement.scaled(size);
        double w = style == GrowthMarksStyle.Fullness ? GuiElement.scaled(UnRuleWidth) : TallyWidth(s);

        BoundsPerLine[0].Width = w;
        BoundsPerLine[0].Height = s;
        BoundsPerLine[0].X = cur.X1 + offsetX;
        // Plain lineY: the engine's Bottom vertical-align lands this on the text baseline.
        // Adding an offset here would double-shift it (the pips' 0.0.3 overlap bug).
        BoundsPerLine[0].Y = lineY;
        nextOffsetX = offsetX + w + GuiElement.scaled(6);
        return EnumCalcBoundsResult.Continue;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (b.Width < 1) return;
        if (style == GrowthMarksStyle.Fullness) { DrawRule(ctx, b); return; }
        DrawTally(ctx, b);
    }

    private void DrawTally(Context ctx, LineRectangled b)
    {
        int n = MarkCount;
        if (n == 0) return;

        double s = b.Height;
        double stride = GuiElement.scaled(UnStride);
        double gateGap = GuiElement.scaled(UnGateGap);
        ctx.LineWidth = GuiElement.scaled(1.8);
        ctx.LineCap = LineCap.Round;

        double x = b.X + s * 0.25;
        double baseY = b.Y + s;

        for (int i = 0; i < n; i++)
        {
            bool kin = i >= whole;
            double h = s * (kin ? 0.55 : 1.0);
            var col = kin ? Kin : Ink;
            ctx.SetSourceRGBA(col[0], col[1], col[2], col[3]);

            int inGate = i % PerGate;
            if (inGate == PerGate - 1)
            {
                // The fifth: struck across the four already standing, low left to high right.
                double span = 3 * stride;
                double lean = s * 0.12 * Wobble(i, 3);
                ctx.NewPath();
                ctx.MoveTo(x - span - stride * 0.55, baseY - h * 0.12 + lean);
                ctx.LineTo(x - stride * 0.15, baseY - h * 0.92 + lean);
                ctx.Stroke();
                x += gateGap;
                continue;
            }

            double tilt = s * 0.10 * Wobble(i, 0);
            double top = h * (0.92 + 0.08 * Wobble(i, 1));
            double jog = stride * 0.16 * Wobble(i, 2);
            ctx.NewPath();
            ctx.MoveTo(x + jog, baseY);
            ctx.LineTo(x + jog + tilt, baseY - top);
            ctx.Stroke();
            x += stride;
        }
    }

    /// <summary>
    /// The fullness rule: a faint full run for what the plant has to teach, inked over as
    /// far as the reader has come. Drawn with a wavering top edge so it reads as a stroke
    /// laid down by a pen rather than a bar filled by a machine.
    /// </summary>
    private void DrawRule(Context ctx, LineRectangled b)
    {
        double s = b.Height;
        double y = b.Y + s * 0.72;
        double thick = Math.Max(1.5, s * 0.24);

        ctx.LineWidth = thick;
        ctx.LineCap = LineCap.Round;

        ctx.SetSourceRGBA(Faint[0], Faint[1], Faint[2], Faint[3]);
        ctx.NewPath(); ctx.MoveTo(b.X, y); ctx.LineTo(b.X + b.Width, y); ctx.Stroke();

        double inked = b.Width * fraction;
        if (inked <= 0.5) return;

        // Segment the inked run so each short stroke can sit a hair above or below the
        // line, the way a nib wanders.
        const int segs = 7;
        ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], Ink[3]);
        for (int i = 0; i < segs; i++)
        {
            double x0 = b.X + inked * i / segs;
            double x1 = b.X + inked * (i + 1) / segs;
            if (x1 - x0 < 0.4) continue;
            double dy = thick * 0.35 * Wobble(i, 7);
            ctx.NewPath();
            ctx.MoveTo(x0, y + dy);
            ctx.LineTo(x1, y + thick * 0.35 * Wobble(i + 1, 7));
            ctx.Stroke();
        }
    }
}
