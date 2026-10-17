using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A DECISION the reader makes, framed as a moment rather than a paragraph: the rubricated
/// double border and wax seal of the author's hand, a heading, the body of the consequence, and
/// the choices themselves drawn inside the same box.
///
/// WHY THIS IS ONE COMPONENT RATHER THAN A FRAME AROUND SEVERAL. Vintage Story's rich text lays
/// out components in sequence, and a component may only narrow its OWN flow path - CalloutComponent
/// works precisely because it owns every line it draws around. There is no way for a frame
/// component to indent separate BookLinkComponents that follow it, so a box drawn around loose
/// links would either clip at the column edge or have its text run under its own border. The
/// actions therefore belong to the panel: their rectangles are computed where they are drawn, and
/// hit-tested against the same coordinate space the richtext element hands to OnMouseDown
/// (GuiElementRichtext subtracts Bounds.absX/absY before dispatching, which is exactly the space
/// BoundsPerLine is expressed in).
///
/// The caller reserves <see cref="OuterMarginTop"/> above and <see cref="OuterMarginBottom"/> below
/// with ClearFloatTextComponents, the same contract CalloutComponent carries: the box is drawn
/// past the text bounds, so without that reservation it collides with the block after it.
/// </summary>
public class DecisionPanelComponent : RichTextComponent
{
    /// <summary>One choice in the panel: what it says, and what it does.</summary>
    public sealed class Choice
    {
        public readonly string Label;
        public readonly Action OnPick;
        /// <summary>Drawn in the muted hue and set apart, for the way out. The committing choice
        /// should be the emphatic one, so a reader who is only browsing is never nudged into it.</summary>
        public readonly bool Quiet;

        public Choice(string label, Action onPick, bool quiet = false)
        {
            Label = label;
            OnPick = onPick;
            Quiet = quiet;
        }
    }

    private readonly string heading;
    private readonly string? sealLetter;
    private readonly CairoFont headingFont;
    private readonly CairoFont sealFont;
    private readonly CairoFont choiceFont;
    private readonly CairoFont quietFont;
    private readonly Choice[] choices;
    private readonly double[] interior, rule, sealFill, sealRim, sealHi;

    private double colLeft, colRight;

    // Hit rectangles for the choices, in richtext-local space, rebuilt every compose.
    private readonly List<(double X, double Y, double W, double H, Choice C)> hits = new();

    private const double Pad = 13;
    private const double Radius = 7, OuterStroke = 2, InnerStroke = 1, RuleGap = 3;
    private const double SealDia = 34, SealClear = 52;
    private const double HeadRow = 26;       // reserved inside the box, above the body
    private const double ChoiceRow = 34;     // reserved inside the box, below the body
    private const double ChoiceGap = 18, ChoicePadX = 12, ChoicePadY = 6;

    public const double OuterMarginTop = Pad + HeadRow + 6;

    /// <summary>Space the caller must reserve BELOW, which depends on whether this panel actually
    /// offers choices: a sealed panel stating a settled fact has no choice row to leave room for,
    /// and reserving one anyway leaves a visible empty band inside the box.</summary>
    public double ReserveBelow => Pad + (choices.Length > 0 ? ChoiceRow : 0) + 6;

    public DecisionPanelComponent(ICoreClientAPI api, string bodyText, CairoFont bodyFont,
        string heading, CairoFont headingFont, CairoFont choiceFont, CairoFont quietFont,
        string? sealLetter, double[] interior, double[] rule,
        double[] sealFill, double[] sealRim, double[] sealHi, params Choice[] choices)
        : base(api, bodyText, bodyFont)
    {
        this.heading = heading;
        this.headingFont = headingFont;
        this.choiceFont = choiceFont;
        this.quietFont = quietFont;
        this.sealLetter = sealLetter;
        this.interior = interior;
        this.rule = rule;
        this.sealFill = sealFill;
        this.sealRim = sealRim;
        this.sealHi = sealHi;
        this.choices = choices ?? Array.Empty<Choice>();
        sealFont = headingFont.Clone().WithFontSize(19f);
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight,
        double offsetX, double lineY, out double nextOffsetX)
    {
        double pad = GuiElement.scaled(Pad);
        double clear = GuiElement.scaled(sealLetter == null ? Pad : SealClear);

        double left = double.MaxValue, right = 0;
        foreach (var f in flowPath) { if (f.X1 < left) left = f.X1; if (f.X2 > right) right = f.X2; }
        if (flowPath.Length == 0) { left = offsetX; right = offsetX; }

        colLeft = left;
        colRight = right;
        double textLeft = left + clear;
        double textRight = right - pad;

        var narrowed = new TextFlowPath[flowPath.Length];
        for (int i = 0; i < flowPath.Length; i++)
            narrowed[i] = new TextFlowPath { X1 = textLeft, Y1 = flowPath[i].Y1, X2 = textRight, Y2 = flowPath[i].Y2 };

        return base.CalcBounds(narrowed, currentLineHeight, textLeft, lineY, out nextOffsetX);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        hits.Clear();
        if (BoundsPerLine == null || BoundsPerLine.Length == 0) { base.ComposeElements(ctx, surface); return; }

        double s(double v) => GuiElement.scaled(v);

        double minY = double.MaxValue, maxY = 0;
        foreach (var b in BoundsPerLine)
        {
            if (b.Y < minY) minY = b.Y;
            if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
        }

        double x = colLeft + s(1);
        double y = minY - s(Pad) - s(HeadRow);
        double w = (colRight - colLeft) - s(2);
        double h = (maxY - minY) + s(Pad) * 2 + s(HeadRow)
                   + (choices.Length > 0 ? s(ChoiceRow) : 0);

        RoundRect(ctx, x, y, w, h, s(Radius));
        ctx.SetSourceRGBA(interior[0], interior[1], interior[2], interior[3]);
        ctx.Fill();

        ctx.LineWidth = s(OuterStroke);
        RoundRect(ctx, x + s(OuterStroke) / 2, y + s(OuterStroke) / 2, w - s(OuterStroke), h - s(OuterStroke), s(Radius));
        ctx.SetSourceRGBA(rule[0], rule[1], rule[2], 1);
        ctx.Stroke();

        double inset = s(OuterStroke + RuleGap);
        ctx.LineWidth = s(InnerStroke);
        RoundRect(ctx, x + inset, y + inset, w - inset * 2, h - inset * 2, Math.Max(1, s(Radius) - inset));
        ctx.SetSourceRGBA(rule[0], rule[1], rule[2], 0.80);
        ctx.Stroke();

        // Heading, on the reserved row above the body and past the seal.
        double textLeft = colLeft + s(sealLetter == null ? Pad : SealClear);
        headingFont.SetupContext(ctx);
        ctx.SetSourceRGBA(rule[0], rule[1], rule[2], 1);
        ctx.MoveTo(textLeft, y + s(Pad) + s(HeadRow) - s(8));
        ctx.ShowText(heading);

        if (sealLetter != null) DrawSeal(ctx, s, x, y, h);

        // Body text, then the choices on the reserved row at the foot of the box.
        base.ComposeElements(ctx, surface);
        DrawChoices(ctx, s, textLeft, maxY + s(Pad));
    }

    private void DrawSeal(Context ctx, Func<double, double> s, double x, double y, double h)
    {
        double r = s(SealDia) / 2;
        double cx = x + s(Pad) + r;
        double cy = y + h / 2;

        ctx.NewPath();
        ctx.Arc(cx, cy, r, 0, Math.PI * 2);
        ctx.SetSourceRGBA(sealFill[0], sealFill[1], sealFill[2], sealFill[3]);
        ctx.FillPreserve();
        ctx.LineWidth = s(1.5);
        ctx.SetSourceRGBA(sealRim[0], sealRim[1], sealRim[2], 1);
        ctx.Stroke();

        ctx.NewPath();
        ctx.Arc(cx, cy, r - s(2), Math.PI * 0.75, Math.PI * 1.25);
        ctx.LineWidth = s(2);
        ctx.SetSourceRGBA(sealHi[0], sealHi[1], sealHi[2], sealHi[3]);
        ctx.Stroke();

        sealFont.SetupContext(ctx);
        var ext = ctx.TextExtents(sealLetter);
        ctx.SetSourceRGBA(0.97, 0.94, 0.86, 0.95);
        ctx.MoveTo(cx - ext.Width / 2 - ext.XBearing, cy + ext.Height / 2);
        ctx.ShowText(sealLetter);
    }

    private void DrawChoices(Context ctx, Func<double, double> s, double left, double top)
    {
        double cx = left;
        foreach (Choice c in choices)
        {
            CairoFont font = c.Quiet ? quietFont : choiceFont;
            font.SetupContext(ctx);
            var ext = ctx.TextExtents(c.Label);

            double bw = ext.Width + s(ChoicePadX) * 2;
            double bh = ext.Height + s(ChoicePadY) * 2;
            double by = top;

            if (!c.Quiet)
            {
                // The committing choice gets a drawn chip, so it reads as a thing you press
                // rather than one more underlined word on a page full of them.
                RoundRect(ctx, cx, by, bw, bh, s(4));
                ctx.SetSourceRGBA(rule[0], rule[1], rule[2], 0.14);
                ctx.Fill();
                ctx.LineWidth = s(1.5);
                RoundRect(ctx, cx, by, bw, bh, s(4));
                ctx.SetSourceRGBA(rule[0], rule[1], rule[2], 0.85);
                ctx.Stroke();
            }

            font.SetupContext(ctx);
            ctx.MoveTo(cx + s(ChoicePadX) - ext.XBearing, by + s(ChoicePadY) + ext.Height);
            ctx.ShowText(c.Label);

            hits.Add((cx, by, bw, bh, c));
            cx += bw + s(ChoiceGap);
        }
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (args.Handled) return;
        foreach (var h in hits)
        {
            if (args.X < h.X || args.X > h.X + h.W || args.Y < h.Y || args.Y > h.Y + h.H) continue;
            args.Handled = true;
            h.C.OnPick?.Invoke();
            return;
        }
    }

    /// <summary>Hand cursor over a choice, the only "this is pressable" cue available without a
    /// recompose per hover.</summary>
    public override bool UseMouseOverCursor(ElementBounds richtextBounds)
    {
        double mx = api.Input.MouseX - richtextBounds.absX;
        double my = api.Input.MouseY - richtextBounds.absY;
        foreach (var h in hits)
            if (mx >= h.X && mx <= h.X + h.W && my >= h.Y && my <= h.Y + h.H) return true;
        return false;
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
