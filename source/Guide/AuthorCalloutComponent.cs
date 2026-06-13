using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// The "From the Author's Hand" callout: a rubricated manuscript aside.
/// Brighter vellum interior, a double red border, a wax-seal disc bearing the
/// author's initial, and a red heading over the italic note. Drawn entirely in
/// Cairo so it stays one inline richtext component.
///
/// Every text line is indented past the seal by narrowing the flow path in
/// CalcBounds (PaddingLeft only indents the first line). Design values from the
/// design-master spec (2026-06-13) reconciled with the Figma mockup.
/// </summary>
public class AuthorCalloutComponent : RichTextComponent
{
    private readonly string letter;
    private readonly CairoFont headingFont;
    private readonly CairoFont sealFont;

    // Column extent captured in CalcBounds, for a full-width box.
    private double colLeft, colRight;

    private const string HeadingText = "From the Author's Hand:";

    private const double Pad = 11;
    private const double Radius = 7;
    private const double OuterStroke = 2;
    private const double InnerStroke = 1;
    private const double RuleGap = 3;
    private const double SealDia = 34;
    private const double SealClear = 52;   // left column reserved for the seal

    private static readonly double[] Interior = { 0.97, 0.94, 0.85, 1.0 };
    private static readonly double[] RuleOuter = { 0.72, 0.13, 0.08, 1.0 };
    private static readonly double[] RuleInner = { 0.72, 0.13, 0.08, 0.80 };
    private static readonly double[] SealFill = { 0.55, 0.12, 0.10, 1.0 };
    private static readonly double[] SealRim = { 0.38, 0.06, 0.04, 1.0 };
    private static readonly double[] SealHi = { 0.82, 0.45, 0.38, 0.60 };

    public AuthorCalloutComponent(ICoreClientAPI api, string note, CairoFont bodyFont, string initial)
        // Two leading blank lines (top padding + heading row) and a trailing
        // blank line (bottom padding). The blanks extend the surface so the box
        // never draws outside it, and give even breathing room top and bottom.
        : base(api, "\n\n" + note + "\n", bodyFont)
    {
        letter = initial;
        headingFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithWeight(FontWeight.Bold).WithSlant(FontSlant.Italic).WithColor(new[] { 0.62, 0.12, 0.10, 1.0 });
        sealFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(FontWeight.Bold).WithFontSize(17f).WithColor(new[] { 0.97, 0.92, 0.82, 1.0 });
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        double clear = GuiElement.scaled(SealClear);
        double rpad = GuiElement.scaled(Pad);

        // One constant left and right for the whole callout, so every wrapped
        // line shares the exact same indent (per-section edges can differ a few px).
        double left = double.MaxValue, right = 0;
        foreach (var f in flowPath) { if (f.X1 < left) left = f.X1; if (f.X2 > right) right = f.X2; }
        if (flowPath.Length == 0) { left = offsetX; right = offsetX; }

        colLeft = left;
        colRight = right;
        double textLeft = left + clear;
        double textRight = right - rpad;

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

        // Box spans the full column (consistent rectangle) and the line span
        // (blank lines supply the inner padding), inset 1px so strokes stay in.
        double x = colLeft + s(1);
        double y = minY + s(1);
        double w = (colRight - colLeft) - s(2);
        double h = (maxY - minY) - s(2);

        ctx.NewPath();
        RoundRect(ctx, x, y, w, h, s(Radius));
        ctx.SetSourceRGBA(Interior[0], Interior[1], Interior[2], Interior[3]);
        ctx.Fill();

        ctx.LineWidth = s(OuterStroke);
        RoundRect(ctx, x + s(OuterStroke) / 2, y + s(OuterStroke) / 2, w - s(OuterStroke), h - s(OuterStroke), s(Radius));
        ctx.SetSourceRGBA(RuleOuter[0], RuleOuter[1], RuleOuter[2], RuleOuter[3]);
        ctx.Stroke();

        double inset = s(OuterStroke + RuleGap);
        ctx.LineWidth = s(InnerStroke);
        RoundRect(ctx, x + inset, y + inset, w - inset * 2, h - inset * 2, Math.Max(1, s(Radius) - inset));
        ctx.SetSourceRGBA(RuleInner[0], RuleInner[1], RuleInner[2], RuleInner[3]);
        ctx.Stroke();

        // Body text (and the empty reserved first line)
        base.ComposeElements(ctx, surface);

        // Red heading on the reserved first line, in the text column
        // Heading sits on the second line (the first is top padding).
        double headY = BoundsPerLine.Length > 1 ? BoundsPerLine[1].Y : minY;
        headingFont.SetupContext(ctx);
        ctx.NewPath();
        ctx.MoveTo(colLeft + s(SealClear), headY + s(15));
        ctx.ShowText(HeadingText);

        // Wax seal in the left column, vertically centered on the box
        double cx = x + s(Pad) + s(SealDia) / 2;
        double cy = y + h / 2;
        double r = s(SealDia) / 2;

        ctx.NewPath();
        ctx.Arc(cx, cy, r, 0, Math.PI * 2);
        ctx.SetSourceRGBA(SealFill[0], SealFill[1], SealFill[2], SealFill[3]);
        ctx.FillPreserve();
        ctx.LineWidth = s(1.5);
        ctx.SetSourceRGBA(SealRim[0], SealRim[1], SealRim[2], 1);
        ctx.Stroke();

        ctx.NewPath();
        ctx.Arc(cx, cy, r - s(2), Math.PI * 0.75, Math.PI * 1.25);
        ctx.LineWidth = s(1);
        ctx.SetSourceRGBA(SealHi[0], SealHi[1], SealHi[2], SealHi[3]);
        ctx.Stroke();

        sealFont.SetupContext(ctx);
        ctx.NewPath();
        var te = ctx.TextExtents(letter);
        ctx.MoveTo(cx - (te.Width / 2 + te.XBearing), cy - (te.YBearing + te.Height / 2));
        ctx.ShowText(letter);
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
