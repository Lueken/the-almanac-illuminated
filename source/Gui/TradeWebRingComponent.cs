using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// The trade web: nodes spaced evenly by arc length around a PORTRAIT ELLIPSE
/// (ruled 2026-08-22: a circle in a book column is width-bound to ~360px by the
/// horizontal side names, so the tall page is spent on a tall figure), their
/// groups read as band arcs, every trading pair joined by a chord bowed toward
/// the centre, and one node held HOT at a time — its edges take the rubric,
/// everything else falls back to faint sepia. Click a name to move the hot seat.
///
/// The figure fits itself to the page: the ellipse grows until the ACTUAL label
/// extents touch the canvas edges (a short-named side gives its slack back to the
/// figure), and the whole drawn bounding box centres in the column — with the
/// title and tagline drawn centred above it, the way the mock set them.
///
/// The anatomy is the Claude Design mock's final circle, carried over: band
/// overhang ±0.32 of a node slot, group labels riding just inside the band and
/// reversed in the lower half so the words stay upright, chords bowed 58% toward
/// centre, leader lines with a sin-weighted drop, cold diamonds CLIPPED to the
/// band — tucked behind the wire — hot and linked ones rendered whole.
/// </summary>
public class TradeWebRingComponent : RichTextComponentBase
{
    public class Node
    {
        public string Key = "";     // caller's identity for OnPick
        public string Label = "";   // ring display name (pre-shortened by the caller)
    }

    public class Group
    {
        public string Label = "";
        public int Start;
        public int Count;
    }

    private readonly List<Node> nodes;
    private readonly List<Group> groups;
    private readonly List<(int A, int B)> edges;   // undirected for drawing
    private readonly int hot;
    private readonly HashSet<int> near = new();    // nodes touched by the hot seat's edges
    private readonly Action<int>? onPick;
    private readonly string? title, subtitle;
    private readonly double widthUn, heightUn;

    // Fixed furniture sizes (unscaled GUI units), the mock's proportions at the
    // book's R3 type scale — the figure grows to the page, the furniture doesn't.
    private const double LeaderIn = 7, LeaderOut = 27, LabelOff = 33;
    private const double ArcInLower = 23, ArcInUpper = 35;   // group-label inset per half
    private const double DiamondHalf = 6.5, EdgeMargin = 4;

    private static readonly double[] Ink = { 0.13, 0.09, 0.05 };
    private static readonly double[] Muted = { 0.42, 0.36, 0.28 };
    private static readonly double[] Rubric = { 0.541, 0.353, 0.133 };
    private static readonly double[] BandInk = { 0.278, 0.180, 0.102 };   // rgba(71,46,26)
    private static readonly double[] LabelInk = { 0.290, 0.227, 0.145 };  // rgba(74,58,37)

    private readonly CairoFont famFont;
    private readonly CairoFont nameFont;
    private readonly CairoFont nameNearFont;
    private readonly CairoFont hotFont;
    private readonly CairoFont titleFont;
    private readonly CairoFont subtitleFont;

    // Layout captured in CalcBounds (scaled px, canvas-relative).
    private double canvasW, canvasH, ringX;
    private double cx, cy, ax, by;                 // centre + semi-axes
    private double headH;
    private double[] nodeT = Array.Empty<double>();      // ellipse param per node
    private double[][] nodePos = Array.Empty<double[]>();
    private double[][] nodeNorm = Array.Empty<double[]>();
    private double[][] labelRect = Array.Empty<double[]>();
    private double slotArc;                        // arc length of one node slot
    private int mouseDownNode = -1;

    public TradeWebRingComponent(ICoreClientAPI api, List<Node> nodes, List<Group> groups,
        List<(int A, int B)> edges, int hot, Action<int>? onPick,
        string? title, string? subtitle, double widthUn, double heightUn) : base(api)
    {
        this.nodes = nodes;
        this.groups = groups;
        this.edges = edges;
        this.hot = hot;
        this.onPick = onPick;
        this.title = title;
        this.subtitle = subtitle;
        this.widthUn = Math.Max(120, widthUn);
        this.heightUn = Math.Max(120, heightUn);
        if (onPick != null) MouseOverCursor = "linkselect";

        foreach (var (a, b) in edges)
            if (a == hot || b == hot) { near.Add(a); near.Add(b); }

        famFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.DisplaySans)
            .WithFontSize(12.5f)
            .WithColor(new[] { LabelInk[0], LabelInk[1], LabelInk[2], 0.88 });
        nameFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithFontSize(16f)
            .WithColor(new[] { Ink[0], Ink[1], Ink[2], 0.42 });
        nameNearFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithWeight(FontWeight.Bold).WithFontSize(16f)
            .WithColor(new[] { Ink[0], Ink[1], Ink[2], 1.0 });
        hotFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(FontWeight.Bold).WithFontSize(18f)
            .WithColor(new[] { Rubric[0], Rubric[1], Rubric[2], 1.0 });
        titleFont = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(FontWeight.Bold).WithFontSize(24f)
            .WithColor(new[] { Ink[0], Ink[1], Ink[2], 1.0 });
        subtitleFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithSlant(FontSlant.Italic).WithFontSize(16f)
            .WithColor(new[] { Muted[0], Muted[1], Muted[2], 1.0 });

        Float = EnumFloat.Left;
        VerticalAlign = EnumVerticalAlign.Top;
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    /// <summary>The web with its mandatory trailing clear (the LegendComponent
    /// pairing), so MeasureHeight and the flow engine both see its true height.</summary>
    public static IEnumerable<RichTextComponentBase> Build(ICoreClientAPI api, List<Node> nodes,
        List<Group> groups, List<(int A, int B)> edges, int hot, Action<int>? onPick,
        string? title, string? subtitle, double widthUn, double heightUn)
    {
        yield return new TradeWebRingComponent(api, nodes, groups, edges, hot, onPick, title, subtitle, widthUn, heightUn);
        yield return new ClearFloatTextComponent(api, 0);
    }

    /// <summary>|d/dt (a cos t, b sin t)| — the param-to-arc-length speed.</summary>
    private double Speed(double t)
        => Math.Sqrt(ax * ax * Math.Sin(t) * Math.Sin(t) + by * by * Math.Cos(t) * Math.Cos(t));

    /// <summary>Advance an ellipse param by a signed arc length (small-step Euler —
    /// plenty for text and overhangs).</summary>
    private double StepParam(double t, double ds)
    {
        for (int i = 0; i < 4; i++) t += ds / 4 / Math.Max(1e-6, Speed(t));
        return t;
    }

    private (double nx, double ny) Normal(double t)
    {
        double nx = by * Math.Cos(t), ny = ax * Math.Sin(t);
        double len = Math.Max(1e-6, Math.Sqrt(nx * nx + ny * ny));
        return (nx / len, ny / len);
    }

    private CairoFont FontFor(int i) => i == hot ? hotFont : near.Contains(i) ? nameNearFont : nameFont;

    /// <summary>Even arc-length node spacing for the current (ax, by), node 0 at the
    /// top, clockwise. Fills nodeT/nodePos/nodeNorm.</summary>
    private void SpaceNodes()
    {
        int n = Math.Max(1, nodes.Count);
        const int Samples = 1440;
        double total = 0;
        var cum = new double[Samples + 1];
        for (int i = 0; i < Samples; i++)
        {
            double t = -Math.PI / 2 + i * 2 * Math.PI / Samples;
            total += Speed(t) * (2 * Math.PI / Samples);
            cum[i + 1] = total;
        }
        slotArc = total / n;

        int si = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            double target = total * i / n;
            while (si < Samples && cum[si + 1] < target) si++;
            double frac = (target - cum[si]) / Math.Max(1e-6, cum[si + 1] - cum[si]);
            nodeT[i] = -Math.PI / 2 + (si + frac) * 2 * Math.PI / Samples;
            nodePos[i] = new[] { cx + ax * Math.Cos(nodeT[i]), cy + by * Math.Sin(nodeT[i]) };
            (double nx, double ny) = Normal(nodeT[i]);
            nodeNorm[i] = new[] { nx, ny };
        }
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        double width = cur.X2 - cur.X1;
        canvasW = Math.Min(GuiElement.scaled(widthUn), width);
        canvasH = GuiElement.scaled(heightUn);
        ringX = (width - canvasW) / 2;

        using var surf = new ImageSurface(Format.Argb32, 1, 1);
        using var mctx = new Context(surf);

        headH = 0;
        if (title != null)
        {
            titleFont.SetupContext(mctx);
            headH += mctx.FontExtents.Ascent + mctx.FontExtents.Descent + GuiElement.scaled(2);
        }
        if (subtitle != null)
        {
            subtitleFont.SetupContext(mctx);
            headH += mctx.FontExtents.Ascent + mctx.FontExtents.Descent + GuiElement.scaled(2);
        }
        if (headH > 0) headH += GuiElement.scaled(8);

        hotFont.SetupContext(mctx);
        double textH = mctx.FontExtents.Ascent + mctx.FontExtents.Descent;
        double maxLabelW = 0;
        foreach (Node nd in nodes) maxLabelW = Math.Max(maxLabelW, mctx.TextExtents(nd.Label).Width);

        double areaTop = headH;
        double areaH = Math.Max(120, canvasH - areaTop);
        double m = GuiElement.scaled(EdgeMargin);
        double off = GuiElement.scaled(LabelOff);

        cy = areaTop + areaH / 2;
        by = Math.Max(60, areaH / 2 - off - GuiElement.scaled(8) - textH - m);
        cx = canvasW / 2;
        ax = Math.Max(60, canvasW / 2 - off - m - maxLabelW);   // conservative start

        nodeT = new double[nodes.Count];
        nodePos = new double[nodes.Count][];
        nodeNorm = new double[nodes.Count][];
        labelRect = new double[nodes.Count][];

        // Grow the ellipse until the label extents meet the canvas edges, then
        // centre the drawn bounding box — a short-named side gives its slack back
        // to the figure instead of mirroring the widest name. Every label is
        // measured at its MAXIMUM possible treatment (the hot font) here, so the
        // fit is identical no matter which node is hot — fitting with the actual
        // fonts made the whole figure shift a few px on every pick (playtest
        // 2026-08-22). Drawing and hit boxes still use the actual fonts.
        hotFont.SetupContext(mctx);
        var fitW = new double[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) fitW[i] = mctx.TextExtents(nodes[i].Label).Width;
        for (int pass = 0; pass < 4 && nodes.Count > 0; pass++)
        {
            SpaceNodes();
            double eMin = cx - ax - off, eMax = cx + ax + off;
            for (int i = 0; i < nodes.Count; i++)
            {
                double w = fitW[i];
                double anchorX = nodePos[i][0] + off * nodeNorm[i][0];
                double left = nodeNorm[i][0] > 0.15 ? anchorX
                    : nodeNorm[i][0] < -0.15 ? anchorX - w : anchorX - w / 2;
                eMin = Math.Min(eMin, left);
                eMax = Math.Max(eMax, left + w);
            }
            double need = eMax - eMin, avail = canvasW - 2 * m;
            double grow = Math.Min(1.35, Math.Max(0.6, avail / Math.Max(1, need)));
            cx += canvasW / 2 - (eMin + eMax) / 2;
            ax = Math.Max(60, ax * grow);
        }
        SpaceNodes();

        for (int i = 0; i < nodes.Count; i++)
        {
            FontFor(i).SetupContext(mctx);
            TextExtents te = mctx.TextExtents(nodes[i].Label);
            FontExtents fe = mctx.FontExtents;
            (double lx, double ly) = LabelAnchor(i);
            double x = nodeNorm[i][0] > 0.15 ? lx : nodeNorm[i][0] < -0.15 ? lx - te.Width : lx - te.Width / 2;
            labelRect[i] = new[] { ringX + x, ly - fe.Ascent, te.Width, fe.Ascent + fe.Descent };
        }

        BoundsPerLine[0].X = cur.X1 + offsetX;
        BoundsPerLine[0].Y = lineY;
        BoundsPerLine[0].Width = width;
        BoundsPerLine[0].Height = canvasH;
        nextOffsetX = offsetX;
        return EnumCalcBoundsResult.Nextline;
    }

    /// <summary>Name anchor: LabelOff out along the node's normal, with the mock's
    /// sin-weighted vertical relaxation and baseline drop.</summary>
    private (double x, double y) LabelAnchor(int i)
    {
        double nx = nodeNorm[i][0], ny = nodeNorm[i][1];
        double lx = nodePos[i][0] + GuiElement.scaled(LabelOff) * nx;
        double ly = nodePos[i][1] + GuiElement.scaled(LabelOff) * ny
            + GuiElement.scaled(3) + ny * GuiElement.scaled(5);
        return (lx, ly);
    }

    /// <summary>An elliptical arc path from param t0 to t1 (the standard Cairo
    /// save/scale/arc idiom — stroke AFTER restore keeps the line width uniform).</summary>
    private void EllipsePath(Context ctx, double ox, double oy, double t0, double t1)
    {
        ctx.Save();
        ctx.Translate(ox + cx, oy + cy);
        ctx.Scale(Math.Max(1, ax), Math.Max(1, by));
        ctx.NewPath();
        ctx.Arc(0, 0, 1, t0, t1);
        ctx.Restore();
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (nodes.Count == 0 || b.Width < 1) return;

        double ox = b.X + ringX, oy = b.Y;

        // 0. The centred head, the way the mock set it over the figure.
        double headY = oy;
        void CentredLine(CairoFont f, string text)
        {
            f.SetupContext(ctx);
            FontExtents fe = ctx.FontExtents;
            TextExtents te = ctx.TextExtents(text);
            ctx.MoveTo(ox + cx - te.Width / 2, headY + fe.Ascent);
            ctx.ShowText(text);
            headY += fe.Ascent + fe.Descent + GuiElement.scaled(2);
        }
        if (title != null) CentredLine(titleFont, title);
        if (subtitle != null) CentredLine(subtitleFont, subtitle);

        double PX(int i) => ox + nodePos[i][0];
        double PY(int i) => oy + nodePos[i][1];

        // 1. Group band arcs with the mock's ±0.32-slot overhang.
        ctx.LineWidth = GuiElement.scaled(1.3);
        foreach (var g in groups)
        {
            if (g.Count <= 0) continue;
            double t0 = StepParam(nodeT[g.Start], -0.32 * slotArc);
            double t1 = StepParam(nodeT[g.Start + g.Count - 1], 0.32 * slotArc);
            EllipsePath(ctx, ox, oy, t0, t1);
            ctx.SetSourceRGBA(BandInk[0], BandInk[1], BandInk[2], 0.35);
            ctx.Stroke();
        }

        // 2. Group labels riding just inside the band; the lower half walks the
        // arc in reverse so the words stay upright, at its own inset so the
        // visual gap reads the same either way.
        famFont.SetupContext(ctx);
        double tracking = 0.12 * GuiElement.scaled(famFont.UnscaledFontsize);
        foreach (var g in groups)
        {
            if (g.Count <= 0 || g.Label.Length == 0) continue;
            string label = g.Label.ToUpperInvariant();
            double t0 = StepParam(nodeT[g.Start], -0.32 * slotArc);
            double t1 = StepParam(nodeT[g.Start + g.Count - 1], 0.32 * slotArc);
            double mid = (t0 + t1) / 2;
            bool lower = Math.Sin(mid) > 0;
            double inset = GuiElement.scaled(lower ? ArcInLower : ArcInUpper);
            int dir = lower ? -1 : 1;

            // Glyph walk on the inset ellipse; the inset shrinks both axes, which
            // is close enough to a parallel curve for a short caps label.
            double aIn = Math.Max(1, ax - inset), bIn = Math.Max(1, by - inset);
            double SpeedIn(double t) => Math.Sqrt(aIn * aIn * Math.Sin(t) * Math.Sin(t) + bIn * bIn * Math.Cos(t) * Math.Cos(t));
            double StepIn(double t, double ds)
            {
                for (int s = 0; s < 4; s++) t += ds / 4 / Math.Max(1e-6, SpeedIn(t));
                return t;
            }

            double totalW = 0;
            foreach (char ch in label) totalW += ctx.TextExtents(ch.ToString()).XAdvance + tracking;
            totalW -= tracking;

            ctx.SetSourceRGBA(LabelInk[0], LabelInk[1], LabelInk[2], 0.88);
            double tCur = StepIn(mid, -dir * totalW / 2);
            foreach (char ch in label)
            {
                string s = ch.ToString();
                double adv = ctx.TextExtents(s).XAdvance;
                double tg = StepIn(tCur, dir * adv / 2);
                double gx = ox + cx + aIn * Math.Cos(tg);
                double gy = oy + cy + bIn * Math.Sin(tg);
                double tanA = Math.Atan2(dir * bIn * Math.Cos(tg), dir * -aIn * Math.Sin(tg));
                ctx.Save();
                ctx.Translate(gx, gy);
                ctx.Rotate(tanA);
                ctx.MoveTo(-adv / 2, 0);
                ctx.ShowText(s);
                ctx.Restore();
                tCur = StepIn(tg, dir * (adv / 2 + tracking));
            }
        }

        // 3. Chords, cold under hot, bowed 58% toward the centre (quad → cubic).
        void Chord(int a, int bi, bool on)
        {
            double px = PX(a), py = PY(a), qx = PX(bi), qy = PY(bi);
            double mx = (px + qx) / 2, my = (py + qy) / 2;
            double kx = mx + (ox + cx - mx) * 0.58, ky = my + (oy + cy - my) * 0.58;
            ctx.LineWidth = GuiElement.scaled(on ? 1.8 : 1.1);
            if (on) ctx.SetSourceRGBA(Rubric[0], Rubric[1], Rubric[2], 1);
            else ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.13);
            ctx.NewPath();
            ctx.MoveTo(px, py);
            ctx.CurveTo(px + 2.0 / 3 * (kx - px), py + 2.0 / 3 * (ky - py),
                qx + 2.0 / 3 * (kx - qx), qy + 2.0 / 3 * (ky - qy), qx, qy);
            ctx.Stroke();
        }
        foreach (var (a, bi) in edges) if (a != hot && bi != hot) Chord(a, bi, false);
        foreach (var (a, bi) in edges) if (a == hot || bi == hot) Chord(a, bi, true);

        // 4. Nodes: leader line, diamond threaded on the band, name.
        for (int i = 0; i < nodes.Count; i++)
        {
            bool isHot = i == hot, isNear = near.Contains(i);
            double nx = nodeNorm[i][0], ny = nodeNorm[i][1];

            ctx.LineWidth = GuiElement.scaled(1.1);
            if (isHot) ctx.SetSourceRGBA(Rubric[0], Rubric[1], Rubric[2], 1);
            else ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.22);
            ctx.NewPath();
            ctx.MoveTo(PX(i) + GuiElement.scaled(LeaderIn) * nx, PY(i) + GuiElement.scaled(LeaderIn) * ny);
            ctx.LineTo(PX(i) + GuiElement.scaled(LeaderOut) * nx,
                PY(i) + GuiElement.scaled(LeaderOut) * ny + ny * GuiElement.scaled(3));
            ctx.Stroke();

            double half = GuiElement.scaled(DiamondHalf);
            void Diamond()
            {
                double x = PX(i), y = PY(i);
                ctx.NewPath();
                ctx.MoveTo(x, y - half);
                ctx.LineTo(x + half, y);
                ctx.LineTo(x, y + half);
                ctx.LineTo(x - half, y);
                ctx.ClosePath();
                if (isHot) ctx.SetSourceRGBA(Rubric[0], Rubric[1], Rubric[2], 1);
                else if (isNear) ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.80);
                else ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 0.30);
                ctx.Fill();
            }
            if (isHot || isNear) Diamond();
            else
            {
                // Tucked behind the wire: cold diamonds clip to the band ellipse.
                ctx.Save();
                EllipsePath(ctx, ox, oy, 0, 2 * Math.PI);
                ctx.Clip();
                Diamond();
                ctx.Restore();
            }

            FontFor(i).SetupContext(ctx);
            TextExtents te = ctx.TextExtents(nodes[i].Label);
            (double lx, double ly) = LabelAnchor(i);
            double tx = nx > 0.15 ? lx : nx < -0.15 ? lx - te.Width : lx - te.Width / 2;
            ctx.MoveTo(ox + tx, oy + ly);
            ctx.ShowText(nodes[i].Label);
        }
        ctx.NewPath();
    }

    public override void OnMouseDown(MouseEvent args)
    {
        mouseDownNode = onPick == null ? -1 : HitNode(args.X, args.Y);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (onPick == null || mouseDownNode < 0) return;
        int hit = HitNode(args.X, args.Y);
        int pressed = mouseDownNode;
        mouseDownNode = -1;
        if (hit >= 0 && hit == pressed)
        {
            args.Handled = true;
            onPick(hit);
        }
    }

    /// <summary>Name-label hit test with a small grace margin — the labels are the
    /// click targets, exactly as in the mock.</summary>
    private int HitNode(double mx, double my)
    {
        var b = BoundsPerLine[0];
        double pad = GuiElement.scaled(3);
        for (int i = 0; i < labelRect.Length; i++)
        {
            double[] r = labelRect[i];
            if (r == null) continue;
            if (mx >= b.X + r[0] - pad && mx <= b.X + r[0] + r[2] + pad
                && my >= b.Y + r[1] - pad && my <= b.Y + r[1] + r[3] + pad) return i;
        }
        return -1;
    }
}
