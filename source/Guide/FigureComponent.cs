using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// An embedded illustration. Loads a PNG texture and bakes it onto the parchment
/// scaled to the text column, aspect preserved. "full" spans the column on its
/// own line; "left" and "right" float it at half width with text flowing beside
/// it (the richtext engine narrows the flow path around a floated component).
///
/// The image is painted in ComposeElements via a Cairo surface pattern, the same
/// path GuiElementImage uses, so there is no per-frame GL work and no premultiply
/// dance. A thin ink frame reads the plate as inset; a missing image shows a
/// faint placeholder so the author sees the gap.
/// </summary>
public class FigureComponent : RichTextComponentBase
{
    private readonly ImageSurface? image;
    private readonly bool loaded;
    private readonly double aspect;   // native height / native width

    private double drawW, drawH;      // resolved each layout pass in CalcBounds

    private static readonly double[] FrameInk = { 0.13, 0.09, 0.05, 0.55 };
    private static readonly double[] MissingFill = { 0.85, 0.80, 0.68, 1.0 };

    public FigureComponent(ICoreClientAPI api, string imagePath, string? align) : base(api)
    {
        string a = (align ?? "full").ToLowerInvariant();
        Float = a switch { "left" => EnumFloat.Left, "right" => EnumFloat.Right, _ => EnumFloat.None };
        VerticalAlign = EnumVerticalAlign.Top;

        if (!string.IsNullOrEmpty(imagePath))
        {
            var loc = new AssetLocation(imagePath).WithPathPrefixOnce("textures/");
            if (api.Assets.TryGet(loc) != null)
            {
                try { image = GuiElement.getImageSurfaceFromAsset(api, loc); }
                catch { image = null; }
            }
        }

        loaded = image != null && image.Width > 0 && image.Height > 0;
        aspect = loaded ? (double)image!.Height / image.Width : 0.6;

        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        double colW = cur.X2 - cur.X1;
        bool isFull = Float == EnumFloat.None;

        // Full spans the column (upscaling allowed, that is the documented intent).
        // A floated figure takes half the column but never upscales past native.
        if (isFull)
        {
            drawW = colW;
        }
        else
        {
            drawW = colW * 0.5;
            if (loaded && drawW > image!.Width) drawW = image.Width;
        }
        if (drawW < 1) drawW = colW;
        drawH = drawW * aspect;

        BoundsPerLine[0].Width = drawW;
        BoundsPerLine[0].Height = drawH;

        if (isFull)
        {
            offsetX += GuiElement.scaled(PaddingLeft);
            bool brk = offsetX > cur.X1 + 1;
            BoundsPerLine[0].X = cur.X1;
            BoundsPerLine[0].Y = lineY + (brk ? currentLineHeight : 0);
            nextOffsetX = cur.X2;
            return brk ? EnumCalcBoundsResult.Nextline : EnumCalcBoundsResult.Continue;
        }

        // Float: sit at the column edge; the engine wraps following text beside it.
        BoundsPerLine[0].X = Float == EnumFloat.Right ? cur.X2 - drawW : cur.X1;
        BoundsPerLine[0].Y = lineY;
        nextOffsetX = offsetX;
        return EnumCalcBoundsResult.Continue;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        if (b.Width < 1 || b.Height < 1) return;

        if (loaded && drawW > 0)
        {
            ctx.Save();
            var pattern = new SurfacePattern(image);
            // Map the destination rect onto the source image: user point (b.X, b.Y)
            // samples pattern (0,0); (b.X+drawW, b.Y+drawH) samples (imgW, imgH).
            var m = new Matrix();
            m.Scale(image!.Width / drawW, image.Height / drawH);
            m.Translate(-b.X, -b.Y);
            pattern.Matrix = m;
            pattern.Filter = Filter.Best;
            ctx.SetSource(pattern);
            ctx.Rectangle(b.X, b.Y, drawW, drawH);
            ctx.Fill();
            ctx.Restore();
            pattern.Dispose();
        }
        else
        {
            ctx.NewPath();
            ctx.Rectangle(b.X, b.Y, b.Width, b.Height);
            ctx.SetSourceRGBA(MissingFill[0], MissingFill[1], MissingFill[2], MissingFill[3]);
            ctx.Fill();
        }

        // Thin ink frame around the plate.
        ctx.NewPath();
        ctx.Rectangle(b.X, b.Y, b.Width, b.Height);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.SetSourceRGBA(FrameInk[0], FrameInk[1], FrameInk[2], FrameInk[3]);
        ctx.Stroke();
    }

    public override void Dispose()
    {
        base.Dispose();
        image?.Dispose();
    }
}
