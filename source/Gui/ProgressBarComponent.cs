using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// An inked progress bar drawn on the parchment: a thin rounded track with a
/// sepia fill, sized in unscaled GUI units. Floats left like the other block
/// components — follow it with a ClearFloatTextComponent to continue below.
/// </summary>
public class ProgressBarComponent : RichTextComponentBase
{
    private readonly double width;
    private readonly double height;
    private readonly double fraction;
    private readonly double inkScale;
    private readonly double pendingFraction;

    private static readonly double[] Track = { 0.13, 0.09, 0.05, 0.10 };
    private static readonly double[] Fill = { 0.36, 0.23, 0.11, 0.85 };
    private static readonly double[] FillEdge = { 0.20, 0.12, 0.05, 0.90 };
    private static readonly double[] Border = { 0.17, 0.11, 0.07, 0.90 };
    private static readonly double[] PendingWash = { 0.36, 0.23, 0.11, 0.30 };

    /// <param name="inkScale">Multiplier on the track/border alphas — pass below 1
    /// to let an empty, untouched bar recede into the page.</param>
    /// <param name="pendingFraction">A faint pencil wash drawn beyond the inked fill:
    /// work done today that settles at rest. Clamped so ink plus wash never overflow.</param>
    public ProgressBarComponent(ICoreClientAPI api, double fraction, double width, double height = 9, double inkScale = 1.0, double pendingFraction = 0) : base(api)
    {
        this.fraction = Math.Clamp(fraction, 0, 1);
        this.pendingFraction = Math.Clamp(pendingFraction, 0, 1 - this.fraction);
        this.width = width;
        this.height = height;
        this.inkScale = Math.Clamp(inkScale, 0, 1);
        Float = EnumFloat.Left;
        VerticalAlign = EnumVerticalAlign.Top;
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        BoundsPerLine[0].Width = Math.Min(GuiElement.scaled(width), cur.X2 - cur.X1);
        BoundsPerLine[0].Height = GuiElement.scaled(height);
        BoundsPerLine[0].X = cur.X1;
        BoundsPerLine[0].Y = lineY + GuiElement.scaled(2);
        nextOffsetX = offsetX;
        return EnumCalcBoundsResult.Continue;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (b.Width < 1) return;
        double r = Math.Min(GuiElement.scaled(3), b.Height / 2);

        RoundRect(ctx, b.X, b.Y, b.Width, b.Height, r);
        ctx.SetSourceRGBA(Track[0], Track[1], Track[2], Track[3] * inkScale);
        ctx.Fill();

        double fw = b.Width * fraction;

        // Pending wash under the ink: today's unsettled practice, drawn first so the
        // solid fill overprints its own span and only the beyond-ink stretch shows.
        double pw = b.Width * pendingFraction;
        if (pw > 0 && fw + pw >= r * 2)
        {
            RoundRect(ctx, b.X, b.Y, fw + pw, b.Height, r);
            ctx.SetSourceRGBA(PendingWash[0], PendingWash[1], PendingWash[2], PendingWash[3]);
            ctx.Fill();
        }

        if (fw >= r * 2)
        {
            RoundRect(ctx, b.X, b.Y, fw, b.Height, r);
            ctx.SetSourceRGBA(Fill[0], Fill[1], Fill[2], Fill[3]);
            ctx.Fill();

            // Ink pooling against the rule: a darker hairline along the top of
            // the fill, so the bar reads as drawn rather than rendered.
            if (fw > r * 2 + GuiElement.scaled(2))
            {
                ctx.NewPath();
                ctx.MoveTo(b.X + r, b.Y + GuiElement.scaled(1));
                ctx.LineTo(b.X + fw - r, b.Y + GuiElement.scaled(1));
                ctx.LineWidth = GuiElement.scaled(1);
                ctx.SetSourceRGBA(FillEdge[0], FillEdge[1], FillEdge[2], FillEdge[3]);
                ctx.Stroke();
            }
        }

        RoundRect(ctx, b.X, b.Y, b.Width, b.Height, r);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.SetSourceRGBA(Border[0], Border[1], Border[2], Border[3] * inkScale);
        ctx.Stroke();
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
