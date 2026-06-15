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

    private static readonly double[] FillActive = { 0.93, 0.88, 0.76, 1 };
    private static readonly double[] FillIdle = { 0.74, 0.64, 0.48, 1 };
    private static readonly double[] Border = { 0.17, 0.11, 0.07, 1 };

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

            TabPath(ctx, side, x0, y, w, tabH, rad);
            var fill = active ? FillActive : FillIdle;
            ctx.SetSourceRGBA(fill[0], fill[1], fill[2], fill[3]);
            ctx.FillPreserve();
            ctx.LineWidth = scaled(1.5);
            ctx.SetSourceRGBA(Border[0], Border[1], Border[2], Border[3]);
            ctx.Stroke();

            var font = active ? fontActive : fontIdle;
            font.SetupContext(ctx);
            var fe = font.GetFontExtents();
            ctx.MoveTo(x0 + pad, y + (tabH + fe.Ascent - fe.Descent) / 2);
            ctx.ShowText(labels[i]);

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
        double mx = api.Input.MouseX - Bounds.absX;
        double my = api.Input.MouseY - Bounds.absY;
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
