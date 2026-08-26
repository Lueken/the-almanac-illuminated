using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// The page-turn corners: a hit region in each bottom-outer corner. Clicking one,
/// or the Prev/Next buttons, arms the modeled sheet turn and swaps the spread
/// under it. Nothing is drawn here — the turn is 3D geometry in the view. (Until
/// 0.2.0 this also played a five-frame PNG turn for the flat page.)
/// </summary>
public class GuiElementPageCorners : GuiElement
{
    private readonly Action<bool> onTurnComplete;   // forward?
    private readonly Action onTurnStart;
    private readonly System.Func<bool, bool> canTurn;      // forward? -> is there a page that way

    // The modeled sheet turn: the callback snapshots the old spread and arms the
    // sheet; the spread then swaps immediately, the animation carrying the transition.
    private readonly System.Func<bool, bool> physicalTurn;

    private const double HitFrac = 0.22;       // corner activation, fraction of the book

    /// <summary>Kept for callers that gate on a turn in flight. The modeled turn owns
    /// its own timing now, so this element is never busy.</summary>
    public bool Busy => false;

    public GuiElementPageCorners(ICoreClientAPI capi, ElementBounds bounds, Action<bool> onTurnComplete, Action onTurnStart, System.Func<bool, bool> canTurn, System.Func<bool, bool> physicalTurn) : base(capi, bounds)
    {
        this.onTurnComplete = onTurnComplete;
        this.onTurnStart = onTurnStart;
        this.canTurn = canTurn;
        this.physicalTurn = physicalTurn;
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();   // nothing static to draw; the art blits at render time
    }

    /// <summary>Begin a turn. Forward turns the bottom-right corner; back turns the left.
    /// The sheet carries the visual: snapshot first, then swap the spread immediately so
    /// the new page composes underneath it.</summary>
    public void StartTurn(bool forward)
    {
        physicalTurn(forward);
        onTurnStart?.Invoke();
        onTurnComplete(forward);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        int c = CornerAt(args.X, args.Y);
        if (c == 0) return;          // not a corner: leave the click for the page beneath
        bool forward = c > 0;
        if (!canTurn(forward)) return;   // dead end (first/last page): nothing happens
        StartTurn(forward);
        args.Handled = true;
    }

    /// <summary>Which bottom corner the point sits in: -1 left, +1 right, 0 neither.</summary>
    private int CornerAt(int mx, int my)
    {
        double rx = mx - Bounds.absX, ry = my - Bounds.absY;
        double w = Bounds.InnerWidth, h = Bounds.InnerHeight;
        if (rx < 0 || rx > w || ry < h * (1 - HitFrac) || ry > h) return 0;
        if (rx > w * (1 - HitFrac)) return 1;
        if (rx < w * HitFrac) return -1;
        return 0;
    }
}
