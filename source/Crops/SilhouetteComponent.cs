using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A masked stand-in for an unidentified item's icon: a dark plate bearing a "?",
/// floated left like the real itemstack so the card layout is unchanged whether the
/// growable is known or not. Used by the Crops tab under Foragers Gamble gating.
/// </summary>
public class SilhouetteComponent : RichTextComponentBase
{
    private readonly double size;

    private static readonly double[] Plate = { 0.20, 0.15, 0.10, 1.0 };
    private static readonly double[] Mark = { 0.74, 0.66, 0.52, 1.0 };

    public SilhouetteComponent(ICoreClientAPI api, double size) : base(api)
    {
        this.size = size;
        Float = EnumFloat.Left;
        VerticalAlign = EnumVerticalAlign.Top;
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        double s = GuiElement.scaled(size);
        BoundsPerLine[0].Width = s;
        BoundsPerLine[0].Height = s;
        BoundsPerLine[0].X = cur.X1;
        BoundsPerLine[0].Y = lineY;
        nextOffsetX = offsetX;
        return EnumCalcBoundsResult.Continue;   // float: following text wraps beside it
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (b.Width < 1) return;

        double r = GuiElement.scaled(4);
        RoundRect(ctx, b.X, b.Y, b.Width, b.Height, r);
        ctx.SetSourceRGBA(Plate[0], Plate[1], Plate[2], Plate[3]);
        ctx.Fill();

        var font = CairoFont.WhiteSmallishText().WithFontSize((float)(size * 0.55)).WithColor(Mark);
        font.SetupContext(ctx);
        var te = ctx.TextExtents("?");
        ctx.NewPath();
        ctx.MoveTo(b.X + b.Width / 2 - (te.Width / 2 + te.XBearing), b.Y + b.Height / 2 - (te.YBearing + te.Height / 2));
        ctx.ShowText("?");
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
