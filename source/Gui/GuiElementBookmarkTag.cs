using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// The bookmark tag: a small paper slip at the top of the page bearing "+"
/// (pin this spread) or "−" (this spread is pinned; click to remove). The
/// physical book's only bookmark control — no hotkey, per Jeffrey's ruling
/// 2026-07-28. Composed only in physical mode on pinnable pages.
/// </summary>
public class GuiElementBookmarkTag : GuiElement
{
    private readonly bool pinned;
    private readonly Action onClick;
    private LoadedTexture texture;

    private static readonly double[] Fill = { 0.90, 0.85, 0.71, 1 };
    private static readonly double[] Border = { 0.38, 0.29, 0.19, 0.85 };
    private static readonly double[] Ink = { 0.32, 0.22, 0.12, 1.0 };

    public GuiElementBookmarkTag(ICoreClientAPI capi, ElementBounds bounds, bool pinned, Action onClick) : base(capi, bounds)
    {
        this.pinned = pinned;
        this.onClick = onClick;
        texture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        double w = Bounds.InnerWidth, h = Bounds.InnerHeight;

        var surface = new ImageSurface(Format.Argb32, (int)w + 1, (int)h + 1);
        var ctx = new Context(surface);
        ctx.SetSourceRGBA(0, 0, 0, 0);
        ctx.Paint();

        // A slip hanging down from the top edge, slightly skewed like the
        // side tabs; flat top (emerging from the pages), border all around.
        ctx.Translate(w / 2, 0);
        ctx.Rotate(-2.5 * Math.PI / 180.0);
        ctx.Translate(-w / 2, 0);

        double inset = scaled(2);
        ctx.NewPath();
        ctx.MoveTo(inset, 0);
        ctx.LineTo(w - inset, 0);
        ctx.LineTo(w - inset, h - scaled(3));
        ctx.LineTo(inset, h - scaled(1));
        ctx.ClosePath();
        ctx.SetSourceRGBA(Fill[0], Fill[1], Fill[2], Fill[3]);
        ctx.FillPreserve();
        ctx.LineWidth = scaled(1);
        ctx.SetSourceRGBA(Border[0], Border[1], Border[2], Border[3]);
        ctx.Stroke();

        // The glyph, drawn as bars (crisper than font glyphs at this size).
        double cxg = w / 2, cyg = h / 2 + scaled(1);
        double arm = Math.Min(w, h) * 0.22, thick = scaled(2);
        ctx.SetSourceRGBA(Ink[0], Ink[1], Ink[2], Ink[3]);
        ctx.Rectangle(cxg - arm, cyg - thick / 2, arm * 2, thick);
        ctx.Fill();
        if (!pinned)
        {
            ctx.Rectangle(cxg - thick / 2, cyg - arm, thick, arm * 2);
            ctx.Fill();
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
        onClick();
        args.Handled = true;
    }

    public override void Dispose()
    {
        base.Dispose();
        texture.Dispose();
    }
}
