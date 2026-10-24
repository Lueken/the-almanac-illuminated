using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace AlmanacIlluminated;

/// <summary>
/// Input remapping contract between the tilted-page renderer and the book
/// dialog. When Active, the dialog suppresses its flat screen draw and routes
/// mouse events through TryRemap (screen point on the tilted page -> the
/// point in the dialog's flat 2D space the composers were laid out in).
/// </summary>
public interface IPhysicalMirror
{
    bool Active { get; }

    /// <summary>
    /// Maps a real screen position to dialog-space coordinates. rx/ry are
    /// always filled (clamped to the page) whenever the page plane is hit;
    /// the return value says whether the point was actually on the page.
    /// Callers use clamped output for mouse-up so drags released off-page
    /// still end cleanly.
    /// </summary>
    bool TryRemap(int x, int y, out int rx, out int ry);

    /// <summary>
    /// Raw page-space coordinates for a screen point (spread center = 0,0;
    /// page area = -1..1), unclamped — the zone above the top edge (pageY > 1)
    /// is where the bookmark scraps live and hit-test.
    /// </summary>
    bool TryRemapRaw(int x, int y, out float pageX, out float pageY);

    /// <summary>
    /// Draws the captured page book. Called from the dialog's own OnRenderGUI
    /// so the book layers with the dialog stack (above chat) instead of under
    /// every HUD like a loose ortho renderer would.
    /// </summary>
    void DrawLive(float deltaTime);

    /// <summary>
    /// The ambient shade every physical draw is multiplied by this frame (final
    /// per-channel multipliers, 1,1,1 = neutral). Set by the dialog right before
    /// DrawLive; the capture itself stays unshaded, so the shade is applied
    /// exactly once whichever presentation renders.
    /// </summary>
    void SetAmbientShade(float r, float g, float b);

    /// <summary>True while the stow-away close animation runs.</summary>
    bool Closing { get; }

    /// <summary>
    /// The stow has finished (or the view was dismissed outright), so the dialog
    /// may now actually close. The dialog refuses every close while this is false
    /// and stows instead, which is what keeps the game's own hotkey close from
    /// snapping the book shut mid-animation.
    /// </summary>
    bool StowComplete { get; }

    /// <summary>
    /// Plays the draw-out in reverse (book folds shut and drops away), then
    /// closes the dialog. Safe to call repeatedly.
    /// </summary>
    void RequestClose();

    /// <summary>
    /// Starts the physical page turn: snapshots the current spread, then a
    /// half-page sheet swings ~180° about the spine — old page on its face,
    /// new page on its back. Call BEFORE the spread recomposes; returns false
    /// when no capture exists yet (caller falls back to an instant swap).
    /// </summary>
    bool BeginPageTurn(bool forward);

    /// <summary>
    /// Starts a leafing-through animation for a jump of more than one spread: a
    /// rapid series of shortened bent turns that accelerates in, decelerates out,
    /// and lands on the target with one full turn. `spreads` is the distance in
    /// the current ordering; the flip count is derived from it and capped, because
    /// leafing is an impression, not a literal per-page playback. Returns false
    /// when no capture exists yet, and the caller simply gets the instant swap it
    /// would have had anyway.
    /// </summary>
    bool BeginFlipThrough(bool forward, int spreads);
}

/// <summary>
/// The physical Almanac presentation layer (design of record:
/// agentic-os projects/briefs/almanac-physical-book/brief.md, P5).
///
/// Live mode: the real book dialog renders into an offscreen framebuffer each
/// frame; the capture is split into left/right page quads mapped onto the
/// shapegen book model's page surfaces, and mouse input is remapped through
/// the inverse perspective back into flat dialog space.
///
/// The draw-out: on open, the book rises from below the view nearly
/// closed and the two halves fold open about the spine as it arrives — driven
/// procedurally with per-side fold matrices shared by the model halves and
/// their page quads, so pages and paper can never drift apart.
///
/// Matrix chain per side (right to left): geometry -> modelNorm (model->page
/// space, quads skip this) -> A (aspect stretch) -> F (fold about the spine,
/// in ISOTROPIC space — folding after the anisotropic stretch would skew the
/// turning page) -> mv (uniform scale, yaw, tilt, rise, distance) -> proj.
/// </summary>
public class PhysicalBookView : IRenderer, IPhysicalMirror
{
    private readonly ICoreClientAPI capi;

    // --- live mode ---
    private GuiDialogIlluminatedBook? liveDialog;
    private FrameBufferRef? fbo;
    private int fboColorTexId;
    private int fboW, fboH;
    private static readonly float[] ClearTransparent = { 0f, 0f, 0f, 0f };

    // Half-page quads: the spread splits at the spine (x = 0 in page space).
    private MeshRef? quadLeftRef, quadRightRef;
    private readonly float[] lastUvLeft = new float[4];
    private readonly float[] lastUvRight = new float[4];

    // Page turn (plan C): the old spread snapshots into a second FBO, then a
    // sheet (dedicated half quads, own UV state) swings about the spine while
    // the fresh spread lives underneath on the turning side.
    private FrameBufferRef? snapFbo;
    private int snapColorTexId;
    private int snapW, snapH;
    // The sheet is a BENT strip, not a rigid quad: a grid of unshared quads whose
    // vertices are rebuilt every frame from an arc-length-preserving bend curve
    // (see BuildBentSheet). Unshared vertices are what let the strip be split at
    // the column where the paper passes vertical, so the near half can draw the
    // outgoing page and the far half the incoming one in the same frame.
    private MeshRef? sheetStripRef, copyQuadRef;
    private MeshData? sheetStrip;
    private readonly int[] stripStart = new int[1];
    private readonly int[] stripSize = new int[1];
    private bool turning, turnForward;

    // Hover lift. The corner click is the book's only page-turn input — no buttons, no
    // hotkey, no wheel — and until 0.3.4 nothing announced it, so players hunted for it
    // or never found it at all. The live corner now lifts under the cursor the way a
    // loose page does. Both knobs are live via .almbook hover.
    public float HoverLift = 0.30f;         // curl at full hover
    public float HoverSeconds = 0.12f;      // settle time, either direction
    private int hoverCorner;                // -1 left, +1 right, 0 none
    private float hoverT;                   // 0..1, eased
    private float turnT;
    private const float TurnSeconds = 0.42f;

    /// <summary>Columns along the page (spine to free edge). Each column is one quad row
    /// tall per <see cref="TurnRows"/> band. Sixteen reads as a smooth curl at the size the
    /// book actually occupies on screen; the cost is 128 vertices rebuilt per frame, only
    /// while a turn is in flight.</summary>
    private const int TurnSegments = 16;

    /// <summary>Bands up the page. Three is the minimum that lets the free corners curl
    /// harder than the mid-height, which is the flex a held sheet actually shows.</summary>
    private const int TurnRows = 3;

    // Bend feel. All four are live knobs (.almbook bend|stiff|lead|corner) and all four
    // defaults below were set by Jeffrey tuning them in game on 2026-08-09, not derived.
    /// <summary>Peak extra tangent rotation from spine to free edge, radians. 0 restores the
    /// old rigid sheet exactly.</summary>
    public float BendMax = 1.5f;
    /// <summary>How the curl distributes along the page. Above 1 holds the root stiff and throws
    /// the bend at the free edge; below 1 does the opposite, bending hardest just off the spine
    /// and letting the rest of the sheet run straighter. The shipped 0.7 is the second, which in
    /// game reads as a page lifting from its binding rather than a tube unrolling.</summary>
    public float BendStiff = 0.7f;
    /// <summary>Where in the turn the curl peaks. Below 1 peaks early, so the page curls as it
    /// lifts and has relaxed by the time it lands.</summary>
    public float BendLead = 0.6f;
    /// <summary>Extra curl at the top and bottom edges relative to mid-height. Zero gives a
    /// pure cylinder.</summary>
    public float BendCorner = 0.05f;

    // Flip-through: a jump of more than one spread leafs through the book instead of
    // turning a single page. Intermediate sheets swing at constant speed (continuous
    // whipping, not a series of settled turns) and curl harder for the speed. Every
    // value here is a live knob, because the cadence is the part of this that has to be
    // felt rather than reasoned about.
    /// <summary>Seconds for the slowest sheet in a series (the first and last of the run-up).</summary>
    public float FlipSlow = 0.13f;
    /// <summary>Seconds for the quickest sheet, at the middle of the series.</summary>
    public float FlipFast = 0.06f;
    /// <summary>Curl multiplier on a mid-series sheet. Fast paper curls harder.</summary>
    public float FlipCurl = 1.35f;
    /// <summary>Sheets added per spread of distance. The count is 2 + spreads * this, capped.</summary>
    public float FlipScale = 0.6f;
    /// <summary>Ceiling on the sheets in one series, landing turn included.</summary>
    public int FlipMax = 7;
    /// <summary>Draw mid-series sheets as blank parchment rather than real page pixels. The
    /// fallback if real content strobes instead of reading as leafing.</summary>
    public bool FlipPaper;

    private const int FlipCountMin = 3;
    private int flipsLeft;          // sheets still to whip past before the landing turn
    private int flipsTotal;         // for the accelerate-in / decelerate-out cadence
    private float turnSeconds = TurnSeconds;   // duration of the swing in flight

    /// <summary>A blank sheet for the mid-series flips. Not an invented colour: it is the
    /// dominant tone of textures/shape/book/paper.png, the same sampled value ScrapBookmark
    /// carries at the middle of its Parchment ramp. Four texels of one colour, uploaded down
    /// the same 0xAARRGGBB path the bookmark faces already use, so the flat sheet needs no
    /// assumption about what the gui shader does with an untextured draw.</summary>
    private LoadedTexture? paperTex;
    private const int PaperFill = unchecked((int)0xFFEEE5CD);

    // Dialog-space capture rect (screen px), stored each render frame for remap.
    private float cropX, cropY, cropW, cropH;

    // --- book model ---
    // shapes/book-open.json, page-rect contract with book.py: x 1..31, z 2..18,
    // top y 2.0 (voxels). Tesselated as five selective meshes so the fold and
    // the painter's draw order (spine, bodies, pages, scraps) both work.
    public bool ModelEnabled = true;
    public bool QuadEnabled = true;   // .almbook quad 0 shows the bare model for diagnosis
    private MeshRef? spineRef, bodyLeftRef, bodyRightRef, scrapRef;
    private int atlasTexId;
    private bool bookMeshFailed;

    // Bookmark scrap slots: ONE neutral-cream scrap mesh instanced across the
    // top edge with per-slot offsets and paper tints. Slot i renders only when
    // pin i exists; the dialog's click zones derive from these same constants.
    public const int ScrapSlots = 8;
    public static readonly float[] ScrapSlotCenter =
        { -0.77f, -0.545f, -0.325f, -0.11f, 0.115f, 0.33f, 0.555f, 0.77f };   // page-a units, slight jitter
    public const float ScrapSlotHalfWidth = 0.085f;
    private const float ScrapBaseCenter = -0.67f;   // the mesh's baked center (book.py SCRAP_X0)
    private static readonly Vec4f[] ScrapTints =
    {
        new(0.82f, 0.40f, 0.36f, 1f),   // red
        new(0.44f, 0.57f, 0.78f, 1f),   // blue
        new(1.00f, 0.97f, 0.88f, 1f),   // cream
        new(0.57f, 0.67f, 0.44f, 1f),   // green
        new(0.88f, 0.70f, 0.36f, 1f),   // ochre
        new(0.65f, 0.46f, 0.61f, 1f),   // plum
        new(0.44f, 0.67f, 0.64f, 1f),   // teal
        new(0.62f, 0.50f, 0.38f, 1f),   // umber
    };
    private readonly float[] slotM = Mat4f.Create();
    private readonly float[] slotT = Mat4f.Create();

    // Side chapter tabs: paper slips protruding from the page edges, one baked
    // mesh per side instanced by row. Constants shared with the dialog's click
    // zones. Baked row center b = 0.825 (book.py TAB_Z0/TAB_H).
    public const float SideTabRow0 = 0.869f;    // baked row center b (book.py TAB_Z0 + TAB_H/2)
    public const float SideTabRowStep = 0.125f;
    public const float SideTabAInner = 0.96f;
    public const float SideTabAOuter = 1.12f;
    public const float SideTabHalfB = 0.052f;

    /// <summary>How far a named BOOKMARK reaches past the page edge. A letter slip stops at
    /// <see cref="SideTabAOuter"/>, which is a thumb-index tab and wants no more; a bookmark
    /// is a scrap shoved in to hold a place, and reaching out is how it earns room to carry
    /// a real name at full size instead of an abbreviation. Shared with the dialog, which
    /// hit-tests the two lengths apart.</summary>
    public const float NamedTabAOuter = 1.30f;

    private const float NamedTabHalfA = (NamedTabAOuter - SideTabAInner) / 2f;
    private const float NamedTabCenterA = (NamedTabAOuter + SideTabAInner) / 2f;

    /// <summary>Half-length the label may occupy on a bookmark, leaving the torn end and the
    /// tucked edge clear. Nearly three times what a letter slip could offer, which is what
    /// puts every name in the book back at full size.</summary>
    private const float NamedLabelMaxHalfA = 0.150f;

    /// <summary>Texel rows down a bookmark. The length follows from the tab's real aspect, so
    /// the texels stay square whatever the page is.</summary>
    private const int ScrapTexelsHigh = 12;

    /// <summary>Block half-heights for a two-line name: full size, and the floor it may shrink
    /// to. Both stay inside <see cref="SideTabHalfB"/> so a wrapped name cannot spill into the
    /// row above or below.</summary>
    private const float NamedLabelTwoLineHalfB = 0.046f;
    private const float NamedLabelTwoLineFloor = 0.040f;

    private static readonly Vec4f ScrapIdleTint = new(0.9f, 0.9f, 0.9f, 1f);

    // The hanging pin tags at the top edge. Their generated face uses the same grid as
    // the side bookmarks, sized so the texels come out square on a tag that is wider
    // than it is long: length over width in real units is 0.176 / (0.170 * page aspect).
    // A labelled pin is a RIBBON draped over the top edge, not a stubby tag: longer than
    // it is wide, hanging DOWN with its torn end at the foot, lapping just over the page
    // edge at b = 1. Drawn as ONE object. The modelled slip is not drawn underneath it at
    // all, because tinting that slip and covering its face left the part that stuck out
    // reading as a bare detached quad.
    private const float PinRibbonHalfW = 0.055f;
    private const float PinRibbonHalfLen = 0.160f;

    // Centre chosen so the ribbon DRAPES rather than towers. The page top is b = 1, and a
    // bookmark shows only a tongue above the edge with the body lying on the page: here
    // 0.12 above and 0.20 below. Centring at 1.140 instead put the head at b = 1.300, a
    // third of a half-page ABOVE the paper, and with only about 8% of screen height above
    // the book that ran off the top of the screen and read as a detached object floating
    // in the corner (reported 2026-08-09). Nothing about the anchoring was wrong; the
    // ribbon was simply reaching somewhere the screen does not go.
    private const float PinRibbonCenterB = 0.960f;

    // Grid sized so the texels come out square: length over width in real units is
    // (2 * HalfLen) / (2 * HalfW * page aspect), which is close to two to one.
    private const int PinRibbonTexelsLong = 27;
    private const int PinRibbonTexelsWide = 14;

    /// <summary>How far the label keeps clear of the ribbon's ends, in ribbon half-lengths.
    /// The foot is torn, so the name must not run into it.</summary>
    private const float PinRibbonLabelInset = 0.030f;

    private static Vec4f ToVec4(double[] rgba)
        => new((float)rgba[0], (float)rgba[1], (float)rgba[2], (float)rgba[3]);

    /// <summary>Row slots down one page edge, from SideTabRow0 to roughly its mirror
    /// at the foot. The bottom-anchored group hangs off the last of these.</summary>
    public const int SideTabRowCount = 15;

    /// <summary>
    /// Which row slot a tab occupies. The last <paramref name="bottomCount"/> tabs
    /// hang from the foot; everything else runs down from the top. ONE implementation
    /// for the renderer and the hit test, because a disagreement between those two is
    /// a tab you can see and cannot click.
    ///
    /// If the two groups would collide on a crowded strip, the anchor is abandoned and
    /// everything runs sequentially: overlapping slips are worse than an unanchored key.
    /// </summary>
    public static int SideTabRow(int index, int total, int bottomCount)
    {
        int topCount = total - bottomCount;
        if (bottomCount <= 0 || topCount + bottomCount > SideTabRowCount) return index;
        if (index < topCount) return index;
        return SideTabRowCount - 1 - (total - 1 - index);
    }

    /// <summary>The inverse of <see cref="SideTabRow"/>: the tab in a row slot, or -1 for an empty slot.</summary>
    public static int SideTabIndexForRow(int row, int total, int bottomCount)
    {
        int topCount = total - bottomCount;
        if (bottomCount <= 0 || topCount + bottomCount > SideTabRowCount)
            return row >= 0 && row < total ? row : -1;
        if (row >= 0 && row < topCount) return row;
        int fromBottom = SideTabRowCount - 1 - row;
        if (fromBottom >= 0 && fromBottom < bottomCount) return total - 1 - fromBottom;
        return -1;
    }
    private MeshRef? tabLeftRef, tabRightRef;
    private static readonly Vec4f TabTintA = new(0.96f, 0.92f, 0.80f, 1f);
    private static readonly Vec4f TabTintB = new(0.87f, 0.81f, 0.69f, 1f);
    private static readonly Vec4f TabTintActive = new(1f, 0.99f, 0.92f, 1f);

    // Scrap labels: the pinned page's name, reading down the hanging tab.
    // One texture per label text, cached; the quad's UVs are rotated 90° so
    // horizontal text textures run along the tab's length.
    private MeshRef? labelQuadRef, labelQuadHRef;
    private readonly Dictionary<string, LoadedTexture> labelTextures = new();
    private readonly Dictionary<string, string> fittedTabLabels = new();
    private CairoFont? labelFont;

    /// <summary>
    /// The label face, built on first use. Sturdy bold sans in cream with a dark
    /// outline: the tabs are mid-tone colors, so dark ink mudded out and a fine
    /// serif fell apart at tab size (in-game review 2026-07-28).
    /// </summary>
    private CairoFont LabelFont => labelFont ??= CairoFont.WhiteSmallText()
        .WithFontSize(28)
        .WithWeight(Cairo.FontWeight.Bold)
        .WithColor(new[] { 0.95, 0.91, 0.80, 1.0 })
        .WithStroke(new[] { 0.18, 0.12, 0.07, 1.0 }, 2.5);

    // Label geometry on a side tab, in isotropic page units: the nominal glyph
    // half-height, the floor it may shrink to, and the greatest half-length a
    // label may reach along the tab before it overruns the slip. The floor is
    // 11/15 of nominal, which is the flat ribbon's 15-to-11 point floor
    // (GuiElementChapterTabs.LabelFloorSize) expressed in this renderer's units.
    private const float SideTabLabelHalfB = 0.030f;
    private const float SideTabLabelHalfBFloor = 0.022f;
    private const float SideTabLabelMaxHalfA = 0.055f;

    /// <summary>
    /// The label as it can be drawn on a side tab without overrunning the slip or
    /// shrinking past the readable floor. Same rule as the flat ribbon: glyphs give
    /// way first, and only a title that still will not fit at the floor is clipped
    /// with one ellipsis. Here the "font size" is the quad's half-height, so the
    /// constraint lands on the label texture's aspect rather than on a point size.
    /// </summary>
    private string FitSideTabLabel(string label, float pageAspect)
        // Widest texture aspect that still sits inside maxHalfA once the glyphs have
        // shrunk all the way to the floor. The 3% margin absorbs any drift between
        // PredictAspect and the texture GenTextTexture actually hands back.
        => ClipToAspect(label, SideTabLabelMaxHalfA * pageAspect / SideTabLabelHalfBFloor * 0.97);

    /// <summary>What a bookmark's label reads as, and the block sizes its scale search may
    /// use. A wrapped name needs a taller block and a different floor, so the two travel
    /// together rather than being rediscovered at the draw site.</summary>
    private readonly struct NamedFit
    {
        public readonly string Text;
        public readonly float NominalHalfB, FloorHalfB;
        public NamedFit(string text, float nominal, float floor) { Text = text; NominalHalfB = nominal; FloorHalfB = floor; }
    }

    /// <summary>
    /// Fits a name to a bookmark, in the order the design calls for: full size on one line,
    /// which is what a bookmark this long gives every name in the book today; then wrapped
    /// to two lines; and only if a name defeats both, clipped. The scale search at the draw
    /// site does the shrinking between those rungs.
    /// </summary>
    private NamedFit FitNamedLabel(string label, float pageAspect)
    {
        double singleBudget = NamedLabelMaxHalfA * pageAspect / SideTabLabelHalfBFloor * 0.97;
        if (PredictAspect(label) <= singleBudget)
            return new NamedFit(label, SideTabLabelHalfB, SideTabLabelHalfBFloor);

        var (head, tail) = SplitTwo(label);
        if (tail != null)
        {
            double twoBudget = NamedLabelMaxHalfA * pageAspect / NamedLabelTwoLineFloor * 0.97;
            if (PredictAspectTwoLine(head, tail) <= twoBudget)
                return new NamedFit(head + "\n" + tail, NamedLabelTwoLineHalfB, NamedLabelTwoLineFloor);
        }

        return new NamedFit(ClipToAspect(label, singleBudget), SideTabLabelHalfB, SideTabLabelHalfBFloor);
    }

    /// <summary>Drops characters off the end until the label's predicted texture fits the
    /// aspect budget. Cached, because it runs per tab per frame otherwise.</summary>
    private string ClipToAspect(string label, double budget)
    {
        string key = label + "|" + (int)(budget * 100);
        if (fittedTabLabels.TryGetValue(key, out var cached)) return cached;

        string shown = label;
        if (PredictAspect(label) > budget)
        {
            shown = "…";
            for (int len = label.Length - 1; len > 0; len--)
            {
                string trial = label.Substring(0, len).TrimEnd() + "…";
                if (PredictAspect(trial) <= budget) { shown = trial; break; }
            }
        }
        fittedTabLabels[key] = shown;
        return shown;
    }

    /// <summary>Splits a name at the space nearest its middle, so "Smelter's Yard" breaks
    /// as "Smelter's" over "Yard". A name with no space cannot wrap and says so.</summary>
    private static (string head, string? tail) SplitTwo(string label)
    {
        int mid = label.Length / 2, best = -1;
        for (int i = 1; i < label.Length - 1; i++)
            if (label[i] == ' ' && (best < 0 || Math.Abs(i - mid) < Math.Abs(best - mid))) best = i;
        return best < 0 ? (label, null) : (label.Substring(0, best), label.Substring(best + 1));
    }

    /// <summary>
    /// The aspect (width over height) of the texture GenTextTexture would build for
    /// this text in the label font. Mirrors TextTextureUtil.GenOrUpdateTextTexture:
    /// the box comes from the font's own extents, plus the padding it adds on every
    /// side for a stroked font. Only ever used to choose where to clip a title. The
    /// drawn scale below still comes off the real texture, so drift here costs a
    /// character, never a broken layout.
    /// </summary>
    private double PredictAspect(string text)
    {
        double s = Vintagestory.API.Config.RuntimeEnv.GUIScale <= 0 ? 1 : Vintagestory.API.Config.RuntimeEnv.GUIScale;
        double pad = Math.Ceiling(LabelFont.StrokeWidth);
        double w = LabelFont.GetTextExtents(text).Width + s * (2 + 2 * pad);
        double h = LabelFont.GetFontExtents().Height + s * (1 + 2 * pad);
        return h <= 0 ? 1 : w / h;
    }

    /// <summary>
    /// The same prediction for a two-line label. GetLabelTexture sizes a multi-line texture
    /// from the widest line and the line height with its own fixed padding, not through
    /// AutoBoxSize, so this mirrors that branch instead of the single-line one.
    /// </summary>
    private double PredictAspectTwoLine(string a, string b)
    {
        double maxW = Math.Max(LabelFont.GetTextExtents(a).Width, LabelFont.GetTextExtents(b).Width);
        double lineH = LabelFont.GetFontExtents().Height;
        double w = (int)maxW + 12;
        double h = (int)(lineH * 2) + 8;
        return h <= 0 ? 1 : w / h;
    }

    // One generated bookmark face per label, side and texel length. Small textures,
    // built once, nearest-filtered so the texels stay square-edged on the tilted page.
    private readonly Dictionary<string, LoadedTexture> scrapTextures = new();
    private readonly Dictionary<ScrapMaterial, CairoFont> scrapFonts = new();

    /// <summary>
    /// The bookmark face for this label on this side, uploaded as pixel art. The grid
    /// length follows the tab's real on-screen aspect so the texels come out square, and
    /// the free (torn) end faces away from the book on whichever side it hangs.
    /// </summary>
    private LoadedTexture ScrapTexture(string label, ScrapSkin skin, bool leftSide)
    {
        // The label belongs in the key. Two chapters that drew the same material at the
        // same size are still two different scraps, and keying on the material alone
        // handed the second one the first one's tear.
        string key = label + "|" + skin.Width + "x" + skin.Height + (leftSide ? "|L" : "|R");
        if (scrapTextures.TryGetValue(key, out var hit)) return hit;

        // The grid is already 0xAARRGGBB, which on a little-endian int array is the byte
        // order GL_BGRA wants, so it uploads as-is. linearMag off is the whole point:
        // a smoothed bookmark would be the one thing that gives the pixel art away.
        var tex = new LoadedTexture(capi, 0, skin.Width, skin.Height);
        capi.Render.LoadOrUpdateTextureFromBgra(skin.Argb, false, 0, ref tex);
        scrapTextures[key] = tex;
        return tex;
    }

    /// <summary>The label face for a bookmark: dark ink on the pale materials, cream on
    /// leather, each stroked with the other end of its own ramp so the name holds up at
    /// tab size against a mottled ground.</summary>
    private CairoFont ScrapFont(ScrapSkin skin)
    {
        if (scrapFonts.TryGetValue(skin.Material, out var hit)) return hit;
        var font = CairoFont.WhiteSmallText()
            .WithFontSize(28)
            .WithWeight(Cairo.FontWeight.Bold)
            .WithColor(skin.Ink)
            .WithStroke(skin.InkStroke, 2.5);
        scrapFonts[skin.Material] = font;
        return font;
    }

    private LoadedTexture GetLabelTexture(string label, bool wrap = false, ScrapSkin? skin = null)
    {
        string key = (wrap ? "w|" : "s|") + (skin == null ? "" : skin.Material + "|") + label;
        if (labelTextures.TryGetValue(key, out var tex)) return tex;
        var font = skin == null ? LabelFont : ScrapFont(skin);

        // Every caller now fits its own text before asking for a texture: side tabs
        // through FitSideTabLabel or FitNamedLabel, pin tags through FitPinLabel, which
        // is also what puts the newline in a wrapped one. The text is taken as given
        // here; `wrap` survives only to keep the two callers' cache entries apart.
        string shown = label;

        if (shown.Contains('\n'))
        {
            var lines = shown.Split('\n');
            double maxW = 0;
            foreach (var l in lines) maxW = Math.Max(maxW, font.GetTextExtents(l).Width);
            double lineH = font.GetFontExtents().Height;
            tex = capi.Gui.TextTexture.GenTextTexture(shown, font,
                (int)maxW + 12, (int)(lineH * lines.Length) + 8, null, EnumTextOrientation.Center);
        }
        else
        {
            tex = capi.Gui.TextTexture.GenTextTexture(shown, font);
        }
        labelTextures[key] = tex;
        return tex;
    }

    /// <summary>Narrowest the label may get across a hanging pin tag before it stops being
    /// readable. The budget below is this floor read backwards into a texture aspect.</summary>
    private const float PinLabelFloorHalfW = 0.020f;

    /// <summary>
    /// A pin tag's name, wrapped to two lines (two columns once rotated onto the tag) so a
    /// long title keeps readable glyphs instead of thinning to fit. Truncation is the LAST
    /// step, not the first: the old rule cut every name past thirteen characters whether or
    /// not it would have fitted, which is the same mistake the side tabs had.
    /// </summary>
    private string FitPinLabel(string label, float pageAspect)
    {
        // Widest texture the tag can carry before the label thins past the floor:
        // halfW = halfLen * (texH / texW) / pageAspect, solved for texW / texH.
        double budget = (PinRibbonHalfLen - PinRibbonLabelInset) / (PinLabelFloorHalfW * pageAspect) * 0.97;

        var (head, tail) = SplitTwo(label);
        if (tail == null) return ClipToAspect(label, budget);   // one word, nothing to wrap
        if (PredictAspectTwoLine(head, tail) <= budget) return head + "\n" + tail;

        // Wrapped and still too wide. Shorten whichever line is the long one, so the
        // name loses its tail rather than its beginning.
        string a = head, b = tail;
        for (int guard = 0; guard < 64 && PredictAspectTwoLine(a, b) > budget; guard++)
        {
            if (LabelFont.GetTextExtents(a).Width >= LabelFont.GetTextExtents(b).Width)
                a = Shorten(a);
            else b = Shorten(b);
            if (a.Length <= 1 && b.Length <= 1) break;
        }
        return a + "\n" + b;
    }

    /// <summary>Drops one character and marks the cut, keeping a single ellipsis.</summary>
    private static string Shorten(string s)
    {
        string bare = s.EndsWith("…") ? s.Substring(0, s.Length - 1) : s;
        if (bare.Length <= 1) return "…";
        return bare.Substring(0, bare.Length - 1).TrimEnd() + "…";
    }

    // Knobs. Defaults = Jeffrey's approved envelope (2026-07-28 rounds).
    public float TiltDeg = 12f;
    public float YawDeg = 0f;
    public float Dist = 1.0f;
    public float Scale = 0.37f;
    public float FovDeg = 50f;
    public float HeightOff = 0.06f;

    public bool Visible { get; private set; }
    public bool LiveActive => Visible && liveDialog != null;
    bool IPhysicalMirror.Active => LiveActive && !CaptureUnavailable;

    /// <summary>
    /// The offscreen capture could not be set up or run on this machine, so the
    /// physical book cannot be drawn. Latched for the session: a driver that
    /// refused the framebuffer once will refuse it every frame, and retrying
    /// each frame would only turn one failure into a log flood. The host reads
    /// this and reads flat instead — the flat dialog is the whole book minus
    /// its presentation, so nothing is lost but the tilt.
    /// </summary>
    public bool CaptureUnavailable { get; private set; }

    /// <summary>Raised once, on the frame the capture gives out, with a short reason.</summary>
    public Action<string>? OnCaptureUnavailable;

    private void FailCapture(string reason)
    {
        if (CaptureUnavailable) return;
        CaptureUnavailable = true;
        liveDialog = null;
        Visible = false;
        closing = false;
        StowComplete = true;
        OnCaptureUnavailable?.Invoke(reason);
    }

    // Draw-out animation. openT runs 0..1 over DrawOutSeconds; rise, fold and
    // fade each ease over their own slice of it.
    private const float DrawOutSeconds = 0.55f;
    private const float StowSeconds = 0.4f;   // the reverse: fold shut and drop away, a touch snappier
    private const float FoldStartDeg = 55f;   // 75° lunged at the camera on arrival (frame review 2026-07-28)
    private const float SpinePivotZ = (0.3f - 2f) / 8f;   // spine line in page space, from book.py
    private float openT;
    private bool closing;

    public bool Closing => closing;

    /// <summary>See IPhysicalMirror.StowComplete. Latched when the stow lands and when the
    /// view is dismissed outright; cleared on the next open.</summary>
    public bool StowComplete { get; private set; }

    public void RequestClose()
    {
        if (!LiveActive || closing) return;
        closing = true;
    }


    // Matrices. mv is uniform-scale view placement; A stretches page space to
    // the capture aspect; fold matrices are per side. remapM = mv*A is the
    // settled (fold-free) page plane the input remap inverts.
    private readonly float[] proj = Mat4f.Create();
    private readonly float[] mv = Mat4f.Create();
    private readonly float[] aspectM = Mat4f.Create();
    private readonly float[] foldLeft = Mat4f.Create();
    private readonly float[] foldRight = Mat4f.Create();
    private readonly float[] remapM = Mat4f.Create();
    private readonly float[] mvQuadLeft = Mat4f.Create();
    private readonly float[] mvQuadRight = Mat4f.Create();
    private readonly float[] mvModelLeft = Mat4f.Create();
    private readonly float[] mvModelRight = Mat4f.Create();
    private readonly float[] mvModelSpine = Mat4f.Create();
    private readonly float[] tmpM = Mat4f.Create();
    private readonly float[] modelNorm = BuildModelNorm();
    private float easeFade;

    public double RenderOrder => 0.98;
    public int RenderRange => 1;

    public PhysicalBookView(ICoreClientAPI capi)
    {
        this.capi = capi;
        quadLeftRef = capi.Render.UploadMesh(BuildQuad(-1f, 0f, 0f, 0f, 0.5f, 1f));
        quadRightRef = capi.Render.UploadMesh(BuildQuad(0f, 1f, 0.5f, 0f, 1f, 1f));

        // Label quad: texture rotated 90° clockwise (book-spine convention) —
        // text starts at the tab's top and reads downward, glyph tops facing
        // right. u = (1-y)/2, v = (1-x)/2; the first attempt inverted v and
        // produced mirror-writing.
        var labelMesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: false, withFlags: false);
        labelMesh.AddVertex(-1f, -1f, 0f, 1f, 1f);
        labelMesh.AddVertex(1f, -1f, 0f, 1f, 0f);
        labelMesh.AddVertex(1f, 1f, 0f, 0f, 0f);
        labelMesh.AddVertex(-1f, 1f, 0f, 0f, 1f);
        labelMesh.AddIndex(0); labelMesh.AddIndex(1); labelMesh.AddIndex(2);
        labelMesh.AddIndex(0); labelMesh.AddIndex(2); labelMesh.AddIndex(3);
        labelQuadRef = capi.Render.UploadMesh(labelMesh);

        // The page-turn sheet: one strip of unshared quads, allocated once at a
        // fixed size and rewritten in place each frame of a turn. Vertex and index
        // counts never change, so UpdateMesh always fits the buffer it was given.
        sheetStrip = BuildStripTemplate();
        sheetStripRef = capi.Render.UploadMesh(sheetStrip);

        // The 1:1 copy quad (identity UV orientation for FBO->FBO blits).
        copyQuadRef = capi.Render.UploadMesh(BuildQuad(-1f, 1f, 0f, 1f, 1f, 0f));

        // Horizontal label quad for the side tabs (standard upright text).
        var labelHMesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: false, withFlags: false);
        labelHMesh.AddVertex(-1f, -1f, 0f, 0f, 1f);
        labelHMesh.AddVertex(1f, -1f, 0f, 1f, 1f);
        labelHMesh.AddVertex(1f, 1f, 0f, 1f, 0f);
        labelHMesh.AddVertex(-1f, 1f, 0f, 0f, 0f);
        labelHMesh.AddIndex(0); labelHMesh.AddIndex(1); labelHMesh.AddIndex(2);
        labelHMesh.AddIndex(0); labelHMesh.AddIndex(2); labelHMesh.AddIndex(3);
        labelQuadHRef = capi.Render.UploadMesh(labelHMesh);
    }

    /// <summary>Model-space (mesh units, voxel/16) -> page space (-1..1 across the
    /// spread). Book lies flat Y-up; the view faces it: mesh x -> x', mesh z -> -y',
    /// mesh y -> z'.</summary>
    private static float[] BuildModelNorm()
    {
        const float cx = 16f / 16f, cy = 2f / 16f, cz = 10f / 16f;   // page-rect center
        const float hx = 15f / 16f, hz = 8f / 16f;                   // half extents
        var m = Mat4f.Create();
        m[0] = 1f / hx; m[1] = 0f; m[2] = 0f;
        m[4] = 0f; m[5] = 0f; m[6] = 1f / hz;
        m[8] = 0f; m[9] = -1f / hz; m[10] = 0f;
        m[12] = -cx / hx; m[13] = cz / hz; m[14] = -cy / hz;
        return m;
    }

    /// <summary>
    /// A page-space quad spanning [x0..x1] x [-1..1] with explicit UVs. vTop is
    /// the V sampled at the TOP edge: 0 for PNGs (row 0 = image top), flipped
    /// for FBO captures (GL framebuffer row 0 = bottom).
    /// </summary>
    private static MeshData BuildQuad(float x0, float x1, float u0, float vTop, float u1, float vBot)
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: false, withFlags: false);
        mesh.AddVertex(x0, -1f, 0f, u0, vBot);
        mesh.AddVertex(x1, -1f, 0f, u1, vBot);
        mesh.AddVertex(x1, 1f, 0f, u1, vTop);
        mesh.AddVertex(x0, 1f, 0f, u0, vTop);
        mesh.AddIndex(0); mesh.AddIndex(1); mesh.AddIndex(2);
        mesh.AddIndex(0); mesh.AddIndex(2); mesh.AddIndex(3);
        return mesh;
    }

    private void UpdateQuadUv(float[] last, MeshRef quad, float x0, float x1, float u0, float vTop, float u1, float vBot)
    {
        if (last[0] == u0 && last[1] == vTop && last[2] == u1 && last[3] == vBot) return;
        last[0] = u0; last[1] = vTop; last[2] = u1; last[3] = vBot;
        capi.Render.UpdateMesh(quad, BuildQuad(x0, x1, u0, vTop, u1, vBot));
    }

    // --- the bent page ----------------------------------------------------

    private const int StripQuads = TurnSegments * (TurnRows - 1);

    /// <summary>
    /// The turning sheet's fixed topology: one quad per (column, band) with its own
    /// four vertices. Nothing is shared, which costs 128 vertices and buys two things
    /// the shared-vertex version cannot have — a UV discontinuity at the column where
    /// the paper passes vertical (recto on one side of it, verso on the other), and a
    /// clean index split so those two halves can be drawn with different textures.
    /// Positions and UVs are meaningless here; every frame of a turn overwrites them.
    /// </summary>
    private static MeshData BuildStripTemplate()
    {
        var mesh = new MeshData(StripQuads * 4, StripQuads * 6,
            withNormals: false, withUv: true, withRgba: false, withFlags: false);
        for (int c = 0; c < TurnSegments; c++)
        {
            for (int b = 0; b < TurnRows - 1; b++)
            {
                int v = mesh.VerticesCount;
                for (int k = 0; k < 4; k++) mesh.AddVertex(0f, 0f, 0f, 0f, 0f);
                mesh.AddIndex(v); mesh.AddIndex(v + 1); mesh.AddIndex(v + 2);
                mesh.AddIndex(v); mesh.AddIndex(v + 2); mesh.AddIndex(v + 3);
            }
        }
        return mesh;
    }

    /// <summary>
    /// Rewrites the sheet as a bent page and returns the column at which it passes
    /// vertical (0 = wholly past it, <see cref="TurnSegments"/> = not yet at all).
    ///
    /// The model is a developable bend with vertical generators: walk the page from
    /// the spine to the free edge in equal arc steps, turning the tangent as you go.
    /// Arc length is therefore preserved exactly, which is the property that makes it
    /// read as paper rather than as a stretched sheet. The tangent is
    ///
    ///     theta(s) = min(turnAngle + curl * s^BendStiff, pi)
    ///
    /// The clamp at pi is the landing page: paper cannot pass through the sheet it
    /// is falling onto. Without it the leading tip keeps rotating past flat in the
    /// last fifth of the swing and sinks under the page beneath it (caught by the
    /// profile harness, where the tip reached 198 degrees at t=0.75). Clamped, the
    /// tip simply lies down along the landing plane, which is what settling looks
    /// like, and arc length is untouched either way.
    ///
    /// so the root leaves the spine at exactly the turn angle (a bound page is held
    /// there and cannot do otherwise), the curvature grows toward the free edge, and
    /// the tip always runs ahead of the root, which is the curl leading the turn.
    /// `curl` is zero at both ends of the swing, so the page lands dead flat.
    ///
    /// Geometry comes out in real proportions (x already multiplied by the page's
    /// true half-width), so the caller feeds it mv directly and the aspect matrix
    /// stays out of it. Bending after an anisotropic stretch would have skewed the
    /// curve; baking the stretch into the vertices avoids the question entirely.
    /// </summary>
    private int BuildBentSheet(float turnAngle, float curl, float pageHalfW, int dir,
                               float uSpine, float uOuterFront, float uOuterBack,
                               float vTop, float vBot, float lowBias = 0f)
    {
        var mesh = sheetStrip!;
        float stiff = Math.Max(0.2f, BendStiff);
        float ds = 1f / TurnSegments;

        // Per-band arc walk. Row r gets its own curl so the free corners flex harder
        // than the mid-height; at BendCorner 0 every row shares one curve and the
        // surface is a pure cylinder.
        Span<float> px = stackalloc float[TurnRows * (TurnSegments + 1)];
        Span<float> pz = stackalloc float[TurnRows * (TurnSegments + 1)];
        for (int r = 0; r < TurnRows; r++)
        {
            float y = TurnRows == 1 ? 0f : -1f + 2f * r / (TurnRows - 1);
            // Rows run y = -1 at the foot of the page to y = +1 at the head. The swing
            // weights both free corners alike; the hover lift wants only the bottom one,
            // because that is the corner the cursor is on. lowBias blends between them,
            // and at 0 this is exactly the symmetric curve the turn has always used.
            float sym = 1f + BendCorner * y * y;
            float low = (1f - y) * (1f - y) * 0.25f;     // 1 at the foot, 0 at the head
            float k = curl * (sym + lowBias * (low - sym));
            float x = 0f, z = 0f;
            int b0 = r * (TurnSegments + 1);
            px[b0] = 0f; pz[b0] = 0f;
            for (int c = 0; c < TurnSegments; c++)
            {
                // Midpoint of the step: second-order accurate, and free here.
                float sm = (c + 0.5f) * ds;
                float th = MathF.Min(turnAngle + k * MathF.Pow(sm, stiff), MathF.PI);
                x += MathF.Cos(th) * ds;
                z += MathF.Sin(th) * ds;
                px[b0 + c + 1] = x * pageHalfW * dir;
                pz[b0 + c + 1] = z * pageHalfW;
            }
        }

        // Where the mid-height row passes vertical. theta is monotone in s while curl
        // is positive, so the first crossing is the only one. Deciding it on one row
        // keeps the seam a straight column instead of a ragged edge.
        float midCurl = curl;
        int splitCol = TurnSegments;
        for (int c = 0; c <= TurnSegments; c++)
        {
            float th = turnAngle + midCurl * MathF.Pow(c * ds, stiff);
            if (th >= MathF.PI / 2f) { splitCol = c; break; }
        }

        int vi = 0;
        for (int c = 0; c < TurnSegments; c++)
        {
            // A quad belongs to the face it shows: front quads ramp their u toward
            // the outgoing page's outer edge, back quads toward the incoming one's.
            float uOuter = c < splitCol ? uOuterFront : uOuterBack;
            float u0 = uSpine + (uOuter - uSpine) * (c * ds);
            float u1 = uSpine + (uOuter - uSpine) * ((c + 1) * ds);

            for (int b = 0; b < TurnRows - 1; b++)
            {
                float y0 = -1f + 2f * b / (TurnRows - 1);
                float y1 = -1f + 2f * (b + 1) / (TurnRows - 1);
                float v0 = vBot + (vTop - vBot) * ((y0 + 1f) * 0.5f);
                float v1 = vBot + (vTop - vBot) * ((y1 + 1f) * 0.5f);
                int i0 = b * (TurnSegments + 1) + c;
                int i1 = (b + 1) * (TurnSegments + 1) + c;

                SetVertex(mesh, vi++, px[i0], y0, pz[i0], u0, v0);
                SetVertex(mesh, vi++, px[i0 + 1], y0, pz[i0 + 1], u1, v0);
                SetVertex(mesh, vi++, px[i1 + 1], y1, pz[i1 + 1], u1, v1);
                SetVertex(mesh, vi++, px[i1], y1, pz[i1], u0, v1);
            }
        }

        capi.Render.UpdateMesh(sheetStripRef!, mesh);
        return splitCol;
    }

    private static void SetVertex(MeshData mesh, int i, float x, float y, float z, float u, float v)
    {
        mesh.xyz[i * 3] = x; mesh.xyz[i * 3 + 1] = y; mesh.xyz[i * 3 + 2] = z;
        mesh.Uv[i * 2] = u; mesh.Uv[i * 2 + 1] = v;
    }

    /// <summary>How hard the sheet is curling at progress `t` through its swing. Zero at both
    /// ends so the page leaves and lands flat; <see cref="BendLead"/> below 1 puts the peak
    /// early, so the curl leads the turn and has relaxed by the landing.</summary>
    private float CurlAt(float t, bool intermediate)
    {
        float shaped = MathF.Pow(GameMath.Clamp(t, 0f, 1f), Math.Max(0.1f, BendLead));
        float k = BendMax * MathF.Sin(MathF.PI * shaped);
        return intermediate ? k * FlipCurl : k;
    }

    /// <summary>The blank parchment sheet, built on first use. Uploaded exactly the way the
    /// bookmark faces are (see ScrapTexture): the ints are 0xAARRGGBB, which on a little-endian
    /// int array is the byte order GL wants, so the sampled value goes up untouched.</summary>
    private int PaperTexId()
    {
        if (paperTex == null)
        {
            paperTex = new LoadedTexture(capi, 0, 2, 2);
            capi.Render.LoadOrUpdateTextureFromBgra(
                new[] { PaperFill, PaperFill, PaperFill, PaperFill }, false, 0, ref paperTex);
        }
        return paperTex.TextureId;
    }

    // --- mode control -----------------------------------------------------

    /// <summary>Enter live mode. Call before the dialog opens so its first frame is already captured, not flat-drawn.</summary>
    public void ShowLive(GuiDialogIlluminatedBook dialog)
    {
        if (CaptureUnavailable) return;
        liveDialog = dialog;
        Visible = true;
        openT = 0f;
        StowComplete = false;
    }

    /// <summary>Leave live mode (dialog closed).</summary>
    public void HideLive()
    {
        liveDialog = null;
        Visible = false;
        StowComplete = true;   // nothing left to animate; a close now is just a close
    }

    public string Describe() =>
        $"tilt {TiltDeg:0.#}°, yaw {YawDeg:0.#}°, dist {Dist:0.##}, scale {Scale:0.##}, fov {FovDeg:0.#}°, height {HeightOff:0.##}, " +
        $"bend {BendMax:0.##}, stiff {BendStiff:0.##}, lead {BendLead:0.##}, corner {BendCorner:0.##}, " +
        $"hover {HoverLift:0.##}, hoverease {HoverSeconds:0.###}, " +
        $"flipslow {FlipSlow:0.###}, flipfast {FlipFast:0.###}, flipcurl {FlipCurl:0.##}, " +
        $"flipscale {FlipScale:0.##}, flipmax {FlipMax}, flippaper {(FlipPaper ? 1 : 0)}" +
        (GuiDialogIlluminatedBook.FlipThroughParked
            ? " (leafing-through is parked, so the flip knobs set and save but do nothing)"
            : "");

    public void Reset()
    {
        TiltDeg = 12f; YawDeg = 0f; Dist = 1.0f; Scale = 0.37f; FovDeg = 50f; HeightOff = 0.06f;
        BendMax = 1.5f; BendStiff = 0.7f; BendLead = 0.6f; BendCorner = 0.05f;
        HoverLift = 0.30f; HoverSeconds = 0.12f;
        FlipSlow = 0.13f; FlipFast = 0.06f; FlipCurl = 1.35f;
        FlipScale = 0.6f; FlipMax = 7; FlipPaper = false;
    }

    // --- meshes -----------------------------------------------------------

    private class ShapeTexSource : ITexPositionSource
    {
        private readonly ICoreClientAPI capi;
        private readonly Shape shapeDoc;
        public int AtlasTextureId;

        public ShapeTexSource(ICoreClientAPI capi, Shape shapeDoc)
        {
            this.capi = capi;
            this.shapeDoc = shapeDoc;
        }

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public TextureAtlasPosition this[string code]
        {
            get
            {
                AssetLocation? path = null;
                shapeDoc.Textures?.TryGetValue(code, out path);
                TextureAtlasPosition? pos = null;
                if (path != null) capi.BlockTextureAtlas.GetOrInsertTexture(path, out _, out pos);
                pos ??= capi.BlockTextureAtlas.UnknownTexturePosition;
                AtlasTextureId = pos.atlasTextureId;
                return pos;
            }
        }
    }

    private void EnsureBookMesh()
    {
        if (spineRef != null || bookMeshFailed) return;
        try
        {
            var shapeDoc = Shape.TryGet(capi, new AssetLocation("almanacilluminated", "shapes/book-open.json"));
            if (shapeDoc == null)
            {
                IlluminatedLogger.Warn(capi, "physical", "book-open.json shape missing; rendering pages only");
                bookMeshFailed = true;
                return;
            }
            if (coverOverride != null && shapeDoc.Textures != null)
            {
                // The boards, the spine and the gutter all bind "cover"; overriding the
                // shape's own entry re-skins them together and leaves the pages alone.
                shapeDoc.Textures["cover"] = coverOverride.Clone();
            }
            var texSource = new ShapeTexSource(capi, shapeDoc);
            MeshRef Tess(string label, params string[] names)
            {
                capi.Tesselator.TesselateShape($"almanacilluminated:book-{label}", shapeDoc, out var mesh, texSource,
                    null, 0, 0, 0, null, names);
                return capi.Render.UploadMesh(mesh);
            }
            spineRef = Tess("spine", "gutter");
            bodyLeftRef = Tess("body-l", "cover-left", "pages-left");
            bodyRightRef = Tess("body-r", "cover-right", "pages-right");
            scrapRef = Tess("scrap", "scrap-0");
            tabLeftRef = Tess("tab-l", "sidetab-left");
            tabRightRef = Tess("tab-r", "sidetab-right");
            atlasTexId = texSource.AtlasTextureId;
            IlluminatedLogger.Info(capi, "physical", $"Book model tesselated in halves (atlas tex {atlasTexId})");
        }
        catch (Exception e)
        {
            IlluminatedLogger.Warn(capi, "physical", $"Book model tesselation failed ({e.Message}); rendering pages only");
            bookMeshFailed = true;
        }
    }

    private void EnsureFbo()
    {
        int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
        if (fbo != null && fboW == w && fboH == h) return;
        DisposeFbo();

        var colorTex = new RawTexture { Width = w, Height = h };   // Rgba8, Linear, ClampToEdge defaults
        var depthTex = new RawTexture
        {
            Width = w,
            Height = h,
            PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32,
            PixelFormat = EnumTexturePixelFormat.DepthComponent,
        };
        fbo = capi.Render.CreateFrameBuffer(new FramebufferAttrs("almanacilluminated-book", w, h)
        {
            Attachments = new[]
            {
                new FramebufferAttrsAttachment { AttachmentType = EnumFramebufferAttachment.ColorAttachment0, Texture = colorTex },
                new FramebufferAttrsAttachment { AttachmentType = EnumFramebufferAttachment.DepthAttachment, Texture = depthTex },
            },
        });
        fboColorTexId = colorTex.TextureId;   // platform fills the generated GL id back in
        fboW = w; fboH = h;
        IlluminatedLogger.Info(capi, "physical", $"Capture framebuffer (re)created: {w}x{h}, colorTex {fboColorTexId}");
    }

    private void DisposeFbo()
    {
        if (fbo == null) return;
        capi.Render.DestroyFrameBuffer(fbo);
        fbo = null;
        fboColorTexId = 0;
    }

    private void EnsureSnapFbo()
    {
        int w = fboW, h = fboH;
        if (snapFbo != null && snapW == w && snapH == h) return;
        DisposeSnapFbo();
        var colorTex = new RawTexture { Width = w, Height = h };
        snapFbo = capi.Render.CreateFrameBuffer(new FramebufferAttrs("almanacilluminated-snap", w, h)
        {
            Attachments = new[]
            {
                new FramebufferAttrsAttachment { AttachmentType = EnumFramebufferAttachment.ColorAttachment0, Texture = colorTex },
            },
        });
        snapColorTexId = colorTex.TextureId;
        snapW = w; snapH = h;
    }

    private void DisposeSnapFbo()
    {
        if (snapFbo == null) return;
        capi.Render.DestroyFrameBuffer(snapFbo);
        snapFbo = null;
        snapColorTexId = 0;
    }

    /// <summary>
    /// Snapshots the live capture (the OLD spread) into the snap FBO, then arms
    /// the sheet animation. Called from the page-corner click, before the
    /// dialog recomposes — the next frame's capture fills the live FBO with
    /// the NEW spread while the sheet carries the old one.
    /// </summary>
    public bool BeginPageTurn(bool forward)
    {
        if (!SnapshotSpread()) return false;
        turning = true;
        turnForward = forward;
        turnT = 0f;
        flipsLeft = 0;
        flipsTotal = 0;
        turnSeconds = TurnSeconds;
        return true;
    }

    /// <summary>
    /// Arms the leafing-through series. The flip count is derived from the jump and
    /// capped: past a handful of sheets a reader sees "a long way", not a count, so
    /// playing every page would only make the wait longer without reading as more.
    /// The last flip in the series is a full-length turn, which is what actually
    /// lands the reader on the target spread; everything before it whips past.
    ///
    /// A distance of one (or an unresolvable one, which the caller passes as one)
    /// degrades to exactly the single turn the page corners have always played.
    /// </summary>
    public bool BeginFlipThrough(bool forward, int spreads)
    {
        if (spreads <= 1) return BeginPageTurn(forward);
        if (!SnapshotSpread()) return false;

        int cap = Math.Max(2, FlipMax);
        int count = GameMath.Clamp(2 + (int)MathF.Round(spreads * FlipScale), Math.Min(FlipCountMin, cap), cap);
        turning = true;
        turnForward = forward;
        turnT = 0f;
        flipsTotal = count;
        flipsLeft = count - 1;          // the remainder after the landing turn
        turnSeconds = FlipDuration(0, count);
        return true;
    }

    /// <summary>
    /// How long flip `i` of `n` lasts. Accelerate in, decelerate out: the series
    /// starts and ends at the slow end and is quickest through the middle, which is
    /// how a thumb through a book actually sounds. The final flip is the landing
    /// turn and gets the full page-turn time so it settles rather than snapping.
    /// </summary>
    private float FlipDuration(int i, int n)
    {
        if (i >= n - 1) return TurnSeconds;
        float q = n <= 2 ? 0.5f : (i + 0.5f) / (n - 1);
        float bell = MathF.Sin(MathF.PI * q);
        // Tolerate the knobs being set the wrong way round rather than running backwards.
        float slow = Math.Max(FlipSlow, FlipFast);
        float fast = Math.Min(FlipSlow, FlipFast);
        return slow - (slow - fast) * bell;
    }

    /// <summary>Copies the live capture (the OLD spread) into the snap FBO. False when there
    /// is nothing captured yet, which is the caller's cue to fall back to an instant swap.</summary>
    private bool SnapshotSpread()
    {
        if (!LiveActive || fbo == null || fboColorTexId == 0 || copyQuadRef == null) return false;
        EnsureSnapFbo();
        if (snapFbo == null) return false;

        var r = capi.Render;
        var prog = ShaderPrograms.Gui;
        bool wasActive = ShaderProgramBase.CurrentShaderProgram == prog;
        if (!wasActive) prog.Use();

        var prevFb = r.CurrentFrameBuffer;
        r.CurrentFrameBuffer = snapFbo;
        r.ClearFrameBuffer(snapFbo, ClearTransparent);

        // 1:1 copy: identity matrices, identity-oriented UVs (copyQuadRef).
        r.GLDisableDepthTest();
        r.GlDisableCullFace();
        r.GlToggleBlend(true);
        prog.RgbaIn = new Vec4f(1f, 1f, 1f, 1f);
        prog.ExtraGlow = 0;
        prog.ApplyColor = 0;
        prog.NoTexture = 0f;
        prog.AlphaTest = 0f;
        prog.OverlayOpacity = 0f;
        prog.NormalShaded = 0;
        prog.TransparentCenter = 0;
        prog.DarkEdges = 0;
        prog.Tex2d2D = fboColorTexId;
        var ident = Mat4f.Create();
        prog.ProjectionMatrix = ident;
        prog.ModelViewMatrix = ident;
        r.RenderMesh(copyQuadRef);

        r.CurrentFrameBuffer = prevFb;
        if (!wasActive) prog.Stop();
        return true;
    }

    // --- rendering --------------------------------------------------------

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!Visible) return;

        if (liveDialog != null)
        {
            // Live mode: this pass only captures. The book itself is drawn by
            // the dialog's OnRenderGUI (via DrawLive) so it layers with the
            // dialog stack — above chat — instead of under every HUD.
            var b = liveDialog.SingleComposer?.Bounds;
            if (b == null) return;

            var r = capi.Render;
            var prog = ShaderPrograms.Gui;
            bool wasActive = ShaderProgramBase.CurrentShaderProgram == prog;
            if (!wasActive) prog.Use();

            // The whole capture pass, guarded: a driver that will not give us an
            // offscreen buffer must cost the tilt, not the book. One failure
            // latches and the host reopens flat.
            try
            {
                EnsureFbo();
                if (fbo == null || fboColorTexId == 0)
                {
                    if (!wasActive) prog.Stop();
                    FailCapture("the capture framebuffer could not be created");
                    return;
                }

                var prevFb = r.CurrentFrameBuffer;
                r.CurrentFrameBuffer = fbo;
                r.ClearFrameBuffer(fbo, ClearTransparent);
                PointMouseAtPage();
                try { liveDialog.RenderForCapture(deltaTime); }
                finally { RestoreMouse(); }
                r.CurrentFrameBuffer = prevFb;   // null setter re-binds the default buffer; viewport already window-sized
            }
            catch (Exception e)
            {
                if (!wasActive) prog.Stop();
                FailCapture(e.Message);
                return;
            }

            if (!wasActive) prog.Stop();

            cropX = (float)b.renderX;
            cropY = (float)b.renderY;
            cropW = b.OuterWidthInt;
            cropH = b.OuterHeightInt;
        }
    }

    /// <summary>Draws the captured spread on the modeled book. Guarded for the same
    /// reason the capture pass is: a draw the GPU refuses costs the tilt, not the book.</summary>
    public void DrawLive(float deltaTime)
    {
        if (CaptureUnavailable) return;
        try { DrawLiveInner(deltaTime); }
        catch (Exception e) { FailCapture(e.Message); }
    }

    private void DrawLiveInner(float deltaTime)
    {
        if (!LiveActive || quadLeftRef == null || quadRightRef == null) return;
        if (fboColorTexId == 0 || cropW <= 0 || cropH <= 0) return;

        var prog = ShaderPrograms.Gui;
        bool wasActive = ShaderProgramBase.CurrentShaderProgram == prog;
        if (!wasActive) prog.Use();

        UpdateMatrices(deltaTime, cropW / cropH);

        if (ModelEnabled)
        {
            EnsureBookMesh();
            if (spineRef != null)
            {
                DrawMesh(spineRef, atlasTexId, mvModelSpine, normalShaded: 1);
                DrawMesh(bodyLeftRef!, atlasTexId, mvModelLeft, normalShaded: 1);
                DrawMesh(bodyRightRef!, atlasTexId, mvModelRight, normalShaded: 1);
            }
        }

        // Scraps are player state: slot i renders only when pin i exists. One
        // mesh, instanced with a page-space x offset (pre-aspect, so it rides
        // the correct fold half) and a per-slot paper tint. The pin whose page
        // is OPEN lies on the spread (drawn after the capture); every other
        // scrap draws before it, slightly sunk, so the pages cover its on-page
        // reach and it reads as tucked deeper into the stack.
        int pinCount = ModelEnabled && scrapRef != null
            ? Math.Min(liveDialog?.PinCount ?? 0, ScrapSlots) : 0;
        int focusedPin = pinCount > 0 ? liveDialog!.FocusedPinIndex : -1;

        void DrawScrap(int i, bool under)
        {
            float center = ScrapSlotCenter[i];
            var side = center < 0 ? mvQuadLeft : mvQuadRight;
            float sink = under ? -0.024f : 0f;

            string label = liveDialog!.PinLabel(i);

            if (label.Length == 0 || labelQuadRef == null)
            {
                // An unnamed pin keeps the modelled slip and the old paper tints. It has
                // no label to be deterministic about, and nothing to cover the slip with.
                Mat4f.Identity(slotT);
                Mat4f.Translate(slotT, slotT, center - ScrapBaseCenter, 0f, sink);
                Mat4f.Mul(slotM, side, slotT);
                Mat4f.Mul(slotM, slotM, modelNorm);
                DrawMesh(scrapRef!, atlasTexId, slotM, normalShaded: 1, tint: ScrapTints[i]);
                return;
            }

            // A named pin is a ribbon, and it is the WHOLE object. The modelled slip is
            // deliberately not drawn: the previous pass tinted it and laid a smaller
            // generated face over the top, which left the uncovered part hanging below as
            // a bare quad. Owning the entire visual is the only way that cannot recur.
            //
            // labelQuadRef's UVs are rotated a quarter turn, so a scrap built lengthwise
            // hangs DOWNWARD with its torn end at the foot, which is what a ribbon draped
            // over a page edge looks like.
            var skin = ScrapBookmark.Build(label, PinRibbonTexelsLong, PinRibbonTexelsWide, freeEndLeft: false);
            Mat4f.Identity(slotT);
            Mat4f.Translate(slotT, slotT, center, PinRibbonCenterB, 0.024f + sink);
            Mat4f.Scale(slotT, slotT, PinRibbonHalfW, PinRibbonHalfLen, 1f);
            Mat4f.Mul(slotM, side, slotT);
            DrawMesh(labelQuadRef, ScrapTexture(label + "|pin", skin, false).TextureId, slotM, normalShaded: 0);

            // The name reads down the ribbon. Its length is the ribbon's minus the inset
            // that keeps it off the torn foot; its width follows the texture aspect, so
            // glyphs keep their proportions whatever the name is.
            var tex = GetLabelTexture(FitPinLabel(label, cropW / cropH), wrap: true, skin: skin);
            float halfLen = PinRibbonHalfLen - PinRibbonLabelInset;
            float halfW = GameMath.Clamp(
                halfLen * (tex.Height / (float)Math.Max(1, tex.Width)) / (cropW / cropH),
                0.010f, PinRibbonHalfW * 0.82f);
            Mat4f.Identity(slotT);
            Mat4f.Translate(slotT, slotT, center, PinRibbonCenterB, 0.026f + sink);
            Mat4f.Scale(slotT, slotT, halfW, halfLen, 1f);
            Mat4f.Mul(slotM, side, slotT);
            DrawMesh(labelQuadRef, tex.TextureId, slotM, normalShaded: 0);
        }

        // Side chapter tabs, same layering rule as the scraps: the active tab
        // lies on the spread, the rest tuck under the page edge.
        bool tabsReady = ModelEnabled && tabLeftRef != null && liveDialog != null;

        void DrawSideTab(bool leftSide, int i, int row, string label, bool active)
        {
            var sideM = leftSide ? mvQuadLeft : mvQuadRight;
            float sink = active ? 0f : -0.022f;
            float outShift = active ? (leftSide ? -0.012f : 0.012f) : 0f;
            float db = -(row * SideTabRowStep);
            float pageAspect = cropW / cropH;
            float sideSign = leftSide ? -1f : 1f;

            // A one-character label is the thumb index and keeps the baked slip it has
            // always had. Anything with a word on it is a BOOKMARK: a scrap of whatever
            // the reader had to hand, reaching further out so the name fits at full size.
            bool named = label.Length > 1;
            ScrapSkin? skin = null;

            if (named)
            {
                // Texels stay square by deriving the grid length from the tab's real
                // on-screen aspect, which is its a-extent in page units times the page
                // aspect, over its b-extent.
                int gw = GameMath.Clamp(
                    (int)Math.Round(ScrapTexelsHigh * (NamedTabHalfA * pageAspect / SideTabHalfB)), 8, 160);
                skin = ScrapBookmark.Build(label, gw, ScrapTexelsHigh, freeEndLeft: leftSide);

                Mat4f.Identity(slotT);
                Mat4f.Translate(slotT, slotT, sideSign * NamedTabCenterA + outShift, SideTabRow0 + db, 0.015f + sink);
                Mat4f.Scale(slotT, slotT, NamedTabHalfA, SideTabHalfB, 1f);
                Mat4f.Mul(slotM, sideM, slotT);
                DrawMesh(labelQuadHRef!, ScrapTexture(label, skin, leftSide).TextureId, slotM,
                    normalShaded: 0, tint: active ? null : ScrapIdleTint);
            }
            else
            {
                Mat4f.Identity(slotT);
                Mat4f.Translate(slotT, slotT, outShift, db, sink);
                Mat4f.Mul(slotM, sideM, slotT);
                Mat4f.Mul(slotM, slotM, modelNorm);
                var tint = active ? TabTintActive : (row % 2 == 0 ? TabTintA : TabTintB);
                DrawMesh(leftSide ? tabLeftRef! : tabRightRef!, atlasTexId, slotM, normalShaded: 1, tint: tint);
            }

            if (label.Length > 0 && labelQuadHRef != null)
            {
                // Uniform glyph height across tabs of a kind (real-index typography);
                // width follows the texture aspect, so a single letter is never
                // stretched wide nor a long name squeezed thin. The shrink stops at the
                // readable floor: anything that would have to go smaller arrived already
                // wrapped or clipped from the fit above.
                float nominalHalfB, floorHalfB, maxHalfA, centerA;
                string text;
                if (named)
                {
                    var fit = FitNamedLabel(label, pageAspect);
                    text = fit.Text;
                    nominalHalfB = fit.NominalHalfB;
                    floorHalfB = fit.FloorHalfB;
                    maxHalfA = NamedLabelMaxHalfA;
                    centerA = sideSign * NamedTabCenterA + outShift;
                }
                else
                {
                    text = FitSideTabLabel(label, pageAspect);
                    nominalHalfB = SideTabLabelHalfB;
                    floorHalfB = SideTabLabelHalfBFloor;
                    maxHalfA = SideTabLabelMaxHalfA;
                    centerA = sideSign * 1.048f + outShift;
                }

                var tex = GetLabelTexture(text, wrap: false, skin: skin);
                float halfB = nominalHalfB;
                float halfA = halfB * (tex.Width / (float)Math.Max(1, tex.Height)) / pageAspect;
                if (halfA > maxHalfA)
                {
                    // The fit above should make this a no-op; it stays as the guard
                    // that a label can never reach past the tab carrying it.
                    halfB = Math.Max(floorHalfB, halfB * maxHalfA / halfA);
                    halfA = maxHalfA;
                }
                Mat4f.Identity(slotT);
                Mat4f.Translate(slotT, slotT, centerA, SideTabRow0 + db, 0.02f + sink);
                Mat4f.Scale(slotT, slotT, halfA, halfB, 1f);
                Mat4f.Mul(slotM, sideM, slotT);
                DrawMesh(labelQuadHRef, tex.TextureId, slotM, normalShaded: 0);
            }
        }

        void DrawSideTabs(bool activePass)
        {
            if (!tabsReady) return;
            for (int s = 0; s < 2; s++)
            {
                bool leftSide = s == 0;
                var labels = leftSide ? liveDialog!.PhysLeftTabs : liveDialog!.PhysRightTabs;
                int active = leftSide ? liveDialog.PhysLeftActive : liveDialog.PhysRightActive;
                int bottom = leftSide ? liveDialog.PhysLeftBottom : liveDialog.PhysRightBottom;
                for (int i = 0; i < labels.Length; i++)
                {
                    if ((i == active) == activePass)
                        DrawSideTab(leftSide, i, SideTabRow(i, labels.Length, bottom), labels[i], i == active);
                }
            }
        }

        DrawSideTabs(activePass: false);

        for (int i = 0; i < pinCount; i++)
        {
            if (i != focusedPin) DrawScrap(i, under: true);
        }

        if (QuadEnabled)
        {
            // FBO texture row 0 is the bottom, screen y=0 is the top; the
            // spread splits at the capture's horizontal center.
            float u0 = cropX / fboW, u1 = (cropX + cropW) / fboW, uMid = (u0 + u1) * 0.5f;
            float vTop = 1f - cropY / fboH, vBot = 1f - (cropY + cropH) / fboH;
            UpdateQuadUv(lastUvLeft, quadLeftRef, -1f, 0f, u0, vTop, uMid, vBot);
            UpdateQuadUv(lastUvRight, quadRightRef, 0f, 1f, uMid, vTop, u1, vBot);

            if (turning && snapColorTexId != 0 && sheetStripRef != null)
            {
                hoverCorner = 0; hoverT = 0f;    // the lift hands off to the swing
                turnT = Math.Min(1f, turnT + deltaTime / Math.Max(0.01f, turnSeconds));
                bool intermediate = flipsLeft > 0;

                // Underneath: the origin side keeps the OLD page (the sheet has
                // not left it… physically it HAS — the old page IS the sheet —
                // but the landing side must show old content until covered, and
                // the origin side already shows the new page beneath the sheet.
                bool fwd = turnForward;
                DrawMesh(quadLeftRef, fwd ? snapColorTexId : fboColorTexId, mvQuadLeft, normalShaded: 0);
                DrawMesh(quadRightRef, fwd ? fboColorTexId : snapColorTexId, mvQuadRight, normalShaded: 0);

                // The swing. A landing turn eases in and out so it settles; a sheet
                // mid-series runs at constant angular speed, because a series of
                // eased turns reads as several separate turns rather than as one
                // continuous leafing-through.
                float e = intermediate
                    ? turnT
                    : turnT < 0.5f ? 4f * turnT * turnT * turnT
                                   : 1f - MathF.Pow(-2f * turnT + 2f, 3f) / 2f;   // ease-in-out cubic
                float phi = e * MathF.PI;

                // Geometry carries the aspect itself, so the sheet takes mv alone.
                float pageHalfW = cropW / cropH;
                int splitCol = BuildBentSheet(
                    phi, CurlAt(turnT, intermediate), pageHalfW, fwd ? 1 : -1,
                    uSpine: uMid,
                    uOuterFront: fwd ? u1 : u0,
                    uOuterBack: fwd ? u0 : u1,
                    vTop, vBot);

                // Two draws, split at the column where the paper passes vertical:
                // everything short of it still presents the outgoing page, everything
                // past it presents the incoming one. The rigid sheet had to swap both
                // at once at the halfway mark; a curled one shows both at once, which
                // is what it would really do.
                // Mid-series sheets can go blank instead: at 60 to 130 ms a sheet the
                // pixels are unreadable either way, and which of the two reads as leafing
                // rather than as strobing is a question only the eye settles.
                bool blank = intermediate && FlipPaper;
                int frontTex = blank ? PaperTexId() : snapColorTexId;
                int backTex = blank ? PaperTexId() : fboColorTexId;

                int frontQuads = splitCol * (TurnRows - 1);
                int backQuads = StripQuads - frontQuads;
                if (frontQuads > 0)
                {
                    stripStart[0] = 0; stripSize[0] = frontQuads * 6;
                    DrawStrip(frontTex, SheetShade(phi + CurlAt(turnT, intermediate) * 0.25f));
                }
                if (backQuads > 0)
                {
                    stripStart[0] = frontQuads * 6; stripSize[0] = backQuads * 6;
                    DrawStrip(backTex, SheetShade(phi + CurlAt(turnT, intermediate) * 0.75f));
                }

                if (turnT >= 1f)
                {
                    if (flipsLeft > 0)
                    {
                        // Next sheet in the series, straight into its swing.
                        flipsLeft--;
                        turnT = 0f;
                        turnSeconds = FlipDuration(flipsTotal - 1 - flipsLeft, flipsTotal);
                    }
                    else turning = false;
                }
            }
            else
            {
                turning = false;
                DrawMesh(quadLeftRef, fboColorTexId, mvQuadLeft, normalShaded: 0);
                DrawMesh(quadRightRef, fboColorTexId, mvQuadRight, normalShaded: 0);
                DrawHoverCurl(deltaTime, u0, u1, uMid, vTop, vBot);
            }
        }

        DrawSideTabs(activePass: true);

        if (focusedPin >= 0 && focusedPin < pinCount)
        {
            DrawScrap(focusedPin, under: false);
        }

        if (!wasActive) prog.Stop();
    }

    /// <summary>Mild darkening peaking mid-flight — the turning sheet catches its own shadow.
    /// Callers pass the mean tangent angle of the half they are about to draw, which the curl
    /// can push a little past flat; clamping keeps that from flipping the sine negative and
    /// brightening the sheet above its own paper on the last frames of a turn.</summary>
    private static Vec4f SheetShade(float phi)
    {
        float shade = 1f - 0.16f * MathF.Sin(GameMath.Clamp(phi, 0f, MathF.PI));
        return new Vec4f(shade, shade, shade, 1f);
    }

    /// <summary>
    /// The corner the cursor is on, lifted off the page. Drawn over the flat spread rather
    /// than instead of it, so the paper beneath shows through the gap the way it would in a
    /// real book; depth testing is off in <see cref="DrawStrip"/>, so draw order alone
    /// decides what covers what.
    ///
    /// The sheet is the same geometry the turn uses, at turnAngle zero and a small curl
    /// biased to the foot of the page. A turn and a lift cannot run together: the turning
    /// branch zeroes this first.
    /// </summary>
    private void DrawHoverCurl(float deltaTime, float u0, float u1, float uMid, float vTop, float vBot)
    {
        int want = liveDialog?.HoveredCorner ?? 0;

        // Settle to nothing before switching sides, so moving across the foot of the book
        // reads as one corner dropping and the other rising, not as a jump.
        if (want != hoverCorner)
        {
            hoverT -= deltaTime / Math.Max(0.01f, HoverSeconds);
            if (hoverT <= 0f) { hoverT = 0f; hoverCorner = want; }
        }
        else if (hoverCorner != 0)
        {
            hoverT = Math.Min(1f, hoverT + deltaTime / Math.Max(0.01f, HoverSeconds));
        }

        if (hoverCorner == 0 || hoverT <= 0f || sheetStripRef == null) return;

        float curl = HoverLift * EaseOutCubic(hoverT);
        bool right = hoverCorner > 0;
        float uOuter = right ? u1 : u0;
        // Geometry carries the aspect itself, exactly as in the turn.
        float pageHalfW = cropW / cropH;
        BuildBentSheet(0f, curl, pageHalfW, right ? 1 : -1,
                       uSpine: uMid,
                       uOuterFront: uOuter,
                       uOuterBack: uOuter,   // nothing passes vertical at this curl
                       vTop, vBot, lowBias: 1f);

        stripStart[0] = 0; stripSize[0] = StripQuads * 6;
        DrawStrip(fboColorTexId, SheetShade(curl * 0.5f));
    }

    /// <summary>
    /// Draws the index range in <see cref="stripStart"/>/<see cref="stripSize"/> of the bent
    /// sheet. The geometry already carries the aspect stretch and the swing, so it takes mv
    /// straight — no fold, no aspect matrix. Otherwise identical to DrawMesh; the two exist
    /// apart only because the engine's partial-range draw is a different call.
    /// </summary>
    private void DrawStrip(int texId, Vec4f tint)
    {
        var r = capi.Render;
        var prog = ShaderPrograms.Gui;

        r.GLDisableDepthTest();
        r.GlDisableCullFace();
        r.GlToggleBlend(true);

        prog.RgbaIn = new Vec4f(tint.X, tint.Y, tint.Z, tint.W * easeFade);
        prog.ExtraGlow = 0;
        prog.ApplyColor = 0;
        prog.NoTexture = 0f;
        prog.AlphaTest = 0f;
        prog.OverlayOpacity = 0f;
        prog.NormalShaded = 0;
        prog.TransparentCenter = 0;
        prog.DarkEdges = 0;
        prog.Tex2d2D = texId;
        prog.ProjectionMatrix = proj;
        prog.ModelViewMatrix = mv;
        r.RenderMesh(sheetStripRef!, stripStart, stripSize, 1);
    }

    private static float EaseOutCubic(float t)
    {
        t = GameMath.Clamp(t, 0f, 1f);
        float u = 1f - t;
        return 1f - u * u * u;
    }

    private void UpdateMatrices(float deltaTime, float texAspect)
    {
        var r = capi.Render;

        if (closing)
        {
            openT -= deltaTime / StowSeconds;
            if (openT <= 0f)
            {
                // Stowed. Close the dialog next tick — never mid-render, the
                // gui loop is iterating the dialog list right now.
                openT = 0f;
                closing = false;
                StowComplete = true;   // lets the dialog's stow guard pass this one through
                var dlg = liveDialog;
                capi.Event.RegisterCallback(_ => dlg?.TryClose(), 0, permittedWhilePaused: true);
            }
        }
        else if (!StowComplete)
        {
            openT = Math.Min(1f, openT + deltaTime / DrawOutSeconds);
        }
        // A landed stow holds at nothing until its close actually happens. The close is
        // registered as a game callback above, and game callbacks do not run while the
        // world is paused, so without this the book would march straight back open on the
        // very next frame. The dialog drops the pause as the stow BEGINS, which is what
        // normally lets that callback run (see GuiDialogIlluminatedBook.TryClose); this is
        // the belt to that braces, for any path that leaves the world paused instead.
        // StowComplete is cleared on the next ShowLive, so a reopen is unaffected.

        // The draw-out: fade in fast, rise through most of it, fold open once
        // the book is mostly up. All phases ease-out; all end at the knobs.
        easeFade = GameMath.Clamp(openT / 0.15f, 0f, 1f);
        float easeRise = EaseOutCubic(openT / 0.6f);
        float easeFold = EaseOutCubic((openT - 0.15f) / 0.85f);
        float foldDeg = FoldStartDeg * (1f - easeFold);

        float screenAspect = r.FrameWidth / (float)r.FrameHeight;
        Mat4f.Perspective(proj, FovDeg * GameMath.DEG2RAD, screenAspect, 0.05f, 10f);

        // Farther start: the closed book rises small and opens as it approaches
        // ("pulled from a pocket"), instead of unfolding right in the camera's face.
        float dist = Dist + (1f - easeRise) * 1.0f;
        float tilt = (TiltDeg + (1f - easeRise) * 18f) * GameMath.DEG2RAD;
        float rise = (1f - easeRise) * -0.85f;

        // Uniform scale only — the aspect stretch lives in A so the fold
        // rotation happens in isotropic space and cannot skew the pages.
        Mat4f.Identity(mv);
        Mat4f.Translate(mv, mv, 0f, HeightOff + rise, -dist);
        Mat4f.RotateX(mv, mv, -tilt);
        Mat4f.RotateY(mv, mv, YawDeg * GameMath.DEG2RAD);
        Mat4f.Scale(mv, mv, Scale, Scale, Scale);

        Mat4f.Identity(aspectM);
        aspectM[0] = texAspect;

        // Per-side fold about the spine line (x=0, z=SpinePivotZ in page space).
        BuildFold(foldLeft, foldDeg * GameMath.DEG2RAD);
        BuildFold(foldRight, -foldDeg * GameMath.DEG2RAD);

        // remapM is the settled page plane (fold-free): input remap inverts it.
        Mat4f.Mul(remapM, mv, aspectM);

        Mat4f.Mul(mvQuadLeft, mv, Mat4f.Mul(tmpM, foldLeft, aspectM));
        Mat4f.Mul(mvModelLeft, mvQuadLeft, modelNorm);
        Mat4f.Mul(mvQuadRight, mv, Mat4f.Mul(tmpM, foldRight, aspectM));
        Mat4f.Mul(mvModelRight, mvQuadRight, modelNorm);
        Mat4f.Mul(mvModelSpine, remapM, modelNorm);
    }

    /// <summary>Rotation by `rad` about the spine axis (vertical line at x=0,
    /// z=SpinePivotZ in isotropic page space).</summary>
    private static void BuildFold(float[] m, float rad)
    {
        Mat4f.Identity(m);
        Mat4f.Translate(m, m, 0f, 0f, SpinePivotZ);
        Mat4f.RotateY(m, m, rad);
        Mat4f.Translate(m, m, 0f, 0f, -SpinePivotZ);
    }

    /// <summary>
    /// One mesh through the gui shader. The engine keeps this shader active
    /// (and depth/cull off) around dialog and ortho rendering, and downstream
    /// renderers set uniforms assuming it is still current — callers Use/Stop
    /// only when they weren't handed an active gui shader, and GL state is
    /// never flipped back "on" behind the engine's back.
    /// </summary>
    /// <summary>The material the boards wear. Null = the shape's own default. Setting a
    /// different one drops the three meshes that carry the cover — the two boards and the
    /// spine — so the next frame re-tesselates them against the new texture; nothing else
    /// about the book is touched.</summary>
    private AssetLocation? coverOverride;

    public void SetCover(AssetLocation? texture)
    {
        if (coverOverride?.ToString() == texture?.ToString()) return;
        coverOverride = texture;
        spineRef?.Dispose(); spineRef = null;
        bodyLeftRef?.Dispose(); bodyLeftRef = null;
        bodyRightRef?.Dispose(); bodyRightRef = null;
        bookMeshFailed = false;   // a previous failure must not outlive a change of cover
    }

    // Ambient shade (local illumination): final per-channel multipliers folded into every
    // DrawMesh, so the whole modeled book — spine, pages, sheet, scraps, tabs, labels —
    // darkens and warms as one object under the reader's light. Neutral by default.
    private float ambientR = 1f, ambientG = 1f, ambientB = 1f;

    public void SetAmbientShade(float r, float g, float b)
    {
        ambientR = r; ambientG = g; ambientB = b;
    }

    private void DrawMesh(MeshRef mesh, int texId, float[] modelView, int normalShaded, Vec4f? tint = null)
    {
        var r = capi.Render;
        var prog = ShaderPrograms.Gui;

        r.GLDisableDepthTest();
        r.GlDisableCullFace();
        r.GlToggleBlend(true);

        prog.RgbaIn = tint == null
            ? new Vec4f(ambientR, ambientG, ambientB, easeFade)
            : new Vec4f(tint.X * ambientR, tint.Y * ambientG, tint.Z * ambientB, tint.W * easeFade);
        prog.ExtraGlow = 0;
        prog.ApplyColor = 0;
        prog.NoTexture = 0f;
        prog.AlphaTest = 0f;
        prog.OverlayOpacity = 0f;
        prog.NormalShaded = normalShaded;
        prog.TransparentCenter = 0;
        prog.DarkEdges = 0;
        prog.Tex2d2D = texId;
        prog.ProjectionMatrix = proj;
        prog.ModelViewMatrix = modelView;
        r.RenderMesh(mesh);
    }

    // --- input remap ------------------------------------------------------

    /// <summary>
    /// Screen point -> dialog space, by intersecting the camera ray with the
    /// settled page plane (remapM = mv*A — the fold is transient, clicks
    /// during the half-second draw-out land on the settled pose). The matrix
    /// columns give the plane's basis, so this stays correct through any knob
    /// change.
    /// </summary>
    /// <summary>Raw page-space hit (unclamped): the scrap zone above the top
    /// edge (pageY 1.0..~1.25) hit-tests through this.</summary>
    // ---------------------------------------------------------------- raw-mouse components
    //
    // The dialog remaps the mouse on the events it receives, and every element this mod wrote
    // reads those event coords (see the note in GuiElementChapterTabs). Vanilla elements do
    // not: ItemstackTextComponent's hover tooltip hit-tests api.Input.MouseX/MouseY straight
    // out of the game, so it compares a real screen point against the flat page coordinates
    // the composers were laid out in. On a tilted book those disagree by the whole transform,
    // which is why hovering one crop card raised the tooltip of another (found in play
    // 2026-08-23 on the Crops tab; it was never crop-specific, it is every hoverable in the
    // physical book).
    //
    // The capture pass is the one place all of them run, so the mouse is pointed at the
    // remapped page position for exactly that long and put straight back. Off the page it is
    // parked far outside the dialog, so nothing hovers rather than something arbitrary
    // hovering. Restored in a finally: leaving the game's mouse moved would be a far worse
    // bug than the one being fixed.

    private int savedMouseX, savedMouseY;
    private bool mousePointed;

    private void PointMouseAtPage()
    {
        var client = capi.World as ClientMain;
        if (client == null) return;

        savedMouseX = client.MouseCurrentX;
        savedMouseY = client.MouseCurrentY;
        mousePointed = true;

        const int parked = -10000;
        if (TryRemap(savedMouseX, savedMouseY, out int rx, out int ry))
        {
            client.MouseCurrentX = rx;
            client.MouseCurrentY = ry;
        }
        else
        {
            client.MouseCurrentX = parked;
            client.MouseCurrentY = parked;
        }
    }

    private void RestoreMouse()
    {
        if (!mousePointed) return;
        mousePointed = false;
        if (capi.World is ClientMain client)
        {
            client.MouseCurrentX = savedMouseX;
            client.MouseCurrentY = savedMouseY;
        }
    }

    public bool TryRemapRaw(int mx, int my, out float pageX, out float pageY)
    {
        pageX = 0f; pageY = 0f;
        if (!SolvePagePlane(mx, my, out float a, out float b)) return false;
        pageX = a; pageY = b;
        return true;
    }

    public bool TryRemap(int mx, int my, out int rx, out int ry)
    {
        rx = 0; ry = 0;
        if (!SolvePagePlane(mx, my, out float a, out float b)) return false;

        bool inside = a >= -1f && a <= 1f && b >= -1f && b <= 1f;

        // Page space (-1..1, y up) -> capture rect (y down), clamped so
        // mouse-up off the page still releases at the nearest page point.
        float u = GameMath.Clamp((a + 1f) * 0.5f, 0f, 1f);
        float vImg = GameMath.Clamp((1f - b) * 0.5f, 0f, 1f);
        rx = (int)(cropX + u * cropW);
        ry = (int)(cropY + vImg * cropH);
        return inside;
    }

    private bool SolvePagePlane(int mx, int my, out float a, out float b)
    {
        a = 0f; b = 0f;
        if (!LiveActive || closing || cropW <= 0 || cropH <= 0) return false;

        float w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
        if (w <= 0 || h <= 0) return false;

        // Mouse -> NDC -> view-space ray through the perspective frustum.
        float nx = 2f * mx / w - 1f;
        float ny = 1f - 2f * my / h;
        float tanY = MathF.Tan(0.5f * FovDeg * GameMath.DEG2RAD);
        float tanX = tanY * (w / h);
        float dx = nx * tanX, dy = ny * tanY, dz = -1f;

        // Page plane from the settled matrix (column-major): origin + a*U + b*V.
        float ux = remapM[0], uy = remapM[1], uz = remapM[2];
        float vx = remapM[4], vy = remapM[5], vz = remapM[6];
        float ox = remapM[12], oy = remapM[13], oz = remapM[14];

        // Solve a*U + b*V - t*D = -O   (Cramer's rule, 3x3)
        float det = Det3(ux, vx, -dx, uy, vy, -dy, uz, vz, -dz);
        if (MathF.Abs(det) < 1e-9f) return false;
        a = Det3(-ox, vx, -dx, -oy, vy, -dy, -oz, vz, -dz) / det;
        b = Det3(ux, -ox, -dx, uy, -oy, -dy, uz, -oz, -dz) / det;
        float t = Det3(ux, vx, -ox, uy, vy, -oy, uz, vz, -oz) / det;
        return t > 0f;   // false: page is behind the camera
    }

    private static float Det3(
        float a, float b, float c,
        float d, float e, float f,
        float g, float h, float i)
        => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

    public void Dispose()
    {
        DisposeFbo();
        DisposeSnapFbo();
        sheetStripRef?.Dispose(); sheetStripRef = null;
        sheetStrip = null;
        paperTex?.Dispose(); paperTex = null;
        copyQuadRef?.Dispose(); copyQuadRef = null;
        quadLeftRef?.Dispose(); quadLeftRef = null;
        quadRightRef?.Dispose(); quadRightRef = null;
        spineRef?.Dispose(); spineRef = null;
        bodyLeftRef?.Dispose(); bodyLeftRef = null;
        bodyRightRef?.Dispose(); bodyRightRef = null;
        scrapRef?.Dispose(); scrapRef = null;
        tabLeftRef?.Dispose(); tabLeftRef = null;
        tabRightRef?.Dispose(); tabRightRef = null;
        labelQuadRef?.Dispose(); labelQuadRef = null;
        labelQuadHRef?.Dispose(); labelQuadHRef = null;
        foreach (var t in scrapTextures.Values) t.Dispose();
        scrapTextures.Clear();
        foreach (var tex in labelTextures.Values) tex.Dispose();
        labelTextures.Clear();
    }
}
