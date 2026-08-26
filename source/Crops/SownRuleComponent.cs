using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// The family break on the Crops tab: a thin ink rule carrying three small seeds at
/// its middle, spaced like grain dropped down a drill row.
///
/// Deliberately NOT <see cref="DividerComponent"/>, which is the Callings tab's rule
/// and carries a single diamond. The two tabs group different things, so they should
/// not separate their groups with the same mark: a reader who learns the diamond means
/// "a strand of callings" should not meet it again meaning "a family of plants". Same
/// mechanism, own mark.
///
/// Drawn rather than typed, for the same reason the diamond rule is: em-dash rules came
/// back as tofu boxes in the serif face, which is why these components exist at all.
///
/// Floats left and reserves its own height, so follow it with a line break to continue
/// underneath.
/// </summary>
public class SownRuleComponent : RichTextComponentBase
{
    private readonly double height;

    // The same ink the Callings rule uses, so the two tabs sit at one weight on the page
    // even while their marks differ.
    private static readonly double[] Ink = { 0.28, 0.18, 0.10, 0.55 };

    public SownRuleComponent(ICoreClientAPI api, double height = 12) : base(api)
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
        double seed = GuiElement.scaled(1.7);
        double step = GuiElement.scaled(7);       // spacing between seeds along the row
        double gap = GuiElement.scaled(18);       // clear space either side of the sown run
        double margin = GuiElement.scaled(10);    // rule stops short of the column edges

        // The rule runs from the left margin only. A family name is read left to right and
        // the seeds fall after it, so the row reads as sown rather than as a centred ornament.
        double sowStart = b.X + b.Width * 0.42;

        ctx.LineWidth = GuiElement.scaled(1);
        ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], Ink[3]);

        ctx.NewPath();
        ctx.MoveTo(b.X + margin, cy);
        ctx.LineTo(sowStart - gap * 0.5, cy);
        ctx.Stroke();

        ctx.NewPath();
        ctx.MoveTo(sowStart + step * 2 + gap * 0.5, cy);
        ctx.LineTo(b.X + b.Width - margin, cy);
        ctx.Stroke();

        // Three seeds, sitting ON the row rather than breaking it, because a drill drops
        // seed into the furrow it has already cut.
        ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.8);
        for (int i = 0; i < 3; i++)
        {
            ctx.NewPath();
            ctx.Arc(sowStart + step * i, cy, seed, 0, System.Math.PI * 2);
            ctx.Fill();
        }
    }
}
