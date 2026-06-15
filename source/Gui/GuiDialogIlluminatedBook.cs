using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace AlmanacIlluminated;

/// <summary>
/// The open book. A two-page parchment spread of one chapter at a time, with
/// page turning, chapter-to-chapter navigation through internal links, a back
/// history, and a jump to the contents/overview. The book opens to the overview
/// when one exists. Frame art and the page-flip animation arrive in later phases.
/// </summary>
public class GuiDialogIlluminatedBook : GuiDialog
{
    public const string HotkeyCode = "almanacilluminatedbook";

    // Fraction of the screen the open book fills.
    private const double ScreenFraction = 0.84;

    private readonly GuideLibrary library;

    private GuidePack? current;
    private List<RichTextComponentBase[]>? pages;
    private Dictionary<GuidePack, List<RichTextComponentBase[]>>? cache;
    private int spreadIndex;
    private bool pendingOpenAtEnd;
    private bool warnedConflict;
    private float lastFrameW, lastFrameH;
    private readonly Stack<string> history = new();

    public override string ToggleKeyCombinationCode => HotkeyCode;

    public GuiDialogIlluminatedBook(ICoreClientAPI capi, GuideLibrary library) : base(capi)
    {
        this.library = library;
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        WarnOverviewConflictOnce();
        current ??= library.Default;
        ComposeSpread();
    }

    /// <summary>
    /// Tell the admin, in chat, when more than one chapter claims the overview slot.
    /// The library already resolved it deterministically; this just makes the
    /// misconfiguration visible to whoever opens the book, once per session.
    /// </summary>
    private void WarnOverviewConflictOnce()
    {
        if (warnedConflict || library.OverviewConflicts.Count == 0) return;
        warnedConflict = true;
        string ids = string.Join(", ", library.OverviewConflicts.Select(p => p.Id));
        capi.ShowChatMessage(
            $"[Almanac] Config warning: more than one guide sets overview:true. " +
            $"Using '{library.Overview?.Id}'. Remove overview from: {ids}. Only one overview is allowed.");
    }

    // --- Navigation -------------------------------------------------------

    private void OnLinkClicked(LinkTextComponent link)
    {
        string href = link.Href ?? "";

        const string chapterPrefix = "almanac://chapter/";
        if (href.StartsWith(chapterPrefix))
        {
            string rest = href.Substring(chapterPrefix.Length);
            int hash = rest.IndexOf('#');
            string id = hash >= 0 ? rest.Substring(0, hash) : rest;   // section anchors land on page 1 for now
            var target = library.ById(id);
            if (target != null) OpenChapter(target);
            else IlluminatedLogger.Info(capi, "link", $"Chapter '{id}' is unavailable");
            return;
        }

        // handbook:// and external URLs get their proper handlers with the nav phase.
        IlluminatedLogger.Info(capi, "link", $"Link not yet wired: {href}");
    }

    /// <summary>
    /// Switch chapters. A link jump records the origin for Back; sequential paging
    /// across a chapter boundary does not. atEnd opens on the chapter's last spread,
    /// for paging backwards into the previous chapter.
    /// </summary>
    private void OpenChapter(GuidePack target, bool atEnd = false, bool record = true)
    {
        if (target == current) return;
        if (record && current?.Id != null) history.Push(current.Id);
        current = target;
        pages = null;
        spreadIndex = 0;
        pendingOpenAtEnd = atEnd;
        PlayPageTurnSound();
        ComposeSpread();
    }

    private bool OnBack()
    {
        if (history.Count == 0) return true;
        var target = library.ById(history.Pop());
        if (target != null) OpenChapter(target, record: false);
        return true;
    }

    private bool OnContents()
    {
        var target = library.ContentsPage ?? library.Default;
        if (target != null) OpenChapter(target);
        return true;
    }

    // --- Composition ------------------------------------------------------

    private void ComposeSpread()
    {
        // Size the book to a fraction of the screen. GUI bounds are unscaled
        // units that get multiplied by GUIScale at render, so divide it out.
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        // The book fills a fraction of the screen; the dialog is wider so chapter
        // tabs can hang off the right edge in the margin beyond the book.
        double bookW = capi.Render.FrameWidth / scale * ScreenFraction;
        double bookH = capi.Render.FrameHeight / scale * ScreenFraction;

        const double pad = 22, gutter = 34, titleBar = 30, btnRow = 34, inset = 16, btnW = 104, btnGap = 6;
        double rightTab = current != null ? 150 : 0;
        double dialogW = bookW + rightTab;
        double pageH = System.Math.Max(140, bookH - titleBar - btnRow - pad * 2);
        double pageW = System.Math.Max(180, (bookW - gutter - pad * 2) / 2);
        double pageY = titleBar + pad;
        double contentW = pageW - inset * 2;
        double contentH = pageH - inset * 2;

        // The page geometry changed (window resize): the cached pagination is stale.
        if (capi.Render.FrameWidth != lastFrameW || capi.Render.FrameHeight != lastFrameH)
        {
            cache = null;
            lastFrameW = capi.Render.FrameWidth;
            lastFrameH = capi.Render.FrameHeight;
        }

        // Paginate every chapter once, against the real page geometry, and cache
        // it. This powers the whole-book page count and makes revisits instant.
        if (current != null)
        {
            if (cache == null)
            {
                var psw = Stopwatch.StartNew();
                cache = new Dictionary<GuidePack, List<RichTextComponentBase[]>>();
                foreach (var ch in library.Ordered)
                    cache[ch] = ChapterRenderer.RenderPages(capi, ch, library, OnLinkClicked, contentW, contentH);
                IlluminatedLogger.Info(capi, "book",
                    $"Paginated {cache.Count} chapter(s) in {psw.ElapsedMilliseconds} ms");
            }
            pages = cache.TryGetValue(current, out var cp)
                ? cp
                : ChapterRenderer.RenderPages(capi, current, library, OnLinkClicked, contentW, contentH);
        }
        else
        {
            pages ??= MockChapter.Generate(capi).Select(s => s.Components).ToList();
        }

        spreadIndex = GameMath.Clamp(spreadIndex, 0, System.Math.Max(0, (pages.Count - 1) / 2));

        if (pendingOpenAtEnd)
        {
            spreadIndex = System.Math.Max(0, (pages.Count - 1) / 2);
            pendingOpenAtEnd = false;
        }

        if (pages.Count == 0) return;

        int leftIdx = spreadIndex * 2;
        int rightIdx = leftIdx + 1;

        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dialogW, bookH).WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds bgBounds = ElementBounds.Fill;

        ElementBounds titleBarBounds = ElementBounds.Fixed(0, 0, bookW, titleBar);
        ElementBounds leftPanel = ElementBounds.Fixed(pad, pageY, pageW, pageH);
        ElementBounds rightPanel = ElementBounds.Fixed(pad + pageW + gutter, pageY, pageW, pageH);
        ElementBounds leftText = ElementBounds.Fixed(pad + inset, pageY + inset, contentW, contentH);
        ElementBounds rightText = ElementBounds.Fixed(pad + pageW + gutter + inset, pageY + inset, contentW, contentH);

        double btnY = pageY + pageH + 6;

        // Left cluster, packed left to right: Contents (when not already there),
        // Back (when there is history), then Previous.
        bool showContents = library.ContentsPage != null && current != library.ContentsPage;
        bool showBack = history.Count > 0;
        int slot = 0;
        ElementBounds Slot() => ElementBounds.Fixed(pad + slot++ * (btnW + btnGap), btnY, btnW, 28);

        ElementBounds? contentsBtn = showContents ? Slot() : null;
        ElementBounds? backBtn = showBack ? Slot() : null;
        ElementBounds prevBtn = Slot();

        ElementBounds pageLabel = ElementBounds.Fixed(bookW / 2 - 130, btnY - 3, 260, 40);
        ElementBounds nextBtn = ElementBounds.Fixed(bookW - pad - btnW, btnY, btnW, 28);

        // Chapter tab ribbons hang off the right edge of the book, into the margin.
        ElementBounds? tabsBounds = (current != null && rightTab > 0)
            ? ElementBounds.Fixed(bookW, pageY, rightTab - 6, pageH)
            : null;

        var children = new List<ElementBounds> { titleBarBounds, leftPanel, rightPanel, leftText, rightText, prevBtn, pageLabel, nextBtn };
        if (contentsBtn != null) children.Add(contentsBtn);
        if (backBtn != null) children.Add(backBtn);
        if (tabsBounds != null) children.Add(tabsBounds);
        bgBounds.WithChildren(children.ToArray());

        int maxSpread = (pages.Count - 1) / 2;
        string title = current != null ? library.Title(current) : "The Almanac";

        var composer = capi.Gui
            .CreateCompo("illuminatedbook", dialogBounds)
            .AddStaticCustomDraw(bgBounds, DrawBoard)
            .AddDialogTitleBar(title, OnTitleBarClose, null, titleBarBounds)
            .AddStaticCustomDraw(leftPanel, DrawPage)
            .AddStaticCustomDraw(rightPanel, DrawPage)
            .AddRichtext(pages[leftIdx], leftText, "leftpage");

        if (rightIdx < pages.Count)
            composer.AddRichtext(pages[rightIdx], rightText, "rightpage");

        if (showContents) composer.AddSmallButton("⌂ Contents", OnContents, contentsBtn);
        if (showBack) composer.AddSmallButton("◀ Back", OnBack, backBtn);

        composer
            .AddSmallButton("‹ Prev", OnPrevPage, prevBtn)
            .AddRichtext(PageLabel(leftIdx, rightIdx, maxSpread),
                CairoFont.WhiteSmallText(), pageLabel, "pagelabel")
            .AddSmallButton("Next ›", OnNextPage, nextBtn);

        if (tabsBounds != null)
        {
            var labels = library.Ordered.Select(p => TruncateTab(library.Title(p))).ToArray();
            int active = System.Math.Max(0, library.OrderIndex(current));
            composer.AddInteractiveElement(
                new GuiElementChapterTabs(capi, tabsBounds, labels, active, OnChapterTab), "chaptertabs");
        }

        SingleComposer = composer.Compose();
    }

    private void OnChapterTab(int index)
    {
        if (index >= 0 && index < library.Ordered.Count) OpenChapter(library.Ordered[index]);
    }

    private static string TruncateTab(string s) => s.Length <= 18 ? s : s.Substring(0, 17) + "…";

    /// <summary>
    /// Two readouts: where you are in this chapter, and where you are in the whole
    /// book. Pages count the parchment sides, so a spread shows a range like p. 3-4.
    /// Falls back to a spread count for the mock chapter, which has no library.
    /// </summary>
    private string PageLabel(int leftIdx, int rightIdx, int maxSpread)
    {
        // Bright parchment tone: the label sits on the dark leather board, not on a page.
        const string col = "#e8dcc0";
        if (current == null || cache == null || pages == null)
            return $"<font align=\"center\" color=\"{col}\">Spread {spreadIndex + 1} / {maxSpread + 1}</font>";

        int chapterPages = pages.Count;
        int leftPage = leftIdx + 1;
        int rightPage = rightIdx < chapterPages ? leftIdx + 2 : leftPage;

        int total = 0, prior = 0;
        foreach (var ch in library.Ordered)
        {
            if (ch == current) prior = total;
            total += cache.TryGetValue(ch, out var cp) ? cp.Count : 0;
        }

        string Span(int l, int r) => l == r ? $"p. {l}" : $"p. {l}–{r}";
        string chap = $"This chapter  {Span(leftPage, rightPage)} / {chapterPages}";
        string book = $"Whole book  {Span(prior + leftPage, prior + rightPage)} / {total}";
        return $"<font align=\"center\" color=\"{col}\">{chap}\n{book}</font>";
    }

    /// <summary>Dark leather book board behind both pages.</summary>
    private void DrawBoard(Context ctx, ImageSurface surface, ElementBounds b)
    {
        // Paint the leather only across the book region; the right margin stays
        // clear so the tab ribbons read as hanging off the book's edge.
        double bookWpx = capi.Render.FrameWidth * ScreenFraction;
        ctx.SetSourceRGBA(0.17, 0.11, 0.07, 1);
        ctx.Rectangle(b.drawX, b.drawY, bookWpx, b.OuterHeight);
        ctx.Fill();
    }

    /// <summary>A cream parchment page panel.</summary>
    private void DrawPage(Context ctx, ImageSurface surface, ElementBounds b)
    {
        ctx.SetSourceRGBA(0.93, 0.88, 0.76, 1);
        ctx.Rectangle(b.drawX, b.drawY, b.OuterWidth, b.OuterHeight);
        ctx.Fill();
    }

    private bool OnPrevPage()
    {
        if (spreadIndex > 0)
        {
            spreadIndex--;
            PlayPageTurnSound();
            ComposeSpread();
        }
        else if (current != null && library.Prev(current) is GuidePack prev)
        {
            // Off the front of this chapter: into the back of the previous one.
            OpenChapter(prev, atEnd: true, record: false);
        }
        return true;
    }

    private bool OnNextPage()
    {
        if (pages != null && (spreadIndex + 1) * 2 < pages.Count)
        {
            spreadIndex++;
            PlayPageTurnSound();
            ComposeSpread();
        }
        else if (current != null && library.Next(current) is GuidePack next)
        {
            // Off the end of this chapter: into the next one. The book reads straight through.
            OpenChapter(next, atEnd: false, record: false);
        }
        return true;
    }

    private void PlayPageTurnSound()
    {
        capi.World.PlaySoundAt(new AssetLocation("game:sounds/effect/writing"),
            capi.World.Player.Entity, null, true, 8);
    }

    private void OnTitleBarClose() => TryClose();

    public override bool PrefersUngrabbedMouse => true;
}
