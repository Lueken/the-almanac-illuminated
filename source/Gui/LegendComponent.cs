using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A small right-aligned key: a header over rows of "glyph + label", every row's
/// text flush to the right edge of the column with its mark just to its left.
/// Drawn as one piece so the marks (the affinity star, the barred pip) move with
/// the text — ordinary text alignment leaves custom inline glyphs behind.
/// </summary>
public class LegendComponent : RichTextComponentBase
{
    public enum Glyph { GiftedStar, FavoredStar, BarredPip }

    private readonly string header;
    private readonly (Glyph glyph, string text)[] rows;
    private readonly CairoFont headFont;
    private readonly CairoFont bodyFont;

    private const double RowUn = 18;     // unscaled row height
    private const double GlyphUn = 9;    // unscaled glyph box
    private const double GapUn = 5;      // gap between glyph and text

    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 0.85 };
    private static readonly double[] Faint = { 0.13, 0.09, 0.05, 0.40 };

    public LegendComponent(ICoreClientAPI api, string header, (Glyph, string)[] rows, CairoFont headFont, CairoFont bodyFont) : base(api)
    {
        this.header = header;
        this.rows = rows;
        this.headFont = headFont;
        this.bodyFont = bodyFont;
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        BoundsPerLine[0].X = cur.X1;
        BoundsPerLine[0].Y = lineY;
        BoundsPerLine[0].Width = cur.X2 - cur.X1;
        BoundsPerLine[0].Height = GuiElement.scaled(RowUn) * (rows.Length + 1);
        nextOffsetX = offsetX;
        return EnumCalcBoundsResult.Continue;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (b.Width < 1) return;

        double right = b.X + b.Width;
        double rowH = GuiElement.scaled(RowUn);
        double glyph = GuiElement.scaled(GlyphUn);
        double gap = GuiElement.scaled(GapUn);

        // Header, right-aligned on the first row.
        headFont.SetupContext(ctx);
        var he = ctx.TextExtents(header);
        double hBase = b.Y + rowH * 0.75;
        ctx.MoveTo(right - he.Width - he.XBearing, hBase);
        ctx.ShowText(header);

        for (int i = 0; i < rows.Length; i++)
        {
            double baseY = b.Y + rowH * (i + 1) + rowH * 0.75;
            bodyFont.SetupContext(ctx);
            var te = ctx.TextExtents(rows[i].text);
            double textX = right - te.Width - te.XBearing;
            ctx.MoveTo(textX, baseY);
            ctx.ShowText(rows[i].text);

            double gcx = textX - gap - glyph / 2;
            double gcy = baseY - GuiElement.scaled(4);   // sit the mark on the text's middle
            DrawGlyph(ctx, rows[i].glyph, gcx, gcy, glyph);
        }
    }

    private static void DrawGlyph(Context ctx, Glyph g, double cx, double cy, double s)
    {
        double r = s / 2;
        if (g == Glyph.BarredPip)
        {
            double h = s * 0.34;
            ctx.LineWidth = GuiElement.scaled(1);
            ctx.SetSourceRGBA(Faint[0], Faint[1], Faint[2], Faint[3]);
            ctx.NewPath(); ctx.MoveTo(cx - h, cy - h); ctx.LineTo(cx + h, cy + h); ctx.Stroke();
            ctx.NewPath(); ctx.MoveTo(cx + h, cy - h); ctx.LineTo(cx - h, cy + h); ctx.Stroke();
            return;
        }

        double ri = r * 0.40;
        ctx.NewPath();
        for (int i = 0; i < 8; i++)
        {
            double theta = -Math.PI / 2 + i * Math.PI / 4;
            double rad = (i % 2 == 0) ? r : ri;
            double x = cx + rad * Math.Cos(theta);
            double y = cy + rad * Math.Sin(theta);
            if (i == 0) ctx.MoveTo(x, y); else ctx.LineTo(x, y);
        }
        ctx.ClosePath();
        ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], Ink[3]);
        if (g == Glyph.GiftedStar)
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
