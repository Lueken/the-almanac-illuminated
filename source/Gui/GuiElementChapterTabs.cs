using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A vertical strip of chapter ribbons hanging off the book's right edge. Each
/// ribbon is a rounded tab bearing the chapter title; the active one reads
/// brighter and protrudes a little further.
///
/// Drawn and hit-tested here directly. The native vertical tabs draw right-facing
/// tabs on one side of their bounds but test clicks on the other, so a right-edge
/// ribbon is visible in one place and clickable in another. This avoids that.
/// </summary>
public class GuiElementChapterTabs : GuiElement
{
    private readonly string[] labels;
    private readonly int activeIndex;
    private readonly Action<int> onClick;
    private readonly CairoFont fontActive;
    private readonly CairoFont fontIdle;

    private LoadedTexture texture;
    private double tabH, gap, rad, pad, tabW;

    private const double UnTabH = 27, UnGap = 5, UnPad = 11, UnRadius = 4;

    private static readonly double[] FillActive = { 0.93, 0.88, 0.76, 1 };
    private static readonly double[] FillIdle = { 0.74, 0.64, 0.48, 1 };
    private static readonly double[] Border = { 0.17, 0.11, 0.07, 1 };

    public GuiElementChapterTabs(ICoreClientAPI capi, ElementBounds bounds, string[] labels, int activeIndex, Action<int> onClick) : base(capi, bounds)
    {
        this.labels = labels;
        this.activeIndex = activeIndex;
        this.onClick = onClick;
        fontActive = CairoFont.WhiteSmallText().WithFontSize(15).WithColor(new[] { 0.13, 0.09, 0.05, 1.0 });
        fontIdle = CairoFont.WhiteSmallText().WithFontSize(15).WithColor(new[] { 0.20, 0.14, 0.08, 1.0 });
        texture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        tabH = scaled(UnTabH); gap = scaled(UnGap); rad = scaled(UnRadius); pad = scaled(UnPad);
        tabW = Bounds.InnerWidth - scaled(4);

        var surface = new ImageSurface(Format.Argb32, (int)Bounds.InnerWidth + 1, (int)Bounds.InnerHeight + 1);
        var ctx = new Context(surface);
        ctx.SetSourceRGBA(0, 0, 0, 0);
        ctx.Paint();

        double y = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            bool active = i == activeIndex;
            double w = active ? tabW : tabW - scaled(10);   // active reaches a touch further out

            TabPath(ctx, 0, y, w, tabH, rad);
            var fill = active ? FillActive : FillIdle;
            ctx.SetSourceRGBA(fill[0], fill[1], fill[2], fill[3]);
            ctx.FillPreserve();
            ctx.LineWidth = scaled(1.5);
            ctx.SetSourceRGBA(Border[0], Border[1], Border[2], Border[3]);
            ctx.Stroke();

            var font = active ? fontActive : fontIdle;
            font.SetupContext(ctx);
            var fe = font.GetFontExtents();
            ctx.MoveTo(pad, y + (tabH + fe.Ascent - fe.Descent) / 2);
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
        double y = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (mx >= 0 && mx <= tabW && my >= y && my <= y + tabH)
            {
                onClick(i);
                args.Handled = true;
                return;
            }
            y += tabH + gap;
        }
    }

    /// <summary>Flat left edge (attached to the book) with rounded right corners.</summary>
    private static void TabPath(Context ctx, double x, double y, double w, double h, double r)
    {
        ctx.NewPath();
        ctx.MoveTo(x, y);
        ctx.LineTo(x + w - r, y);
        ctx.Arc(x + w - r, y + r, r, -Math.PI / 2, 0);
        ctx.LineTo(x + w, y + h - r);
        ctx.Arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
        ctx.LineTo(x, y + h);
        ctx.ClosePath();
    }

    public override void Dispose()
    {
        base.Dispose();
        texture.Dispose();
    }
}
