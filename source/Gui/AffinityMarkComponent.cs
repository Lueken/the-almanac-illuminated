using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A small ink star set beside a name to mark a natural aptitude: filled for a
/// strong calling, outlined for a lesser one. A four-point star, distinct from
/// the diamond tier-pips, so "this trade is yours" reads at a glance without
/// touching the rank line. Flows inline; place it right after the name.
/// </summary>
public class AffinityMarkComponent : RichTextComponentBase
{
    private readonly bool filled;
    private readonly double size;

    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 0.85 };

    public AffinityMarkComponent(ICoreClientAPI api, bool filled, double size = 9) : base(api)
    {
        this.filled = filled;
        this.size = size;
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        double s = GuiElement.scaled(size);
        double lead = GuiElement.scaled(4);   // small gap after the name

        BoundsPerLine[0].Width = s + lead;
        BoundsPerLine[0].Height = s;
        BoundsPerLine[0].X = cur.X1 + offsetX + lead;
        BoundsPerLine[0].Y = lineY;
        nextOffsetX = offsetX + s + lead + GuiElement.scaled(2);
        return EnumCalcBoundsResult.Continue;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (b.Width < 1) return;

        double s = b.Height;
        double cx = b.X + (s / 2);
        double cy = b.Y + s / 2;
        double r = s / 2;
        double ri = r * 0.40;   // inner radius: the star's waist

        ctx.NewPath();
        for (int i = 0; i < 8; i++)
        {
            double theta = -Math.PI / 2 + i * Math.PI / 4;   // start at the top, step 45°
            double rad = (i % 2 == 0) ? r : ri;
            double x = cx + rad * Math.Cos(theta);
            double y = cy + rad * Math.Sin(theta);
            if (i == 0) ctx.MoveTo(x, y); else ctx.LineTo(x, y);
        }
        ctx.ClosePath();

        ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], Ink[3]);
        if (filled)
        {
            ctx.Fill();
        }
        else
        {
            ctx.LineWidth = GuiElement.scaled(1);
            ctx.Stroke();
        }
    }
}
