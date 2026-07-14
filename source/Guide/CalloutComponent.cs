using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A sibling callout (tip, warning, lore): the same rubricated double-border
/// frame as the author's hand, in the variant's hue, with a brighter interior
/// than the page. No seal, no heading. Drawn inline around its own wrapped
/// lines. Variant owns the color (docs/SCHEMA.md section 5).
/// </summary>
public class CalloutComponent : RichTextComponent
{
    private readonly double[] interior;
    private readonly double[] border;

    // Column extent captured in CalcBounds, for a full-width box. The box must
    // stay inside the column: the richtext surface is exactly the column wide,
    // so anything drawn past its edges is clipped off (side borders vanish and
    // the box reads as a band). The text narrows instead, like the author's hand.
    private double colLeft, colRight;

    public const double Pad = 13;
    private const double Radius = 7, OuterStroke = 2, InnerStroke = 1, RuleGap = 3;

    /// <summary>Vertical space the renderer must reserve above and below the box,
    /// so the Pad overhang (drawn past the text bounds) never collides with the
    /// next block. Sized to the larger of the two callout paddings, plus a hair.</summary>
    public const double OuterMargin = Pad + 5;

    public CalloutComponent(ICoreClientAPI api, string text, CairoFont font, double[] interior, double[] border)
        : base(api, text, font)
    {
        this.interior = interior;
        this.border = border;
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        double pad = GuiElement.scaled(Pad);

        // One constant left and right for the whole callout, so every wrapped
        // line shares the exact same indent (per-section edges can differ a few px).
        double left = double.MaxValue, right = 0;
        foreach (var f in flowPath) { if (f.X1 < left) left = f.X1; if (f.X2 > right) right = f.X2; }
        if (flowPath.Length == 0) { left = offsetX; right = offsetX; }

        colLeft = left;
        colRight = right;
        double textLeft = left + pad;
        double textRight = right - pad;

        var narrowed = new TextFlowPath[flowPath.Length];
        for (int i = 0; i < flowPath.Length; i++)
            narrowed[i] = new TextFlowPath { X1 = textLeft, Y1 = flowPath[i].Y1, X2 = textRight, Y2 = flowPath[i].Y2 };

        return base.CalcBounds(narrowed, currentLineHeight, textLeft, lineY, out nextOffsetX);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        if (BoundsPerLine == null || BoundsPerLine.Length == 0) { base.ComposeElements(ctx, surface); return; }

        double s(double v) => GuiElement.scaled(v);

        double minY = double.MaxValue, maxY = 0;
        foreach (var b in BoundsPerLine)
        {
            if (b.Y < minY) minY = b.Y;
            if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
        }

        // Box spans the full column, inset 1px so the strokes stay inside the
        // surface. Vertical pad reaches into the blank wrapping lines around the
        // block, so the text never touches the rule.
        double x = colLeft + s(1), y = minY - s(Pad);
        double w = (colRight - colLeft) - s(2), h = (maxY - minY) + s(Pad) * 2;

        RoundRect(ctx, x, y, w, h, s(Radius));
        ctx.SetSourceRGBA(interior[0], interior[1], interior[2], interior[3]);
        ctx.Fill();

        ctx.LineWidth = s(OuterStroke);
        RoundRect(ctx, x + s(OuterStroke) / 2, y + s(OuterStroke) / 2, w - s(OuterStroke), h - s(OuterStroke), s(Radius));
        ctx.SetSourceRGBA(border[0], border[1], border[2], 1);
        ctx.Stroke();

        double inset = s(OuterStroke + RuleGap);
        ctx.LineWidth = s(InnerStroke);
        RoundRect(ctx, x + inset, y + inset, w - inset * 2, h - inset * 2, Math.Max(1, s(Radius) - inset));
        ctx.SetSourceRGBA(border[0], border[1], border[2], 0.80);
        ctx.Stroke();

        base.ComposeElements(ctx, surface);
    }

    private static void RoundRect(Context ctx, double x, double y, double w, double h, double r)
    {
        ctx.NewPath();
        ctx.Arc(x + r, y + r, r, Math.PI, Math.PI * 1.5);
        ctx.Arc(x + w - r, y + r, r, Math.PI * 1.5, Math.PI * 2);
        ctx.Arc(x + w - r, y + h - r, r, 0, Math.PI * 0.5);
        ctx.Arc(x + r, y + h - r, r, Math.PI * 0.5, Math.PI);
        ctx.ClosePath();
    }
}
