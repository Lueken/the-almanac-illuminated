using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// The page-turn corners. At rest the book draws nothing here; faint ‹ › cues sit
/// in the bottom-outer corners (drawn by the dialog). Click a corner, or the
/// Prev/Next buttons, and the full five-frame turn plays over the book before the
/// spread swaps. The flip art is the whole book with blank pages, which reads as a
/// page physically turning.
/// Art: Wanderer's Sketchbook by JeanPierre, used by permission.
/// </summary>
public class GuiElementPageCorners : GuiElement
{
    private readonly Action<bool> onTurnComplete;   // forward?
    private readonly Action onTurnStart;
    private readonly System.Func<bool, bool> canTurn;      // forward? -> is there a page that way

    private readonly int[] flipRight = new int[5];
    private readonly int[] flipLeft = new int[5];
    private bool loaded;

    private bool animating;
    private bool animForward;
    private float animElapsed;

    private const float Total = 0.280f;
    // Per-frame cutoffs: frame 2 until .056, 3 until .140, 4 until .224, 5 until .280.
    private static readonly float[] Cutoffs = { 0.056f, 0.140f, 0.224f, 0.280f };

    private const double HitFrac = 0.22;       // corner activation, fraction of the book
    private const float OverZ = 600;

    public bool Busy => animating;

    public GuiElementPageCorners(ICoreClientAPI capi, ElementBounds bounds, Action<bool> onTurnComplete, Action onTurnStart, System.Func<bool, bool> canTurn) : base(capi, bounds)
    {
        this.onTurnComplete = onTurnComplete;
        this.onTurnStart = onTurnStart;
        this.canTurn = canTurn;
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();   // nothing static to draw; the art blits at render time
    }

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        for (int i = 0; i < 5; i++)
        {
            flipRight[i] = api.Render.GetOrLoadTexture(new AssetLocation($"almanacilluminated:textures/gui/flip/{i + 1}-5_right_flip.png"));
            flipLeft[i] = api.Render.GetOrLoadTexture(new AssetLocation($"almanacilluminated:textures/gui/flip/{i + 1}-5_left_flip.png"));
        }
    }

    /// <summary>Begin a turn. Forward turns the bottom-right corner; back turns the left.</summary>
    public void StartTurn(bool forward)
    {
        if (animating) return;
        animating = true;
        animForward = forward;
        animElapsed = 0;
        onTurnStart?.Invoke();
    }

    public override void RenderInteractiveElements(float dt)
    {
        EnsureLoaded();
        if (!animating) return;

        animElapsed += dt;
        if (animElapsed >= Total) { animating = false; onTurnComplete(animForward); return; }

        int tex = (animForward ? flipRight : flipLeft)[FrameAt(animElapsed)];
        if (tex != 0)
            api.Render.Render2DTexture(tex, (float)Bounds.renderX, (float)Bounds.renderY,
                (float)Bounds.InnerWidth, (float)Bounds.InnerHeight, OverZ);
    }

    private static int FrameAt(float t)
    {
        for (int i = 0; i < Cutoffs.Length; i++)
            if (t < Cutoffs[i]) return i + 1;   // frames 2..5 (index 1..4)
        return 4;
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (animating) { args.Handled = true; return; }
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
