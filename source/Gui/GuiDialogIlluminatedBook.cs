using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Cairo;
using Newtonsoft.Json;
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
    private bool journalMode;
    private float lastFrameW, lastFrameH;
    private double boardLeftPx, boardWpx;   // book-region paint extent, for DrawBoard
    private readonly Stack<string> history = new();

    // The journal: one writable text block per spread, saved to a single file.
    private List<string> journalSpreads = new();
    private bool journalLoaded;
    private bool journalDirty;
    private long editToken;

    private static string JournalPath => System.IO.Path.Combine(GamePaths.DataPath, "ModData", "almanacilluminated", "journal.json");

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
        if (target == current && !journalMode) return;
        bool wasJournal = journalMode;
        journalMode = false;
        if (record && !wasJournal && current?.Id != null && target != current) history.Push(current.Id);
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

    private void OnContents()
    {
        var target = library.ContentsPage ?? library.Default;
        if (target != null) OpenChapter(target);
    }

    /// <summary>Open the personal journal: a writable, file-saved notebook.</summary>
    private void OnJournal()
    {
        if (journalMode) return;
        EnsureJournalLoaded();
        journalMode = true;
        pages = null;
        spreadIndex = 0;
        PlayPageTurnSound();
        ComposeSpread();
    }

    private void EnsureJournalLoaded()
    {
        if (journalLoaded) return;
        journalLoaded = true;
        try
        {
            if (File.Exists(JournalPath))
            {
                var list = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(JournalPath));
                if (list != null) journalSpreads = list;
            }
        }
        catch (Exception e) { IlluminatedLogger.Warn(capi, "journal", $"Could not load journal: {e.Message}"); }
        if (journalSpreads.Count == 0) journalSpreads.Add("");
    }

    private void SaveJournal()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(JournalPath)!);
            File.WriteAllText(JournalPath, JsonConvert.SerializeObject(journalSpreads, Formatting.Indented));
            journalDirty = false;
        }
        catch (Exception e) { IlluminatedLogger.Warn(capi, "journal", $"Could not save journal: {e.Message}"); }
    }

    /// <summary>Update the model on each keystroke and schedule a debounced auto-save.</summary>
    private void OnJournalTextChanged(string text)
    {
        if (spreadIndex >= 0 && spreadIndex < journalSpreads.Count) journalSpreads[spreadIndex] = text;
        journalDirty = true;

        // Save 3s after the last keystroke: each edit supersedes the prior timer.
        long token = ++editToken;
        capi.Event.RegisterCallback(_ => { if (token == editToken && journalDirty) SaveJournal(); }, 3000);
    }

    private bool OnJournalSave()
    {
        SaveJournal();
        ComposeSpread();
        return true;
    }

    private bool OnJournalAddPage()
    {
        journalSpreads.Add("");
        spreadIndex = journalSpreads.Count - 1;
        journalDirty = true;
        PlayPageTurnSound();
        ComposeSpread();
        return true;
    }

    // --- Composition ------------------------------------------------------

    private void ComposeSpread()
    {
        // Size the book to a fraction of the screen. GUI bounds are unscaled
        // units that get multiplied by GUIScale at render, so divide it out.
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        // The book fills a fraction of the screen; the dialog is wider so the tab
        // ribbons can hang off both edges in the margins beyond the book.
        double bookW = capi.Render.FrameWidth / scale * ScreenFraction;
        double bookH = capi.Render.FrameHeight / scale * ScreenFraction;

        const double pad = 22, gutter = 34, titleBar = 30, btnRow = 34, inset = 16, btnW = 104, btnGap = 6;
        bool hasTabs = library.Ordered.Count > 0;
        double tabMargin = hasTabs ? 150 : 0;
        double bookX = tabMargin;                 // book region is offset right by the left margin
        double dialogW = tabMargin + bookW + tabMargin;
        double pageH = System.Math.Max(140, bookH - titleBar - btnRow - pad * 2);
        double pageW = System.Math.Max(180, (bookW - gutter - pad * 2) / 2);
        double pageY = titleBar + pad;
        double contentW = pageW - inset * 2;
        double contentH = pageH - inset * 2;

        boardLeftPx = bookX * scale;
        boardWpx = bookW * scale;

        // The page geometry changed (window resize): the cached pagination is stale.
        if (capi.Render.FrameWidth != lastFrameW || capi.Render.FrameHeight != lastFrameH)
        {
            cache = null;
            lastFrameW = capi.Render.FrameWidth;
            lastFrameH = capi.Render.FrameHeight;
        }

        // Resolve the spread content: a chapter's paginated richtext, or the
        // journal's writable spreads. The journal is not paginated; one spread is
        // one writable page across the whole sheet.
        int spreadCount;
        if (journalMode)
        {
            EnsureJournalLoaded();
            spreadCount = System.Math.Max(1, journalSpreads.Count);
            spreadIndex = GameMath.Clamp(spreadIndex, 0, spreadCount - 1);
        }
        else
        {
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
            spreadCount = (pages.Count - 1) / 2 + 1;
        }

        int leftIdx = spreadIndex * 2;
        int rightIdx = leftIdx + 1;

        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dialogW, bookH).WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds bgBounds = ElementBounds.Fill;

        ElementBounds titleBarBounds = ElementBounds.Fixed(bookX, 0, bookW, titleBar);
        ElementBounds leftPanel = ElementBounds.Fixed(bookX + pad, pageY, pageW, pageH);
        ElementBounds rightPanel = ElementBounds.Fixed(bookX + pad + pageW + gutter, pageY, pageW, pageH);
        ElementBounds leftText = ElementBounds.Fixed(bookX + pad + inset, pageY + inset, contentW, contentH);
        ElementBounds rightText = ElementBounds.Fixed(bookX + pad + pageW + gutter + inset, pageY + inset, contentW, contentH);
        // Journal: one continuous sheet and one writing area across the whole spread.
        ElementBounds spreadPanel = ElementBounds.Fixed(bookX + pad, pageY, pageW * 2 + gutter, pageH);
        ElementBounds journalText = ElementBounds.Fixed(bookX + pad + inset, pageY + inset, pageW * 2 + gutter - inset * 2, pageH - inset * 2);

        double btnY = pageY + pageH + 6;
        bool showBack = !journalMode && history.Count > 0;
        int slot = 0;
        ElementBounds Slot() => ElementBounds.Fixed(bookX + pad + slot++ * (btnW + btnGap), btnY, btnW, 28);
        ElementBounds? backBtn = showBack ? Slot() : null;
        ElementBounds? saveBtn = journalMode ? Slot() : null;
        ElementBounds? addBtn = journalMode ? Slot() : null;
        ElementBounds prevBtn = Slot();

        ElementBounds pageLabel = ElementBounds.Fixed(bookX + bookW / 2 - 130, btnY - 3, 260, 40);
        ElementBounds nextBtn = ElementBounds.Fixed(bookX + bookW - pad - btnW, btnY, btnW, 28);

        // Tab strips hang off both edges. Left: Contents, Journal, then the letters
        // already passed. Right: the current letter and the ones still ahead.
        ElementBounds? leftTabs = hasTabs ? ElementBounds.Fixed(0, pageY, tabMargin, pageH) : null;
        ElementBounds? rightTabs = hasTabs ? ElementBounds.Fixed(bookX + bookW, pageY, tabMargin - 6, pageH) : null;

        var children = new List<ElementBounds> { titleBarBounds, prevBtn, pageLabel, nextBtn };
        if (journalMode) { children.Add(spreadPanel); children.Add(journalText); }
        else { children.Add(leftPanel); children.Add(rightPanel); children.Add(leftText); children.Add(rightText); }
        if (backBtn != null) children.Add(backBtn);
        if (saveBtn != null) children.Add(saveBtn);
        if (addBtn != null) children.Add(addBtn);
        if (leftTabs != null) children.Add(leftTabs);
        if (rightTabs != null) children.Add(rightTabs);
        bgBounds.WithChildren(children.ToArray());

        int maxSpread = spreadCount - 1;
        string title = journalMode ? "Journal" : (current != null ? library.Title(current) : "The Almanac");

        var composer = capi.Gui
            .CreateCompo("illuminatedbook", dialogBounds)
            .AddStaticCustomDraw(bgBounds, DrawBoard)
            .AddDialogTitleBar(title, OnTitleBarClose, null, titleBarBounds);

        if (journalMode)
        {
            var journalFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(new[] { 0.13, 0.09, 0.05, 1.0 });
            composer.AddStaticCustomDraw(spreadPanel, DrawPage)
                    .AddTextArea(journalText, OnJournalTextChanged, journalFont, "journaltext");
            var ta = composer.GetTextArea("journaltext");
            ta.Autoheight = false;
            ta.SetMaxHeight((int)(pageH - inset * 2));
        }
        else
        {
            composer.AddStaticCustomDraw(leftPanel, DrawPage)
                    .AddStaticCustomDraw(rightPanel, DrawPage)
                    .AddRichtext(pages![leftIdx], leftText, "leftpage");
            if (rightIdx < pages.Count)
                composer.AddRichtext(pages[rightIdx], rightText, "rightpage");
        }

        if (showBack) composer.AddSmallButton("◀ Back", OnBack, backBtn);
        if (journalMode)
        {
            composer.AddSmallButton("Save", OnJournalSave, saveBtn);
            composer.AddSmallButton("+ Page", OnJournalAddPage, addBtn);
        }

        composer
            .AddSmallButton("‹ Prev", OnPrevPage, prevBtn)
            .AddRichtext(journalMode ? JournalLabel(spreadCount) : PageLabel(leftIdx, rightIdx, maxSpread),
                CairoFont.WhiteSmallText(), pageLabel, "pagelabel")
            .AddSmallButton("Next ›", OnNextPage, nextBtn);

        if (hasTabs)
        {
            BuildTabStrips(out var lLabels, out var lActions, out var lActive,
                           out var rLabels, out var rActions, out var rActive);
            composer.AddInteractiveElement(
                new GuiElementChapterTabs(capi, leftTabs!, lLabels, lActive, i => lActions[i](), BookTabSide.Left), "lefttabs");
            composer.AddInteractiveElement(
                new GuiElementChapterTabs(capi, rightTabs!, rLabels, rActive, i => rActions[i](), BookTabSide.Right), "righttabs");
        }

        SingleComposer = composer.Compose();

        if (journalMode)
            SingleComposer.GetTextArea("journaltext")?.SetValue(journalSpreads[spreadIndex], false);
    }

    private string JournalLabel(int spreadCount)
    {
        int l = spreadIndex * 2 + 1, r = spreadIndex * 2 + 2;
        return $"<font align=\"center\" color=\"#e8dcc0\">Journal  pages {l}–{r} / {spreadCount * 2}</font>";
    }

    /// <summary>
    /// Builds the two tab strips. Left holds Contents, Journal, then the letter
    /// groups already passed; right holds the current letter and those still ahead.
    /// Each letter jumps to the first chapter under it. Selecting a letter shifts
    /// the split, the way a thumb-index moves as you read deeper into the book.
    /// </summary>
    private void BuildTabStrips(out string[] leftLabels, out List<Action> leftActions, out int leftActive,
                                out string[] rightLabels, out List<Action> rightActions, out int rightActive)
    {
        var ordered = library.Ordered;
        var letters = new List<char>();
        var firstByLetter = new Dictionary<char, int>();
        for (int i = 0; i < ordered.Count; i++)
        {
            char l = LetterOf(library.Title(ordered[i]));
            if (!firstByLetter.ContainsKey(l)) { firstByLetter[l] = i; letters.Add(l); }
        }
        letters.Sort();

        // The split point: the current chapter's letter. Contents/Journal have no
        // letter, so the whole alphabet sits on the right.
        char cur = (!journalMode && current != null && library.OrderIndex(current) >= 0)
            ? LetterOf(library.Title(current)) : '\0';

        var lLabels = new List<string> { "Contents", "Journal" };
        leftActions = new List<Action> { OnContents, OnJournal };
        foreach (char l in letters)
        {
            if (l >= cur) continue;
            int idx = firstByLetter[l];
            lLabels.Add(l.ToString());
            leftActions.Add(() => OpenChapter(ordered[idx]));
        }
        leftLabels = lLabels.ToArray();
        leftActive = journalMode ? 1 : (current == library.ContentsPage ? 0 : -1);

        var rLabels = new List<string>();
        rightActions = new List<Action>();
        foreach (char l in letters)
        {
            if (l < cur) continue;
            int idx = firstByLetter[l];
            rLabels.Add(l.ToString());
            rightActions.Add(() => OpenChapter(ordered[idx]));
        }
        rightLabels = rLabels.ToArray();
        rightActive = (cur != '\0') ? rLabels.IndexOf(cur.ToString()) : -1;
    }

    /// <summary>The first letter a chapter indexes under, ignoring a leading "The ".</summary>
    private static char LetterOf(string title)
    {
        string t = title.Trim();
        if (t.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(4);
        foreach (char c in t) if (char.IsLetter(c)) return char.ToUpperInvariant(c);
        return '#';
    }

    /// <summary>
    /// Two readouts: where you are in this chapter, and where you are in the whole
    /// book. Pages count the parchment sides, so a spread shows a range like p. 3-4.
    /// Falls back to a spread count for the mock chapter, which has no library.
    /// </summary>
    private string PageLabel(int leftIdx, int rightIdx, int maxSpread)
    {
        // Bright parchment tone: the label sits on the dark leather board, not on a page.
        const string col = "#e8dcc0";
        if (journalMode)
            return $"<font align=\"center\" color=\"{col}\">Journal</font>";
        if (current == null || cache == null || pages == null || library.OrderIndex(current) < 0)
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
        // Paint the leather only across the book region; the side margins stay
        // clear so the tab ribbons read as hanging off the book's edges.
        ctx.SetSourceRGBA(0.17, 0.11, 0.07, 1);
        ctx.Rectangle(b.drawX + boardLeftPx, b.drawY, boardWpx, b.OuterHeight);
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
        if (journalMode)
        {
            if (spreadIndex > 0) { spreadIndex--; PlayPageTurnSound(); ComposeSpread(); }
            return true;
        }
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
        if (journalMode)
        {
            if (spreadIndex + 1 < System.Math.Max(1, journalSpreads.Count)) { spreadIndex++; PlayPageTurnSound(); ComposeSpread(); }
            return true;
        }
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

    public override void OnGuiClosed()
    {
        if (journalDirty) SaveJournal();
        base.OnGuiClosed();
    }

    public override bool PrefersUngrabbedMouse => true;
}
