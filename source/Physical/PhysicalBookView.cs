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
    private MeshRef? sheetRightRef, sheetLeftRef, copyQuadRef;
    private readonly float[] lastUvSheet = new float[4];
    private bool turning, turnForward;
    private float turnT;
    private const float TurnSeconds = 0.42f;
    private readonly float[] sheetM = Mat4f.Create();
    private readonly float[] sheetF = Mat4f.Create();

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
    private CairoFont? labelFont;

    private LoadedTexture GetLabelTexture(string label, bool wrap = false)
    {
        string key = (wrap ? "w|" : "s|") + label;
        if (labelTextures.TryGetValue(key, out var tex)) return tex;
        // Sturdy bold sans in cream with a dark outline: the tabs are mid-tone
        // colors, so dark ink mudded out and a fine serif fell apart at tab
        // size (in-game review 2026-07-28).
        labelFont ??= CairoFont.WhiteSmallText()
            .WithFontSize(28)
            .WithWeight(Cairo.FontWeight.Bold)
            .WithColor(new[] { 0.95, 0.91, 0.80, 1.0 })
            .WithStroke(new[] { 0.18, 0.12, 0.07, 1.0 }, 2.5);

        // Bookmarks wrap to two lines (two columns once rotated onto the tab)
        // so long titles keep readable glyphs instead of shrinking to fit.
        string shown = wrap ? WrapTwoLines(label, 10)
            : label.Length > 14 ? label.Substring(0, 13) + "…" : label;

        if (shown.Contains('\n'))
        {
            var lines = shown.Split('\n');
            double maxW = 0;
            foreach (var l in lines) maxW = Math.Max(maxW, labelFont.GetTextExtents(l).Width);
            double lineH = labelFont.GetFontExtents().Height;
            tex = capi.Gui.TextTexture.GenTextTexture(shown, labelFont,
                (int)maxW + 12, (int)(lineH * lines.Length) + 8, null, EnumTextOrientation.Center);
        }
        else
        {
            tex = capi.Gui.TextTexture.GenTextTexture(shown, labelFont);
        }
        labelTextures[key] = tex;
        return tex;
    }

    private static string WrapTwoLines(string s, int maxLine)
    {
        if (s.Length <= maxLine) return s;
        int mid = s.Length / 2, best = -1;
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == ' ' && (best == -1 || Math.Abs(i - mid) < Math.Abs(best - mid))) best = i;
        }
        if (best <= 0) return s.Length > 14 ? s.Substring(0, 13) + "…" : s;
        string l2 = s.Substring(best + 1);
        if (l2.Length > 13) l2 = l2.Substring(0, 12) + "…";
        return s.Substring(0, best) + "\n" + l2;
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

        // Page-turn sheet halves (own UV state so the underneath quads keep theirs)
        // and the 1:1 copy quad (identity UV orientation for FBO->FBO blits).
        sheetRightRef = capi.Render.UploadMesh(BuildQuad(0f, 1f, 0.5f, 0f, 1f, 1f));
        sheetLeftRef = capi.Render.UploadMesh(BuildQuad(-1f, 0f, 0f, 0f, 0.5f, 1f));
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
        $"tilt {TiltDeg:0.#}°, yaw {YawDeg:0.#}°, dist {Dist:0.##}, scale {Scale:0.##}, fov {FovDeg:0.#}°, height {HeightOff:0.##}";

    public void Reset()
    {
        TiltDeg = 12f; YawDeg = 0f; Dist = 1.0f; Scale = 0.37f; FovDeg = 50f; HeightOff = 0.06f;
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

        turning = true;
        turnForward = forward;
        turnT = 0f;
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
                liveDialog.RenderForCapture(deltaTime);
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

            Mat4f.Identity(slotT);
            Mat4f.Translate(slotT, slotT, center - ScrapBaseCenter, 0f, sink);
            Mat4f.Mul(slotM, side, slotT);
            Mat4f.Mul(slotM, slotM, modelNorm);
            DrawMesh(scrapRef!, atlasTexId, slotM, normalShaded: 1, tint: ScrapTints[i]);

            // The pinned page's name, reading down the hanging tab. The quad's
            // cross-tab width derives from the texture aspect so glyphs keep
            // their proportions whatever the label length.
            string label = liveDialog!.PinLabel(i);
            if (label.Length > 0 && labelQuadRef != null)
            {
                var tex = GetLabelTexture(label, wrap: true);
                const float halfLen = 0.088f;   // down-tab, isotropic page units
                float halfW = GameMath.Clamp(
                    halfLen * (tex.Height / (float)Math.Max(1, tex.Width)) / (cropW / cropH),
                    0.010f, 0.045f);
                Mat4f.Identity(slotT);
                Mat4f.Translate(slotT, slotT, center, 1.115f, 0.026f + sink);   // tab's hanging area, just above its face
                Mat4f.Scale(slotT, slotT, halfW, halfLen, 1f);
                Mat4f.Mul(slotM, side, slotT);
                DrawMesh(labelQuadRef, tex.TextureId, slotM, normalShaded: 0);
            }
        }

        // Side chapter tabs, same layering rule as the scraps: the active tab
        // lies on the spread, the rest tuck under the page edge.
        bool tabsReady = ModelEnabled && tabLeftRef != null && liveDialog != null;

        void DrawSideTab(bool leftSide, int i, int row, string label, bool active)
        {
            var mesh = leftSide ? tabLeftRef! : tabRightRef!;
            var sideM = leftSide ? mvQuadLeft : mvQuadRight;
            float sink = active ? 0f : -0.022f;
            float outShift = active ? (leftSide ? -0.012f : 0.012f) : 0f;
            float db = -(row * SideTabRowStep);

            Mat4f.Identity(slotT);
            Mat4f.Translate(slotT, slotT, outShift, db, sink);
            Mat4f.Mul(slotM, sideM, slotT);
            Mat4f.Mul(slotM, slotM, modelNorm);
            var tint = active ? TabTintActive : (row % 2 == 0 ? TabTintA : TabTintB);
            DrawMesh(mesh, atlasTexId, slotM, normalShaded: 1, tint: tint);

            if (label.Length > 0 && labelQuadHRef != null)
            {
                var tex = GetLabelTexture(label);
                // Uniform glyph height across all tabs (real-index typography);
                // width follows the texture aspect, and over-long titles shrink
                // to the tab instead of distorting. The first rule fixed width
                // and stretched single letters wide, long titles thin.
                float halfB = 0.030f;
                float halfA = halfB * (tex.Width / (float)Math.Max(1, tex.Height)) / (cropW / cropH);
                const float maxHalfA = 0.055f;
                if (halfA > maxHalfA)
                {
                    halfB *= maxHalfA / halfA;
                    halfA = maxHalfA;
                }
                float centerA = (leftSide ? -1.048f : 1.048f) + outShift;
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

            if (turning && snapColorTexId != 0 && sheetRightRef != null)
            {
                turnT = Math.Min(1f, turnT + deltaTime / TurnSeconds);

                // Underneath: the origin side keeps the OLD page (the sheet has
                // not left it… physically it HAS — the old page IS the sheet —
                // but the landing side must show old content until covered, and
                // the origin side already shows the new page beneath the sheet.
                bool fwd = turnForward;
                DrawMesh(quadLeftRef, fwd ? snapColorTexId : fboColorTexId, mvQuadLeft, normalShaded: 0);
                DrawMesh(quadRightRef, fwd ? fboColorTexId : snapColorTexId, mvQuadRight, normalShaded: 0);

                // The sheet: ease across 0..180°, lifting toward the camera.
                float e = turnT < 0.5f ? 4f * turnT * turnT * turnT
                    : 1f - MathF.Pow(-2f * turnT + 2f, 3f) / 2f;   // ease-in-out cubic
                float phi = e * MathF.PI;
                bool pastVertical = phi > MathF.PI / 2f;

                Mat4f.Identity(sheetF);
                Mat4f.RotateY(sheetF, sheetF, fwd ? -phi : phi);
                Mat4f.Mul(sheetM, mv, Mat4f.Mul(tmpM, sheetF, aspectM));

                var sheet = fwd ? sheetRightRef : sheetLeftRef!;
                if (!pastVertical)
                {
                    // Face side: the OLD page of the turning half.
                    float a0 = fwd ? uMid : u0, a1 = fwd ? u1 : uMid;
                    UpdateQuadUv(lastUvSheet, sheet, fwd ? 0f : -1f, fwd ? 1f : 0f, a0, vTop, a1, vBot);
                    DrawMesh(sheet, snapColorTexId, sheetM, normalShaded: 0, tint: SheetShade(phi));
                }
                else
                {
                    // Back side: the NEW page of the landing half, u-reversed —
                    // the sheet's outer edge points the other way now.
                    float a0 = fwd ? uMid : u1, a1 = fwd ? u0 : uMid;
                    UpdateQuadUv(lastUvSheet, sheet, fwd ? 0f : -1f, fwd ? 1f : 0f, a0, vTop, a1, vBot);
                    DrawMesh(sheet, fboColorTexId, sheetM, normalShaded: 0, tint: SheetShade(phi));
                }

                if (turnT >= 1f) turning = false;
            }
            else
            {
                turning = false;
                DrawMesh(quadLeftRef, fboColorTexId, mvQuadLeft, normalShaded: 0);
                DrawMesh(quadRightRef, fboColorTexId, mvQuadRight, normalShaded: 0);
            }
        }

        DrawSideTabs(activePass: true);

        if (focusedPin >= 0 && focusedPin < pinCount)
        {
            DrawScrap(focusedPin, under: false);
        }

        if (!wasActive) prog.Stop();
    }

    /// <summary>Mild darkening peaking mid-flight — the turning sheet catches its own shadow.</summary>
    private static Vec4f SheetShade(float phi)
    {
        float shade = 1f - 0.16f * MathF.Sin(phi);
        return new Vec4f(shade, shade, shade, 1f);
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
                capi.Event.RegisterCallback(_ => dlg?.TryClose(), 0);
            }
        }
        else
        {
            openT = Math.Min(1f, openT + deltaTime / DrawOutSeconds);
        }

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
    private void DrawMesh(MeshRef mesh, int texId, float[] modelView, int normalShaded, Vec4f? tint = null)
    {
        var r = capi.Render;
        var prog = ShaderPrograms.Gui;

        r.GLDisableDepthTest();
        r.GlDisableCullFace();
        r.GlToggleBlend(true);

        prog.RgbaIn = tint == null
            ? new Vec4f(1f, 1f, 1f, easeFade)
            : new Vec4f(tint.X, tint.Y, tint.Z, tint.W * easeFade);
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
        sheetRightRef?.Dispose(); sheetRightRef = null;
        sheetLeftRef?.Dispose(); sheetLeftRef = null;
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
        foreach (var tex in labelTextures.Values) tex.Dispose();
        labelTextures.Clear();
    }
}
