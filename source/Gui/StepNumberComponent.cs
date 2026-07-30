using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// The mockup's circular step numeral: a small filled disc in the chapter's
/// accent color bearing a cream number. Flows inline at the head of a step
/// line; the step text follows as ordinary richtext.
/// </summary>
public class StepNumberComponent : RichTextComponentBase
{
    private readonly int number;
    private readonly double[] accent;
    private const double UnSize = 15;

    public StepNumberComponent(ICoreClientAPI api, int number, double[] accent) : base(api)
    {
        this.number = number;
        this.accent = accent;
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        double s = GuiElement.scaled(UnSize);
        BoundsPerLine[0].Width = s;
        BoundsPerLine[0].Height = s;
        BoundsPerLine[0].X = cur.X1 + offsetX + GuiElement.scaled(4);
        BoundsPerLine[0].Y = lineY;   // engine baseline-shifts, like InkPipsComponent
        nextOffsetX = offsetX + s + GuiElement.scaled(10);
        return EnumCalcBoundsResult.Continue;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (b.Width < 1) return;
        double s = b.Height;
        double cx = b.X + s / 2, cy = b.Y + s / 2;

        ctx.NewPath();
        ctx.Arc(cx, cy, s / 2, 0, Math.PI * 2);
        ctx.SetSourceRGBA(accent[0], accent[1], accent[2], 0.92);
        ctx.Fill();

        ctx.SelectFontFace(FontRegistry.SerifBody, FontSlant.Normal, FontWeight.Bold);
        ctx.SetFontSize(s * 0.72);
        string t = number.ToString();
        var ext = ctx.TextExtents(t);
        ctx.SetSourceRGBA(0.96, 0.93, 0.84, 1);
        ctx.MoveTo(cx - ext.Width / 2 - ext.XBearing, cy + ext.Height / 2);
        ctx.ShowText(t);
    }
}
