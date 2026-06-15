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

    private const double Pad = 13, Radius = 7, OuterStroke = 2, InnerStroke = 1, RuleGap = 3;

    public CalloutComponent(ICoreClientAPI api, string text, CairoFont font, double[] interior, double[] border)
        : base(api, text, font)
    {
        this.interior = interior;
        this.border = border;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        if (BoundsPerLine == null || BoundsPerLine.Length == 0) { base.ComposeElements(ctx, surface); return; }

        double s(double v) => GuiElement.scaled(v);

        double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
        foreach (var b in BoundsPerLine)
        {
            if (b.X < minX) minX = b.X;
            if (b.Y < minY) minY = b.Y;
            if (b.X + b.Width > maxX) maxX = b.X + b.Width;
            if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
        }

        // Pad on every side so the text never touches the rule. The box reaches a
        // little into the page margin left and right, which the cream inset absorbs.
        double x = minX - s(Pad), y = minY - s(Pad);
        double w = (maxX - minX) + s(Pad) * 2, h = (maxY - minY) + s(Pad) * 2;

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
