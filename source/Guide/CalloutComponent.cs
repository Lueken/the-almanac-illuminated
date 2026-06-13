using Cairo;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A callout box: a flowing richtext run that paints a tinted panel with a
/// left accent bar behind its own wrapped lines, then draws the text on top.
/// Stays inline in the page's single richtext, the way the handbook renders
/// recipes and itemstacks. Color comes from the block variant, not the chapter
/// accent, so a warning stays warning-colored (see docs/SCHEMA.md section 5).
/// </summary>
public class CalloutComponent : RichTextComponent
{
    private readonly double[] box;
    private readonly double[] bar;

    public CalloutComponent(ICoreClientAPI api, string text, CairoFont font, double[] box, double[] bar)
        : base(api, text, font)
    {
        this.box = box;
        this.bar = bar;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        if (BoundsPerLine != null && BoundsPerLine.Length > 0)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
            foreach (var b in BoundsPerLine)
            {
                if (b.X < minX) minX = b.X;
                if (b.Y < minY) minY = b.Y;
                if (b.X + b.Width > maxX) maxX = b.X + b.Width;
                if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
            }

            double pad = GuiElement.scaled(7);
            double x = minX - pad, y = minY - pad;
            double w = (maxX - minX) + 2 * pad, h = (maxY - minY) + 2 * pad;

            ctx.SetSourceRGBA(box[0], box[1], box[2], box[3]);
            ctx.Rectangle(x, y, w, h);
            ctx.Fill();

            ctx.SetSourceRGBA(bar[0], bar[1], bar[2], 1);
            ctx.Rectangle(x, y, GuiElement.scaled(3), h);
            ctx.Fill();
        }

        base.ComposeElements(ctx, surface);
    }
}
