using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// The "From the Author's Hand" callout: a rubricated manuscript aside.
/// Brighter vellum interior, a double red border, a wax-seal disc bearing the
/// author's initial, and a red heading over the italic note. Drawn entirely in
/// Cairo so it stays one inline richtext component (no stacking engine).
///
/// Design values from the design-master spec (2026-06-13) reconciled with the
/// Figma mockup (rounded corners, seal at left). Numbers are unscaled; they are
/// wrapped in GuiElement.scaled at draw time.
/// </summary>
public class AuthorCalloutComponent : RichTextComponent
{
    private readonly string letter;
    private readonly CairoFont headingFont;
    private readonly CairoFont sealFont;

    private const string HeadingText = "From the Author's Hand:";

    // Geometry (unscaled px)
    private const double Pad = 11;        // text inset from inner rule
    private const double Radius = 7;       // corner rounding
    private const double OuterStroke = 2;
    private const double InnerStroke = 1;
    private const double RuleGap = 3;
    private const double SealDia = 34;
    private const double SealClear = 50;   // left clearance reserved for the seal (PaddingLeft)

    // Colors (RGBA 0..1)
    private static readonly double[] Interior = { 0.97, 0.94, 0.85, 1.0 };
    private static readonly double[] RuleOuter = { 0.72, 0.13, 0.08, 1.0 };
    private static readonly double[] RuleInner = { 0.72, 0.13, 0.08, 0.80 };
    private static readonly double[] SealFill = { 0.55, 0.12, 0.10, 1.0 };
    private static readonly double[] SealRim = { 0.38, 0.06, 0.04, 1.0 };
    private static readonly double[] SealHi = { 0.82, 0.45, 0.38, 0.60 };

    public AuthorCalloutComponent(ICoreClientAPI api, string note, CairoFont bodyFont, string initial)
        : base(api, "\n" + note, bodyFont)   // leading line reserves space for the heading
    {
        letter = initial;
        PaddingLeft = SealClear;             // first line (heading) clears the seal
        headingFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithWeight(FontWeight.Bold).WithSlant(FontSlant.Italic).WithColor(new[] { 0.62, 0.12, 0.10, 1.0 });
        sealFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(FontWeight.Bold).WithFontSize(17f).WithColor(new[] { 0.97, 0.92, 0.82, 1.0 });
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        if (BoundsPerLine == null || BoundsPerLine.Length == 0) { base.ComposeElements(ctx, surface); return; }

        double s(double v) => GuiElement.scaled(v);

        double minY = double.MaxValue, maxX = 0, maxY = 0;
        foreach (var b in BoundsPerLine)
        {
            if (b.Y < minY) minY = b.Y;
            if (b.X + b.Width > maxX) maxX = b.X + b.Width;
            if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
        }

        double x = 0;
        double y = minY - s(Pad);
        double w = maxX + s(Pad);
        double h = (maxY - minY) + s(Pad) * 2;
        if (h < s(SealDia) + s(Pad) * 2) h = s(SealDia) + s(Pad) * 2;

        // Interior fill
        RoundRect(ctx, x, y, w, h, s(Radius));
        ctx.SetSourceRGBA(Interior[0], Interior[1], Interior[2], Interior[3]);
        ctx.Fill();

        // Double red rule
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

        // Heading, on the reserved first line, right of the seal
        double headBaseline = minY + s(13);
        headingFont.SetupContext(ctx);
        ctx.MoveTo(s(SealClear), headBaseline);
        ctx.ShowText(HeadingText);

        // Wax seal, left, vertically centered on the box
        double cx = x + s(Pad) + s(SealDia) / 2;
        double cy = y + h / 2;
        double r = s(SealDia) / 2;

        ctx.Arc(cx, cy, r, 0, Math.PI * 2);
        ctx.SetSourceRGBA(SealFill[0], SealFill[1], SealFill[2], SealFill[3]);
        ctx.FillPreserve();
        ctx.LineWidth = s(1.5);
        ctx.SetSourceRGBA(SealRim[0], SealRim[1], SealRim[2], 1);
        ctx.Stroke();

        // Highlight arc, top-left, catching candlelight
        ctx.Arc(cx, cy, r - s(2), Math.PI * 0.75, Math.PI * 1.25);
        ctx.LineWidth = s(1);
        ctx.SetSourceRGBA(SealHi[0], SealHi[1], SealHi[2], SealHi[3]);
        ctx.Stroke();

        // Initial, centered in the disc
        sealFont.SetupContext(ctx);
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
