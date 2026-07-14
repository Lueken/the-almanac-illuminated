using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A small inline row of ink diamonds — the stamp row of a journeyman's
/// wander-book. Filled pips are earned, the current one sits outlined, the
/// rest wait faint on the page, and the top `barred` pips are struck through
/// as an ×, tiers a hand can never reach (a walled affinity ceiling). Flows
/// inline with text (no float); follow with a newline component to break.
/// </summary>
public class InkPipsComponent : RichTextComponentBase
{
    private readonly int total;
    private readonly int filled;
    private readonly int current;
    private readonly int barred;
    private readonly double size;

    private const double UnGap = 4;

    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 0.80 };
    private static readonly double[] Outline = { 0.13, 0.09, 0.05, 0.90 };
    private static readonly double[] Faint = { 0.13, 0.09, 0.05, 0.18 };
    private static readonly double[] Barred = { 0.13, 0.09, 0.05, 0.34 };

    /// <param name="filled">Pips drawn solid (completed).</param>
    /// <param name="current">Index drawn as an outline (in progress), or -1 for none.</param>
    /// <param name="barred">Count of top pips struck through (unreachable ceiling).</param>
    public InkPipsComponent(ICoreClientAPI api, int total, int filled, int current = -1, int barred = 0, double size = 7) : base(api)
    {
        this.total = Math.Max(0, total);
        this.filled = Math.Clamp(filled, 0, this.total);
        this.current = current;
        this.barred = Math.Clamp(barred, 0, this.total);
        this.size = size;
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        double s = GuiElement.scaled(size);
        double gap = GuiElement.scaled(UnGap);
        double w = total > 0 ? total * s + (total - 1) * gap : 0;

        BoundsPerLine[0].Width = w;
        BoundsPerLine[0].Height = s;
        BoundsPerLine[0].X = cur.X1 + offsetX;
        // Plain lineY: the engine's Bottom vertical-align then shifts this by
        // (line ascent - height), landing the pips on the text baseline — which
        // is also the x-height optical center. Adding our own offset here would
        // double-shift them below the line (the 0.0.3 overlap bug).
        BoundsPerLine[0].Y = lineY;
        nextOffsetX = offsetX + w + GuiElement.scaled(6);
        return EnumCalcBoundsResult.Continue;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (b.Width < 1 || total == 0) return;
        double s = b.Height;
        double gap = GuiElement.scaled(UnGap);

        int firstBarred = total - barred;   // top `barred` pips are unreachable
        for (int i = 0; i < total; i++)
        {
            double cx = b.X + i * (s + gap) + s / 2;
            double cy = b.Y + s / 2;

            if (i >= firstBarred)
            {
                // A struck ×: this tier is beyond reach here.
                double h = s * 0.34;
                ctx.LineWidth = GuiElement.scaled(1);
                ctx.SetSourceRGBA(Barred[0], Barred[1], Barred[2], Barred[3]);
                ctx.NewPath(); ctx.MoveTo(cx - h, cy - h); ctx.LineTo(cx + h, cy + h); ctx.Stroke();
                ctx.NewPath(); ctx.MoveTo(cx + h, cy - h); ctx.LineTo(cx - h, cy + h); ctx.Stroke();
                continue;
            }

            ctx.NewPath();
            ctx.MoveTo(cx, cy - s / 2);
            ctx.LineTo(cx + s / 2, cy);
            ctx.LineTo(cx, cy + s / 2);
            ctx.LineTo(cx - s / 2, cy);
            ctx.ClosePath();

            if (i < filled)
            {
                ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], Ink[3]);
                ctx.Fill();
            }
            else if (i == current)
            {
                ctx.LineWidth = GuiElement.scaled(1);
                ctx.SetSourceRGBA(Outline[0], Outline[1], Outline[2], Outline[3]);
                ctx.Stroke();
            }
            else
            {
                ctx.SetSourceRGBA(Faint[0], Faint[1], Faint[2], Faint[3]);
                ctx.Fill();
            }
        }
    }
}
