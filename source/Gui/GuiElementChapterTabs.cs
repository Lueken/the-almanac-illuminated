using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace AlmanacIlluminated;

public enum BookTabSide { Left, Right }

/// <summary>
/// A vertical strip of ribbon tabs hanging off one edge of the book. Right-side
/// ribbons attach at the book's right edge and reach into the right margin; left
/// ribbons mirror that on the left. Each ribbon bears a short label; the active
/// one reads brighter and reaches a touch further out.
///
/// Two kinds of tab share the strip. A LETTER tab is the thumb-index: one character
/// on a short printed slip, and it is drawn exactly as it always was. A NAMED tab is
/// a BOOKMARK: a longer scrap of parchment, cloth or leather shoved between the
/// pages, drawn as pixel art at texel grain from a real sampled palette. Fitting a
/// word onto an index slip was the wrong problem; a bookmark is simply longer, so
/// the name fits at full size and the ellipsis becomes unreachable.
///
/// The last <c>bottomCount</c> tabs hang from the FOOT of the strip instead of
/// following on from the top group. That is what separates the book's own tools
/// (Journal, Crops, Weather, Index) from the reading tabs above them. Capacity is
/// this element's own business: the physical renderer anchors the same group to
/// the same edge using its own metrics, so neither needs a shared row count.
///
/// Drawn and hit-tested here directly. The native vertical tabs draw right-facing
/// tabs on one side of their bounds but test clicks on the other, so they cannot
/// be clicked. This avoids that.
/// </summary>
public class GuiElementChapterTabs : GuiElement
{
    private readonly string[] labels;
    private readonly int bottomCount;
    private readonly int activeIndex;
    private readonly Action<int> onClick;
    private readonly BookTabSide side;
    private readonly CairoFont fontActive;
    private readonly CairoFont fontIdle;

    private LoadedTexture texture;
    private double tabH, gap, rad, pad, padEnd, fullW, letterW;
    private int texel;

    private const double UnTabH = 27, UnGap = 5, UnPad = 11, UnRadius = 4, IdleShrink = 10;

    /// <summary>Longest a LETTER slip may reach, unscaled. This is the old strip width, so
    /// a thumb-index tab is exactly the length it has always been however far the margin
    /// now runs. The margin grew for the bookmarks; a slip with one character on it has no
    /// use for the extra reach, and keeping it short is what makes the bookmarks read as
    /// longer.</summary>
    private const double UnLetterW = 146;

    /// <summary>Texel size for the pixel-art bookmarks, unscaled. Three is coarse enough to
    /// read as made rather than drawn, fine enough that a torn edge still has shape.</summary>
    private const double UnTexel = 3;

    /// <summary>How far an idle bookmark is dimmed. The material carries its own colour, so
    /// the active cue is a shade off the idle ones rather than a different palette. It does
    /// the whole job here, since a bookmark keeps its length either way, so it is set deep
    /// enough to read at a glance.</summary>
    private const double IdleDim = 0.86;

    /// <summary>Trailing inset on the far side of the label, matching the tuck shadow's
    /// width. It is what stops a long title running out over the rounded outer corner
    /// (or, on a left ribbon, into the shadow along the book edge).</summary>
    private const double UnPadEnd = 5;

    /// <summary>Smallest label size, unscaled. A title that will not fit the ribbon steps
    /// down to here and no further; below it the glyphs stop being readable at a glance,
    /// which is the whole job of a thumb tab. Anything still too wide is clipped instead.</summary>
    private const float LabelFloorSize = 11f;

    /// <summary>Step for the size search. Small enough that a title rarely loses more size
    /// than it needs, coarse enough to keep the search to a handful of measurements.</summary>
    private const float LabelSizeStep = 0.5f;

    /// <summary>The size a label wants to be read at, and gets on a bookmark long enough
    /// to hold it.</summary>
    private const float LabelNominalSize = 15f;

    private static readonly double[] FillActive = { 0.95, 0.91, 0.80, 1 };
    private static readonly double[] FillIdle = { 0.86, 0.80, 0.66, 1 };
    private static readonly double[] Border = { 0.38, 0.29, 0.19, 0.85 };

    // Per-tab paper character: small deterministic skew + tint jitter so the
    // strip reads as tucked-in slips of paper, not a printed UI ribbon.
    private static double Skew(int i) => ((i * 7 + 3) % 5 - 2) * 1.1;          // -2.2..+2.2 deg
    private static double Tint(int i) => ((i * 5 + 1) % 4 - 1.5) * 0.014;      // subtle per-slip shade

    public GuiElementChapterTabs(ICoreClientAPI capi, ElementBounds bounds, string[] labels, int activeIndex,
        Action<int> onClick, BookTabSide side = BookTabSide.Right, int bottomCount = 0) : base(capi, bounds)
    {
        this.labels = labels;
        this.bottomCount = GameMath.Clamp(bottomCount, 0, labels.Length);
        this.activeIndex = activeIndex;
        this.onClick = onClick;
        this.side = side;
        fontActive = CairoFont.WhiteSmallText().WithFontSize(LabelNominalSize).WithColor(new[] { 0.13, 0.09, 0.05, 1.0 });
        fontIdle = CairoFont.WhiteSmallText().WithFontSize(LabelNominalSize).WithColor(new[] { 0.20, 0.14, 0.08, 1.0 });
        texture = new LoadedTexture(capi);
    }

    /// <summary>Top of tab i. The top group runs down from 0; the bottom group runs up
    /// from the foot, so it stays pinned however many tabs sit above it.</summary>
    private double RowY(int i)
    {
        int topCount = labels.Length - bottomCount;
        if (i < topCount) return i * (tabH + gap);
        int fromBottom = labels.Length - 1 - i;
        return Bounds.InnerHeight - tabH - fromBottom * (tabH + gap);
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        tabH = scaled(UnTabH); gap = scaled(UnGap); rad = scaled(UnRadius); pad = scaled(UnPad);
        padEnd = scaled(UnPadEnd);
        fullW = Bounds.InnerWidth - scaled(4);
        letterW = Math.Min(fullW, scaled(UnLetterW));
        texel = Math.Max(2, (int)Math.Round(scaled(UnTexel)));

        var surface = new ImageSurface(Format.Argb32, (int)Bounds.InnerWidth + 1, (int)Bounds.InnerHeight + 1);
        var ctx = new Context(surface);
        ctx.SetSourceRGBA(0, 0, 0, 0);
        ctx.Paint();

        for (int i = 0; i < labels.Length; i++)
        {
            double y = RowY(i);
            bool active = i == activeIndex;
            double w = TabWidth(i, active);
            // Right ribbons grow from the left (book) edge; left ribbons grow from the right (book) edge.
            double x0 = side == BookTabSide.Right ? 0 : Bounds.InnerWidth - w;

            if (IsNamed(i)) DrawBookmark(ctx, i, x0, y, w, active);
            else DrawLetterSlip(ctx, i, x0, y, w, active);
        }

        generateTexture(surface, ref texture);
        ctx.Dispose();
        surface.Dispose();
    }

    /// <summary>A named tab is a bookmark; a single character is a thumb-index slip. The
    /// strip builder writes letters as one-character labels, so the label itself is the
    /// whole test, and both renderers can make it independently.</summary>
    private bool IsNamed(int i) => labels[i].Length > 1;

    /// <summary>
    /// A letter slip shortens when it is not the active one, which is the printed-index
    /// idiom. A BOOKMARK does not: its length sets the texel grid, and a grid that changed
    /// with selection would re-tear the scrap every time it was clicked. A bookmark that
    /// changes shape when you pick it stops reading as an object, so its active cue is the
    /// shade instead (see <see cref="IdleDim"/>).
    /// </summary>
    private double TabWidth(int i, bool active)
    {
        if (IsNamed(i)) return fullW;
        return active ? letterW : letterW - scaled(IdleShrink);
    }

    /// <summary>
    /// The thumb-index slip, unchanged: a smooth printed tab with a rounded outer edge,
    /// a small skew, and a tuck shadow along the book edge.
    /// </summary>
    private void DrawLetterSlip(Context ctx, int i, double x0, double y, double w, bool active)
    {
        // Each slip skews slightly about its attachment edge (the book side),
        // like paper tucked between pages.
        double pivotX = side == BookTabSide.Right ? x0 : x0 + w;
        ctx.Save();
        ctx.Translate(pivotX, y + tabH / 2);
        ctx.Rotate(Skew(i) * Math.PI / 180.0);
        ctx.Translate(-pivotX, -(y + tabH / 2));

        TabPath(ctx, side, x0, y, w, tabH, rad);
        var fill = active ? FillActive : FillIdle;
        double t = active ? 0 : Tint(i);
        ctx.SetSourceRGBA(fill[0] + t, fill[1] + t, fill[2] + t, fill[3]);
        ctx.FillPreserve();
        ctx.LineWidth = scaled(1);
        ctx.SetSourceRGBA(Border[0], Border[1], Border[2], Border[3]);
        ctx.Stroke();

        // A faint shadow along the attachment edge sells "emerging from the pages".
        double sx = side == BookTabSide.Right ? x0 : x0 + w - scaled(5);
        var tuck = new LinearGradient(sx, y, sx + scaled(5), y);
        bool fromLeft = side == BookTabSide.Right;
        tuck.AddColorStop(fromLeft ? 0.0 : 1.0, new Color(0.25, 0.18, 0.10, 0.30));
        tuck.AddColorStop(fromLeft ? 1.0 : 0.0, new Color(0.25, 0.18, 0.10, 0.0));
        ctx.SetSource(tuck);
        ctx.Rectangle(sx, y, scaled(5), tabH);
        ctx.Fill();
        tuck.Dispose();

        var font = active ? fontActive : fontIdle;
        font.SetupContext(ctx);
        var fe = font.GetFontExtents();
        ctx.MoveTo(x0 + pad, y + (tabH + fe.Ascent - fe.Descent) / 2);
        ctx.ShowText(labels[i]);

        ctx.Restore();
    }

    /// <summary>
    /// The bookmark: a scrap of whatever was to hand, laid on the page edge. No skew here.
    /// The tear is already irregular, and rotating a texel grid off axis is the one thing
    /// that would give the pixel art away.
    /// </summary>
    private void DrawBookmark(Context ctx, int i, double x0, double y, double w, bool active)
    {
        int gw = Math.Max(4, (int)(w / texel));
        int gh = Math.Max(3, (int)(tabH / texel));
        var skin = ScrapBookmark.Build(labels[i], gw, gh, freeEndLeft: side == BookTabSide.Left);

        // The grid rarely divides the row exactly. Centre the remainder rather than
        // letting the scrap sit high, and keep the attachment edge against the book.
        double drawW = gw * texel, drawH = gh * texel;
        double bx = side == BookTabSide.Right ? x0 : x0 + w - drawW;
        double by = y + (tabH - drawH) / 2;
        PaintSkin(ctx, skin, bx, by, texel, active ? 1.0 : IdleDim);

        // The free end is torn, and the tear eats texels, so the label has to clear more
        // there than it does against the book. Deep enough for the deepest tear the
        // generator will cut; a name lying across a notch reads as a rendering fault.
        double freeInset = Math.Max(padEnd, ScrapBookmark.MaxEndBite * texel);
        var nominal = CairoFont.WhiteSmallText().WithFontSize(LabelNominalSize).WithColor(skin.Ink);
        var fit = FitLabel(nominal, labels[i], drawW - pad - freeInset, tabH);
        fit.Font.SetupContext(ctx);
        var fe = fit.Font.GetFontExtents();
        double tx = bx + (side == BookTabSide.Right ? pad : freeInset);

        if (fit.Second == null)
        {
            ctx.MoveTo(tx, y + (tabH + fe.Ascent - fe.Descent) / 2);
            ctx.ShowText(fit.First);
        }
        else
        {
            // Two lines, the pair centred on the row: first baseline sits one line
            // height above where a single line would have gone.
            double block = fe.Height * 2;
            double top = y + (tabH - block) / 2;
            ctx.MoveTo(tx, top + fe.Ascent);
            ctx.ShowText(fit.First);
            ctx.MoveTo(tx, top + fe.Height + fe.Ascent);
            ctx.ShowText(fit.Second);
        }
    }

    /// <summary>
    /// Blits a texel grid as hard-edged blocks. Antialiasing is off and runs of one colour
    /// go down as a single rectangle, so the result is a real pixel grid rather than a
    /// smoothed image of one.
    /// </summary>
    private static void PaintSkin(Context ctx, ScrapSkin skin, double x, double y, int texel, double dim)
    {
        ctx.Save();
        ctx.Antialias = Antialias.None;
        for (int gy = 0; gy < skin.Height; gy++)
        {
            int gx = 0;
            while (gx < skin.Width)
            {
                int argb = skin.Argb[gy * skin.Width + gx];
                int run = 1;
                while (gx + run < skin.Width && skin.Argb[gy * skin.Width + gx + run] == argb) run++;

                double a = ((argb >> 24) & 255) / 255.0;
                if (a > 0)
                {
                    ctx.SetSourceRGBA(
                        ((argb >> 16) & 255) / 255.0 * dim,
                        ((argb >> 8) & 255) / 255.0 * dim,
                        (argb & 255) / 255.0 * dim, a);
                    ctx.Rectangle(x + gx * texel, y + gy * texel, run * texel, texel);
                    ctx.Fill();
                }
                gx += run;
            }
        }
        ctx.Restore();
    }

    /// <summary>A label as it can actually be drawn: a font, a first line, and a second
    /// line when the name had to wrap.</summary>
    public readonly struct LabelFit
    {
        public readonly CairoFont Font;
        public readonly string First;
        public readonly string? Second;
        public LabelFit(CairoFont font, string first, string? second = null) { Font = font; First = first; Second = second; }
    }

    /// <summary>
    /// Fits a label to a bookmark, in the order the design calls for. Full size on one
    /// line is the answer a long enough bookmark gives for every name that exists today.
    /// Failing that the name wraps to two lines; failing that the type steps down toward
    /// <see cref="LabelFloorSize"/>, trying one line then two at each size, so the largest
    /// readable type always wins. The ellipsis is the last resort and should be
    /// unreachable: it means the name did not fit on two lines at eleven points.
    ///
    /// Widths come back from the font already scaled, like every width in this element,
    /// so they compare directly. Fonts are cloned before resizing, never mutated.
    /// </summary>
    private static LabelFit FitLabel(CairoFont nominal, string label, double usableW, double usableH)
    {
        if (string.IsNullOrEmpty(label) || usableW <= 0) return new LabelFit(nominal, label);

        var (head, tail) = SplitTwo(label);
        for (float size = (float)nominal.UnscaledFontsize; size > LabelFloorSize; size -= LabelSizeStep)
        {
            var f = size >= nominal.UnscaledFontsize ? nominal : nominal.Clone().WithFontSize(size);
            if (f.GetTextExtents(label).Width <= usableW) return new LabelFit(f, label);
            if (tail != null && Fits(f, head, tail, usableW, usableH)) return new LabelFit(f, head, tail);
        }

        var floor = nominal.Clone().WithFontSize(LabelFloorSize);
        if (floor.GetTextExtents(label).Width <= usableW) return new LabelFit(floor, label);
        if (tail != null && Fits(floor, head, tail, usableW, usableH)) return new LabelFit(floor, head, tail);
        return new LabelFit(floor, Clip(floor, label, usableW));
    }

    /// <summary>Whether both lines fit the width and the pair fits the row's height.</summary>
    private static bool Fits(CairoFont font, string a, string b, double usableW, double usableH)
        => font.GetFontExtents().Height * 2 <= usableH
        && font.GetTextExtents(a).Width <= usableW
        && font.GetTextExtents(b).Width <= usableW;

    /// <summary>
    /// Splits a name at the space nearest its middle, which is what keeps "Smelter's Yard"
    /// breaking as "Smelter's" over "Yard" instead of stranding one short word. A name with
    /// no space cannot wrap, and says so by returning no second line.
    /// </summary>
    private static (string head, string? tail) SplitTwo(string label)
    {
        int mid = label.Length / 2, best = -1;
        for (int i = 1; i < label.Length - 1; i++)
            if (label[i] == ' ' && (best < 0 || Math.Abs(i - mid) < Math.Abs(best - mid))) best = i;
        return best < 0 ? (label, null) : (label.Substring(0, best), label.Substring(best + 1));
    }

    /// <summary>Drops characters off the end until the title plus its ellipsis fits. A
    /// trailing space before the ellipsis reads as a gap, so it goes with the character.</summary>
    private static string Clip(CairoFont font, string label, double usableW)
    {
        for (int len = label.Length - 1; len > 0; len--)
        {
            string trial = label.Substring(0, len).TrimEnd() + "…";
            if (font.GetTextExtents(trial).Width <= usableW) return trial;
        }
        return "…";
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        api.Render.Render2DTexture(texture.TextureId,
            (int)Bounds.renderX, (int)Bounds.renderY, (int)Bounds.InnerWidth + 1, (int)Bounds.InnerHeight + 1);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        // Event coords, never api.Input.MouseX: in physical mode events arrive
        // remapped through the tilted page, and the raw cursor position points
        // at the invisible flat layout instead.
        double mx = args.X - Bounds.absX;
        double my = args.Y - Bounds.absY;
        if (mx < 0 || mx > Bounds.InnerWidth) return;
        for (int i = 0; i < labels.Length; i++)
        {
            double y = RowY(i);
            if (my < y || my > y + tabH) continue;

            // Letters and bookmarks no longer share a length, so the row alone is not
            // the hit box. A click out past the end of a short slip is a click on the
            // margin, and must stay one.
            double w = TabWidth(i, i == activeIndex);
            double x0 = side == BookTabSide.Right ? 0 : Bounds.InnerWidth - w;
            if (mx < x0 || mx > x0 + w) return;

            onClick(i);
            args.Handled = true;
            return;
        }
    }

    /// <summary>The flat edge sits against the book; the outer corners are rounded. Traced clockwise.</summary>
    private static void TabPath(Context ctx, BookTabSide side, double x, double y, double w, double h, double r)
    {
        double pi = Math.PI;
        ctx.NewPath();
        if (side == BookTabSide.Right)
        {
            // Flat left edge against the book; rounded top-right and bottom-right.
            ctx.MoveTo(x, y);
            ctx.LineTo(x + w - r, y);
            ctx.Arc(x + w - r, y + r, r, 1.5 * pi, 2 * pi);
            ctx.LineTo(x + w, y + h - r);
            ctx.Arc(x + w - r, y + h - r, r, 0, 0.5 * pi);
            ctx.LineTo(x, y + h);
        }
        else
        {
            // Flat right edge against the book; rounded top-left and bottom-left.
            ctx.MoveTo(x + r, y);
            ctx.LineTo(x + w, y);
            ctx.LineTo(x + w, y + h);
            ctx.LineTo(x + r, y + h);
            ctx.Arc(x + r, y + h - r, r, 0.5 * pi, pi);
            ctx.LineTo(x, y + r);
            ctx.Arc(x + r, y + r, r, pi, 1.5 * pi);
        }
        ctx.ClosePath();
    }

    public override void Dispose()
    {
        base.Dispose();
        texture.Dispose();
    }
}
