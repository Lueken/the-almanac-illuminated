using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// An ornamental rule: a thin centred ink line broken by a small diamond at its
/// middle. Drawn, never typed, so it never depends on a font glyph — the old
/// three-em-dash text rendered as tofu boxes in the serif face. Spans the
/// column and reserves its own height; floats left like the other block
/// components, so follow it with a line break to continue below.
/// </summary>
public class DividerComponent : RichTextComponentBase
{
    private readonly double height;

    private static readonly double[] Ink = { 0.28, 0.18, 0.10, 0.55 };

    public DividerComponent(ICoreClientAPI api, double height = 16) : base(api)
    {
        this.height = height;
        Float = EnumFloat.Left;
        VerticalAlign = EnumVerticalAlign.Top;
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        BoundsPerLine[0].X = cur.X1;
        BoundsPerLine[0].Y = lineY;
        BoundsPerLine[0].Width = cur.X2 - cur.X1;
        BoundsPerLine[0].Height = GuiElement.scaled(height);
        nextOffsetX = offsetX;
        return EnumCalcBoundsResult.Continue;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (b.Width < 1) return;

        double cy = b.Y + b.Height / 2;
        double cx = b.X + b.Width / 2;
        double diamond = GuiElement.scaled(4);
        double gap = GuiElement.scaled(16);      // clear space either side of the diamond
        double margin = GuiElement.scaled(10);   // rule stops short of the column edges

        ctx.LineWidth = GuiElement.scaled(1);
        ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], Ink[3]);

        ctx.NewPath();
        ctx.MoveTo(b.X + margin, cy);
        ctx.LineTo(cx - gap, cy);
        ctx.Stroke();

        ctx.NewPath();
        ctx.MoveTo(cx + gap, cy);
        ctx.LineTo(b.X + b.Width - margin, cy);
        ctx.Stroke();

        ctx.NewPath();
        ctx.MoveTo(cx, cy - diamond);
        ctx.LineTo(cx + diamond, cy);
        ctx.LineTo(cx, cy + diamond);
        ctx.LineTo(cx - diamond, cy);
        ctx.ClosePath();
        ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.75);
        ctx.Fill();
    }
}
