using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// The page-turn corners: a hit region in each bottom-outer corner. Clicking one arms
/// the modeled sheet turn and swaps the spread under it. This is the only page-turn
/// input in the book — there are no Prev/Next buttons, no hotkey and no wheel binding,
/// so the corner has to be both reachable and discoverable on its own.
///
/// Nothing is drawn here. The hover lift and the turn are both 3D geometry in the view;
/// this element only reports which corner the cursor is over. (Until 0.2.0 the flat page
/// played a five-frame PNG turn instead.)
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

    // Floor on the corner band. The fraction alone shrinks with the plate, and the plate
    // is smallest exactly where a player is least able to aim: a small window, or a high
    // GUI scale. Below this the corner stops being a target.
    private const double MinBandUn = 48;

    /// <summary>Which corner the cursor is over and could actually turn: -1 left,
    /// +1 right, 0 neither. The view reads this to lift that corner off the page.</summary>
    public int HoveredCorner { get; private set; }

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
        HoveredCorner = 0;          // the lift hands off to the swing
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

    /// <summary>Hover tracking for the corner lift. Never handles the event: moves have to
    /// keep reaching links, tabs and pinned scraps underneath.</summary>
    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        int c = CornerAt(args.X, args.Y);
        HoveredCorner = c != 0 && canTurn(c > 0) ? c : 0;
    }

    /// <summary>Drop the lift. The dialog calls this when the cursor leaves the page
    /// entirely, where no move event is dispatched and the state would otherwise stick.</summary>
    public void ClearHover() => HoveredCorner = 0;

    /// <summary>Which bottom corner the point sits in: -1 left, +1 right, 0 neither.
    /// The single definition of where a corner is — the click, the hover and the lift all
    /// come through here, so the cue cannot drift away from the target.</summary>
    private int CornerAt(int mx, int my)
    {
        double rx = mx - Bounds.absX, ry = my - Bounds.absY;
        double w = Bounds.InnerWidth, h = Bounds.InnerHeight;
        double bandW = Math.Max(w * HitFrac, scaled(MinBandUn));
        double bandH = Math.Max(h * HitFrac, scaled(MinBandUn));
        if (rx < 0 || rx > w || ry < h - bandH || ry > h) return 0;
        if (rx > w - bandW) return 1;
        if (rx < bandW) return -1;
        return 0;
    }
}
