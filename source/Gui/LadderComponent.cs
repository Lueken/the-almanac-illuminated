using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// The Ladder: InkPips unrolled vertically — six rungs threaded on a staff, the recto
/// of a calling's first spread. Three states with no label to lean on, so each is
/// carried four ways at once: the staff inks 2px solid where it has been climbed and
/// 1px faint below, the node is a solid diamond behind you, a rubric lozenge with a
/// parchment collar at your rung and a hollow outline ahead, the name steps ink to
/// rubric to faint, and the reading itself moves from settled roman to full ink to
/// italic for what has not happened. A walled rank takes the barred × and the caps
/// aside "beyond this hand". No "you stand here" label — the four signals carry it
/// (user decision, 2026-08-22 handoff).
///
/// Each rung is a click target when <c>onRungClick</c> is supplied (turn straight to
/// the spread that rung landed on). Like LegendComponent this floats left and reserves
/// its full height, so it MUST be trailed by a ClearFloat — use <see cref="Build"/>,
/// which bundles the pairing.
/// </summary>
public class LadderComponent : RichTextComponentBase
{
    public enum RungState { Done, Current, Ahead, Barred }

    public sealed class Rung
    {
        public string Label = "";
        public string Body = "";
        public RungState State;
        public int PipsTotal = 5;
        public int PipsFilled;
        public int PipsCurrent = -1;
        public int PipsBarred;
    }

    private readonly List<Rung> rungs;
    private readonly Action<int>? onRungClick;

    private readonly CairoFont nameFont;
    private readonly CairoFont nameFontCurrent;
    private readonly CairoFont bodyFont;
    private readonly CairoFont capsFont;

    // Unscaled geometry.
    private const double StaffCX = 8;      // staff centerline x
    private const double TextX = 28;       // text column left edge
    private const double RowGap = 14;      // gap under each rung block
    private const double NameH = 27;       // name line height (current rows add a hair)
    private const double BodyLineH = 22;   // wrapped body line height
    private const double PipSize = 7, PipGap = 4, NamePipGap = 9;

    private static readonly double[] Ink = { 0.13, 0.09, 0.05 };
    private static readonly double[] Rubric = { 0.541, 0.353, 0.133 };
    private static readonly double[] Muted = { 0.42, 0.36, 0.28 };
    private const double FaintA = 0.38, BarredA = 0.34, StaffFaintA = 0.16;
    private static readonly double[] Parchment = { 0.953, 0.918, 0.824 };

    // Per-row layout captured in CalcBounds for drawing + hit testing (scaled px,
    // relative to the component's own bounds origin).
    private double[] rowTop = Array.Empty<double>();
    private double[] rowHeight = Array.Empty<double>();
    private List<string>[] wrapped = Array.Empty<List<string>>();
    private int mouseDownRow = -1;

    public LadderComponent(ICoreClientAPI api, List<Rung> rungs, Action<int>? onRungClick = null) : base(api)
    {
        this.rungs = rungs;
        this.onRungClick = onRungClick;
        if (onRungClick != null) MouseOverCursor = "linkselect";

        // Explicit sizes, stepped up 2026-08-22 R2: the ladder page carried the most
        // blank paper of the set, and the ruling is to spend it on type.
        nameFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(FontWeight.Bold).WithFontSize(20f);
        nameFontCurrent = nameFont.Clone().WithFontSize(22f);
        bodyFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithFontSize(17f);
        capsFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.DisplaySans).WithFontSize(13.5f);

        Float = EnumFloat.Left;
        VerticalAlign = EnumVerticalAlign.Top;
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    /// <summary>The ladder with its mandatory trailing clear, so MeasureHeight and the
    /// flow engine both see its true height (the LegendComponent pairing).</summary>
    public static IEnumerable<RichTextComponentBase> Build(ICoreClientAPI api,
        List<Rung> rungs, Action<int>? onRungClick = null)
    {
        yield return new LadderComponent(api, rungs, onRungClick);
        yield return new ClearFloatTextComponent(api, 0);
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        double width = cur.X2 - cur.X1;
        double textW = width - GuiElement.scaled(TextX);

        rowTop = new double[rungs.Count];
        rowHeight = new double[rungs.Count];
        wrapped = new List<string>[rungs.Count];

        using var surf = new ImageSurface(Format.Argb32, 1, 1);
        using var mctx = new Context(surf);

        double y = 0;
        for (int i = 0; i < rungs.Count; i++)
        {
            rowTop[i] = y;
            wrapped[i] = Wrap(mctx, rungs[i].Body, bodyFont, textW);
            double h = GuiElement.scaled(NameH)
                + wrapped[i].Count * GuiElement.scaled(BodyLineH)
                + GuiElement.scaled(RowGap);
            rowHeight[i] = h;
            y += h;
        }

        BoundsPerLine[0].X = cur.X1 + offsetX;
        BoundsPerLine[0].Y = lineY;
        BoundsPerLine[0].Width = width;
        BoundsPerLine[0].Height = y;
        nextOffsetX = offsetX;
        return EnumCalcBoundsResult.Nextline;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (rungs.Count == 0 || b.Width < 1) return;

        double staffX = b.X + GuiElement.scaled(StaffCX);
        double textX = b.X + GuiElement.scaled(TextX);

        for (int i = 0; i < rungs.Count; i++)
        {
            Rung r = rungs[i];
            bool current = r.State == RungState.Current;
            bool done = r.State == RungState.Done;
            bool barred = r.State == RungState.Barred;
            double top = b.Y + rowTop[i];
            double nameH = GuiElement.scaled(NameH);
            double nodeCY = top + nameH * 0.52;

            // The current row's faint rubric wash, full width, behind everything.
            if (current)
            {
                ctx.SetSourceRGBA(Rubric[0], Rubric[1], Rubric[2], 0.075);
                RoundRect(ctx, b.X - GuiElement.scaled(4), top - GuiElement.scaled(3),
                    b.Width + GuiElement.scaled(8), rowHeight[i] - GuiElement.scaled(4), GuiElement.scaled(3));
                ctx.Fill();
            }

            // The staff segment from this node down to the next (half-stopped on the
            // last rung): 2px solid ink where climbed, 1px faint where not.
            if (i < rungs.Count - 1 || true)
            {
                double segBottom = i == rungs.Count - 1
                    ? nodeCY
                    : b.Y + rowTop[i + 1] + GuiElement.scaled(NameH) * 0.52;
                if (segBottom > nodeCY)
                {
                    bool climbed = done;
                    ctx.LineWidth = GuiElement.scaled(climbed ? 2 : 1);
                    ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], climbed ? 0.80 : StaffFaintA);
                    ctx.NewPath();
                    ctx.MoveTo(staffX, nodeCY);
                    ctx.LineTo(staffX, segBottom);
                    ctx.Stroke();
                }
            }

            // The node.
            if (barred)
            {
                double h = GuiElement.scaled(3.2);
                ctx.LineWidth = GuiElement.scaled(1);
                ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], BarredA);
                ctx.NewPath(); ctx.MoveTo(staffX - h, nodeCY - h); ctx.LineTo(staffX + h, nodeCY + h); ctx.Stroke();
                ctx.NewPath(); ctx.MoveTo(staffX + h, nodeCY - h); ctx.LineTo(staffX - h, nodeCY + h); ctx.Stroke();
            }
            else if (current)
            {
                // Rubric lozenge with a parchment collar: diamond, then a parchment
                // outline, then a thin rubric ring outside it.
                double s = GuiElement.scaled(5.5);
                Diamond(ctx, staffX, nodeCY, s);
                ctx.SetSourceRGBA(Rubric[0], Rubric[1], Rubric[2], 1); ctx.Fill();
                Diamond(ctx, staffX, nodeCY, s + GuiElement.scaled(1.5));
                ctx.LineWidth = GuiElement.scaled(2);
                ctx.SetSourceRGBA(Parchment[0], Parchment[1], Parchment[2], 0.95); ctx.Stroke();
                Diamond(ctx, staffX, nodeCY, s + GuiElement.scaled(2.6));
                ctx.LineWidth = GuiElement.scaled(1);
                ctx.SetSourceRGBA(Rubric[0], Rubric[1], Rubric[2], 1); ctx.Stroke();
            }
            else
            {
                double s = GuiElement.scaled(3.5);
                Diamond(ctx, staffX, nodeCY, s);
                if (done) { ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.80); ctx.Fill(); }
                else
                {
                    ctx.LineWidth = GuiElement.scaled(1);
                    ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], BarredA);
                    ctx.Stroke();
                }
            }

            // Name — ink behind you, rubric and a size step at your rung, faint ahead.
            CairoFont nf = (current ? nameFontCurrent : nameFont).Clone();
            nf.Color = barred ? new[] { Ink[0], Ink[1], Ink[2], BarredA }
                : current ? new[] { Rubric[0], Rubric[1], Rubric[2], 1.0 }
                : done ? new[] { Ink[0], Ink[1], Ink[2], 1.0 }
                : new[] { Ink[0], Ink[1], Ink[2], FaintA };
            nf.SetupContext(ctx);
            FontExtents fe = ctx.FontExtents;
            double nameBase = top + (nameH + fe.Ascent - fe.Descent) / 2;
            ctx.MoveTo(textX, nameBase);
            ctx.ShowText(r.Label);
            double afterName = textX + ctx.TextExtents(r.Label).Width;

            // The pip row inline after the name (InkPips' own drawing, mini).
            double px = afterName + GuiElement.scaled(NamePipGap);
            double ps = GuiElement.scaled(PipSize);
            double pg = GuiElement.scaled(PipGap);
            int firstBarredPip = r.PipsTotal - r.PipsBarred;
            for (int p = 0; p < r.PipsTotal; p++)
            {
                double cx = px + p * (ps + pg) + ps / 2;
                double cy = nodeCY;
                if (p >= firstBarredPip)
                {
                    double h = ps * 0.34;
                    ctx.LineWidth = GuiElement.scaled(1);
                    ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], BarredA);
                    ctx.NewPath(); ctx.MoveTo(cx - h, cy - h); ctx.LineTo(cx + h, cy + h); ctx.Stroke();
                    ctx.NewPath(); ctx.MoveTo(cx + h, cy - h); ctx.LineTo(cx - h, cy + h); ctx.Stroke();
                    continue;
                }
                Diamond(ctx, cx, cy, ps / 2);
                if (p < r.PipsFilled) { ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.80); ctx.Fill(); }
                else if (p == r.PipsCurrent)
                {
                    ctx.LineWidth = GuiElement.scaled(1);
                    ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.90); ctx.Stroke();
                }
                else { ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.18); ctx.Fill(); }
            }

            // The walled aside, caps, after the pips.
            if (barred)
            {
                capsFont.Clone().SetupContext(ctx);
                ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], FaintA);
                ctx.MoveTo(px + r.PipsTotal * (ps + pg) + GuiElement.scaled(4), nameBase - GuiElement.scaled(1));
                ctx.ShowText("BEYOND THIS HAND");
            }

            // Body — settled roman muted behind you, full ink at your rung, italic
            // faint for what has not happened.
            CairoFont bf = bodyFont.Clone();
            if (barred) { bf.Color = new[] { Ink[0], Ink[1], Ink[2], BarredA }; bf.Slant = FontSlant.Italic; }
            else if (current) bf.Color = new[] { Ink[0], Ink[1], Ink[2], 1.0 };
            else if (done) bf.Color = new[] { Muted[0], Muted[1], Muted[2], 1.0 };
            else { bf.Color = new[] { Ink[0], Ink[1], Ink[2], FaintA }; bf.Slant = FontSlant.Italic; }
            bf.SetupContext(ctx);
            FontExtents bfe = ctx.FontExtents;
            double by = top + nameH;
            foreach (string line in wrapped[i])
            {
                ctx.MoveTo(textX, by + bfe.Ascent);
                ctx.ShowText(line);
                by += GuiElement.scaled(BodyLineH);
            }
            ctx.NewPath();
        }
    }

    public override void OnMouseDown(MouseEvent args)
    {
        mouseDownRow = onRungClick == null ? -1 : HitRow(args.X, args.Y);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (onRungClick == null || mouseDownRow < 0) return;
        int row = HitRow(args.X, args.Y);
        int pressed = mouseDownRow;
        mouseDownRow = -1;
        if (row >= 0 && row == pressed)
        {
            args.Handled = true;
            onRungClick(row);
        }
    }

    private int HitRow(double mx, double my)
    {
        var b = BoundsPerLine[0];
        if (mx < b.X || mx > b.X + b.Width) return -1;
        for (int i = 0; i < rowTop.Length; i++)
        {
            double top = b.Y + rowTop[i];
            if (my >= top && my <= top + rowHeight[i]) return i;
        }
        return -1;
    }

    private static void Diamond(Context ctx, double cx, double cy, double half)
    {
        ctx.NewPath();
        ctx.MoveTo(cx, cy - half);
        ctx.LineTo(cx + half, cy);
        ctx.LineTo(cx, cy + half);
        ctx.LineTo(cx - half, cy);
        ctx.ClosePath();
    }

    private static void RoundRect(Context ctx, double x, double y, double w, double h, double r)
    {
        ctx.NewPath();
        ctx.Arc(x + w - r, y + r, r, -Math.PI / 2, 0);
        ctx.Arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
        ctx.Arc(x + r, y + h - r, r, Math.PI / 2, Math.PI);
        ctx.Arc(x + r, y + r, r, Math.PI, 1.5 * Math.PI);
        ctx.ClosePath();
    }

    /// <summary>Greedy word wrap against the real font metrics, so the reserved height
    /// is the drawn height. A single over-long word keeps its line and overflows right
    /// rather than dropping content.</summary>
    private static List<string> Wrap(Context ctx, string text, CairoFont font, double width)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return lines;
        font.SetupContext(ctx);
        string line = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string trial = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && ctx.TextExtents(trial).Width > width)
            {
                lines.Add(line);
                line = word;
            }
            else line = trial;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }
}
