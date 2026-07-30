using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

public enum BookTabSide { Left, Right }

/// <summary>
/// A vertical strip of ribbon tabs hanging off one edge of the book. Right-side
/// ribbons attach at the book's right edge and reach into the right margin; left
/// ribbons mirror that on the left. Each ribbon bears a short label; the active
/// one reads brighter and reaches a touch further out.
///
/// Drawn and hit-tested here directly. The native vertical tabs draw right-facing
/// tabs on one side of their bounds but test clicks on the other, so they cannot
/// be clicked. This avoids that.
/// </summary>
public class GuiElementChapterTabs : GuiElement
{
    private readonly string[] labels;
    private readonly int activeIndex;
    private readonly Action<int> onClick;
    private readonly BookTabSide side;
    private readonly CairoFont fontActive;
    private readonly CairoFont fontIdle;

    private LoadedTexture texture;
    private double tabH, gap, rad, pad, fullW;

    private const double UnTabH = 27, UnGap = 5, UnPad = 11, UnRadius = 4, IdleShrink = 10;

    private static readonly double[] FillActive = { 0.95, 0.91, 0.80, 1 };
    private static readonly double[] FillIdle = { 0.86, 0.80, 0.66, 1 };
    private static readonly double[] Border = { 0.38, 0.29, 0.19, 0.85 };

    // Per-tab paper character: small deterministic skew + tint jitter so the
    // strip reads as tucked-in slips of paper, not a printed UI ribbon.
    private static double Skew(int i) => ((i * 7 + 3) % 5 - 2) * 1.1;          // -2.2..+2.2 deg
    private static double Tint(int i) => ((i * 5 + 1) % 4 - 1.5) * 0.014;      // subtle per-slip shade

    public GuiElementChapterTabs(ICoreClientAPI capi, ElementBounds bounds, string[] labels, int activeIndex, Action<int> onClick, BookTabSide side = BookTabSide.Right) : base(capi, bounds)
    {
        this.labels = labels;
        this.activeIndex = activeIndex;
        this.onClick = onClick;
        this.side = side;
        fontActive = CairoFont.WhiteSmallText().WithFontSize(15).WithColor(new[] { 0.13, 0.09, 0.05, 1.0 });
        fontIdle = CairoFont.WhiteSmallText().WithFontSize(15).WithColor(new[] { 0.20, 0.14, 0.08, 1.0 });
        texture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        tabH = scaled(UnTabH); gap = scaled(UnGap); rad = scaled(UnRadius); pad = scaled(UnPad);
        fullW = Bounds.InnerWidth - scaled(4);

        var surface = new ImageSurface(Format.Argb32, (int)Bounds.InnerWidth + 1, (int)Bounds.InnerHeight + 1);
        var ctx = new Context(surface);
        ctx.SetSourceRGBA(0, 0, 0, 0);
        ctx.Paint();

        double y = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            bool active = i == activeIndex;
            double w = active ? fullW : fullW - scaled(IdleShrink);
            // Right ribbons grow from the left (book) edge; left ribbons grow from the right (book) edge.
            double x0 = side == BookTabSide.Right ? 0 : Bounds.InnerWidth - w;

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
            y += tabH + gap;
        }

        generateTexture(surface, ref texture);
        ctx.Dispose();
        surface.Dispose();
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
        double y = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (my >= y && my <= y + tabH)
            {
                onClick(i);
                args.Handled = true;
                return;
            }
            y += tabH + gap;
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
