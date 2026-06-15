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
/// page turning, chapter-to-chapter navigation through internal links, a Home
/// button to the landing page, and a jump to the contents/overview. The book
/// opens to the overview when one exists. Frame art and the page-flip animation
/// arrive in later phases.
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
    private double boardLeftPx, boardWpx, bookHpx;   // book-region paint extent, for DrawFrame
    private ImageSurface? frameSurface;              // the book art, loaded once per open
    private int soundCounter;                        // rotates the three page-turn sounds
    private int leftPageNum = -1, rightPageNum = -1;  // codex footer numbers, -1 hides one
    private GuiElementPageCorners? corners;          // the page-turn animation overlay

    // The book art is a fixed-aspect plate; pages and margins are mapped onto it as
    // fractions of the frame (tuned to bookframe.png, from Wanderer's Sketchbook).
    private const double FrameAspect = 876.0 / 590.0;
    private const double FxTitleY = 0.05;
    private const double FxContentTop = 0.12, FxContentBottom = 0.87;
    private const double FxLeftL = 0.075, FxLeftR = 0.478;
    private const double FxRightL = 0.522, FxRightR = 0.925;
    private static readonly AssetLocation FrameAsset = new("almanacilluminated:textures/gui/bookframe.png");

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
    /// Switch chapters. atEnd opens on the chapter's last spread, for paging
    /// backwards into the previous chapter. silent skips the page-turn sound, used
    /// when a flip animation already played it.
    /// </summary>
    private void OpenChapter(GuidePack target, bool atEnd = false, bool silent = false)
    {
        if (target == current && !journalMode) return;
        journalMode = false;
        current = target;
        pages = null;
        spreadIndex = 0;
        pendingOpenAtEnd = atEnd;
        if (!silent) PlayPageTurnSound();
        ComposeSpread();
    }

    /// <summary>Return to the front of the book: the landing page, first spread.</summary>
    private bool OnHome()
    {
        var target = library.Default;
        if (target == null) return true;
        if (current == target && !journalMode)
        {
            if (spreadIndex != 0) { spreadIndex = 0; PlayPageTurnSound(); ComposeSpread(); }
        }
        else OpenChapter(target);
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
        // The book is the frame art at its fixed aspect, fit inside a fraction of
        // the screen. The dialog is wider so the tab ribbons hang off both edges.
        double availW = capi.Render.FrameWidth / scale * ScreenFraction;
        double availH = capi.Render.FrameHeight / scale * ScreenFraction;
        double bookW, bookH;
        if (availW / availH > FrameAspect) { bookH = availH; bookW = bookH * FrameAspect; }
        else { bookW = availW; bookH = bookW / FrameAspect; }

        const double btnRow = 38, btnW = 104, btnGap = 6;
        bool hasTabs = library.Ordered.Count > 0;
        double tabMargin = hasTabs ? 150 : 0;
        double bookX = tabMargin;                 // book region is offset right by the left margin
        double dialogW = tabMargin + bookW + tabMargin;
        double dialogH = bookH + btnRow;

        // Page text columns, mapped onto the frame art as fractions of the book.
        double colLeftX = bookX + FxLeftL * bookW;
        double colRightX = bookX + FxRightL * bookW;
        double colW = (FxLeftR - FxLeftL) * bookW;
        double colTopY = FxContentTop * bookH;
        double colH = (FxContentBottom - FxContentTop) * bookH;
        double contentW = colW;
        double contentH = colH;

        boardLeftPx = bookX * scale;
        boardWpx = bookW * scale;
        bookHpx = bookH * scale;

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
        ComputeFooterNumbers(leftIdx, rightIdx);

        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dialogW, dialogH).WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds bgBounds = ElementBounds.Fill;

        // Title sits as a running head across the top of the open book, in ink.
        ElementBounds titleTextBounds = ElementBounds.Fixed(colLeftX, FxTitleY * bookH, (colRightX + colW) - colLeftX, 0.06 * bookH);
        ElementBounds leftText = ElementBounds.Fixed(colLeftX, colTopY, colW, colH);
        ElementBounds rightText = ElementBounds.Fixed(colRightX, colTopY, colW, colH);
        // Journal: one writing area spanning both pages, across the gutter.
        ElementBounds journalText = ElementBounds.Fixed(colLeftX, colTopY, (colRightX + colW) - colLeftX, colH);

        double btnY = bookH + 4;
        int slot = 0;
        ElementBounds Slot() => ElementBounds.Fixed(colLeftX + slot++ * (btnW + btnGap), btnY, btnW, 28);
        ElementBounds homeBtn = Slot();
        ElementBounds? saveBtn = journalMode ? Slot() : null;
        ElementBounds? addBtn = journalMode ? Slot() : null;

        // The page-turn overlay covers the whole book; it acts only in the corners.
        ElementBounds cornerBounds = ElementBounds.Fixed(bookX, 0, bookW, bookH);

        // Tab strips hang off both edges. Left: Contents, Journal, then the letters
        // already passed. Right: the current letter and the ones still ahead.
        ElementBounds? leftTabs = hasTabs ? ElementBounds.Fixed(0, colTopY, tabMargin, colH) : null;
        ElementBounds? rightTabs = hasTabs ? ElementBounds.Fixed(bookX + bookW, colTopY, tabMargin - 6, colH) : null;

        var children = new List<ElementBounds> { titleTextBounds, homeBtn, cornerBounds };
        if (journalMode) children.Add(journalText);
        else { children.Add(leftText); children.Add(rightText); }
        if (saveBtn != null) children.Add(saveBtn);
        if (addBtn != null) children.Add(addBtn);
        if (leftTabs != null) children.Add(leftTabs);
        if (rightTabs != null) children.Add(rightTabs);
        bgBounds.WithChildren(children.ToArray());

        string title = journalMode ? "Journal" : (current != null ? library.Title(current) : "The Almanac");

        var titleFont = CairoFont.WhiteSmallishText()
            .WithFont(FontRegistry.SerifDecorative)
            .WithColor(new[] { 0.28, 0.18, 0.10, 1.0 })
            .WithOrientation(EnumTextOrientation.Center);

        var composer = capi.Gui
            .CreateCompo("illuminatedbook", dialogBounds)
            .AddStaticCustomDraw(bgBounds, DrawFrame)
            .AddStaticText(title, titleFont, titleTextBounds);

        if (journalMode)
        {
            var journalFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(new[] { 0.13, 0.09, 0.05, 1.0 });
            composer.AddTextArea(journalText, OnJournalTextChanged, journalFont, "journaltext");
            var ta = composer.GetTextArea("journaltext");
            ta.Autoheight = false;
            ta.SetMaxHeight((int)colH);
        }
        else
        {
            composer.AddRichtext(pages![leftIdx], leftText, "leftpage");
            if (rightIdx < pages.Count)
                composer.AddRichtext(pages[rightIdx], rightText, "rightpage");
        }

        composer.AddSmallButton("⌂ Home", OnHome, homeBtn);
        if (journalMode)
        {
            composer.AddSmallButton("Save", OnJournalSave, saveBtn);
            composer.AddSmallButton("+ Page", OnJournalAddPage, addBtn);
        }

        if (hasTabs)
        {
            BuildTabStrips(out var lLabels, out var lActions, out var lActive,
                           out var rLabels, out var rActions, out var rActive);
            composer.AddInteractiveElement(
                new GuiElementChapterTabs(capi, leftTabs!, lLabels, lActive, i => lActions[i](), BookTabSide.Left), "lefttabs");
            composer.AddInteractiveElement(
                new GuiElementChapterTabs(capi, rightTabs!, rLabels, rActive, i => rActions[i](), BookTabSide.Right), "righttabs");
        }

        // Added last so it sits atop the pages: link clicks resolve first, then the
        // corners. It only acts in the bottom-outer corners; elsewhere clicks pass through.
        corners = new GuiElementPageCorners(capi, cornerBounds, OnTurnComplete, PlayPageTurnSound);
        composer.AddInteractiveElement(corners, "pagecorners");

        SingleComposer = composer.Compose();

        if (journalMode)
            SingleComposer.GetTextArea("journaltext")?.SetValue(journalSpreads[spreadIndex], false);
    }

    /// <summary>
    /// Sets the two footer page numbers for the current spread. The journal numbers
    /// its own sheets; a chapter numbers across the whole book, so flipping through
    /// reads like one continuous codex. A blank right page (odd last page) hides its
    /// number, as does the mock chapter, which has no library to count against.
    /// </summary>
    private void ComputeFooterNumbers(int leftIdx, int rightIdx)
    {
        if (journalMode)
        {
            leftPageNum = spreadIndex * 2 + 1;
            rightPageNum = spreadIndex * 2 + 2;
            return;
        }
        if (current == null || cache == null || pages == null || library.OrderIndex(current) < 0)
        {
            leftPageNum = rightPageNum = -1;
            return;
        }

        int prior = 0;
        foreach (var ch in library.Ordered)
        {
            if (ch == current) { break; }
            prior += cache.TryGetValue(ch, out var cp) ? cp.Count : 0;
        }
        leftPageNum = prior + leftIdx + 1;
        rightPageNum = rightIdx < pages.Count ? prior + leftIdx + 2 : -1;
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
    /// The open-book art (leather board, brackets, and both parchment pages),
    /// scaled to the book region. Margins outside it stay clear so the tab ribbons
    /// read as hanging off the edges. Falls back to a flat leather rect if the art
    /// will not load. Art: Wanderer's Sketchbook by JeanPierre, used by permission.
    /// </summary>
    private void DrawFrame(Context ctx, ImageSurface surface, ElementBounds b)
    {
        frameSurface ??= TryLoadFrame();
        double dx = b.drawX + boardLeftPx, dy = b.drawY, dw = boardWpx, dh = bookHpx;

        if (frameSurface == null)
        {
            ctx.SetSourceRGBA(0.17, 0.11, 0.07, 1);
            ctx.Rectangle(dx, dy, dw, dh);
            ctx.Fill();
            return;
        }

        ctx.Save();
        var pattern = new SurfacePattern(frameSurface);
        var m = new Matrix();
        m.Scale(frameSurface.Width / dw, frameSurface.Height / dh);
        m.Translate(-dx, -dy);
        pattern.Matrix = m;
        pattern.Filter = Filter.Best;
        ctx.SetSource(pattern);
        ctx.Rectangle(dx, dy, dw, dh);
        ctx.Fill();
        ctx.Restore();
        pattern.Dispose();

        DrawFooterNumbers(ctx, dx, dy);
    }

    /// <summary>
    /// Codex page numbers in each page's footer, with a faint sepia rule above, as
    /// fractions of the book plate. Bare numerals, lighter than body ink, the way a
    /// printed folio sets its page number.
    /// </summary>
    private void DrawFooterNumbers(Context ctx, double bookDrawX, double bookDrawY)
    {
        double X(double f) => bookDrawX + f * boardWpx;
        double Y(double f) => bookDrawY + f * bookHpx;

        const double ruleY = 0.885, numY = 0.905;

        ctx.LineWidth = GuiElement.scaled(0.7);
        ctx.SetSourceRGBA(0.28, 0.18, 0.10, 0.35);
        if (leftPageNum > 0) { ctx.NewPath(); ctx.MoveTo(X(FxLeftL), Y(ruleY)); ctx.LineTo(X(FxLeftR), Y(ruleY)); ctx.Stroke(); }
        if (rightPageNum > 0) { ctx.NewPath(); ctx.MoveTo(X(FxRightL), Y(ruleY)); ctx.LineTo(X(FxRightR), Y(ruleY)); ctx.Stroke(); }

        void Glyph(CairoFont f, string g, double cx, double cy)
        {
            f.SetupContext(ctx);
            var te = ctx.TextExtents(g);
            ctx.NewPath();
            ctx.MoveTo(X(cx) - (te.Width / 2 + te.XBearing), Y(cy));
            ctx.ShowText(g);
        }

        var numFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithFontSize(13f).WithColor(new[] { 0.28, 0.18, 0.10, 0.70 });
        if (leftPageNum > 0) Glyph(numFont, leftPageNum.ToString(), (FxLeftL + FxLeftR) / 2, numY);
        if (rightPageNum > 0) Glyph(numFont, rightPageNum.ToString(), (FxRightL + FxRightR) / 2, numY);

        // Faint turn cues in the bottom-outer corners, only where a turn exists.
        var cueFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithFontSize(20f).WithColor(new[] { 0.28, 0.18, 0.10, 0.28 });
        if (CanGoPrev()) Glyph(cueFont, "‹", FxLeftL + 0.01, numY);
        if (CanGoNext()) Glyph(cueFont, "›", FxRightR - 0.01, numY);
    }

    private ImageSurface? TryLoadFrame()
    {
        try { return GuiElement.getImageSurfaceFromAsset(capi, FrameAsset); }
        catch (Exception e) { IlluminatedLogger.Warn(capi, "book", $"Book frame art failed to load: {e.Message}"); return null; }
    }

    /// <summary>The flip animation landed: swap the spread (silently, the turn already sounded).</summary>
    private void OnTurnComplete(bool forward)
    {
        if (forward) AdvanceNext(); else AdvancePrev();
    }

    private bool CanGoPrev() => journalMode
        ? spreadIndex > 0
        : spreadIndex > 0 || (current != null && library.Prev(current) != null);

    private bool CanGoNext() => journalMode
        ? spreadIndex + 1 < System.Math.Max(1, journalSpreads.Count)
        : (pages != null && (spreadIndex + 1) * 2 < pages.Count) || (current != null && library.Next(current) != null);

    private void AdvancePrev()
    {
        if (journalMode)
        {
            if (spreadIndex > 0) { spreadIndex--; ComposeSpread(); }
            return;
        }
        if (spreadIndex > 0) { spreadIndex--; ComposeSpread(); }
        else if (current != null && library.Prev(current) is GuidePack prev)
            OpenChapter(prev, atEnd: true, silent: true);   // off the front: into the previous chapter's back
    }

    private void AdvanceNext()
    {
        if (journalMode)
        {
            if (spreadIndex + 1 < System.Math.Max(1, journalSpreads.Count)) { spreadIndex++; ComposeSpread(); }
            return;
        }
        if (pages != null && (spreadIndex + 1) * 2 < pages.Count) { spreadIndex++; ComposeSpread(); }
        else if (current != null && library.Next(current) is GuidePack next)
            OpenChapter(next, atEnd: false, silent: true);   // off the end: into the next chapter
    }

    /// <summary>One of the three parchment page-turn sounds, rotated so a run of turns varies.</summary>
    private void PlayPageTurnSound()
    {
        int n = soundCounter++ % 3 + 1;
        capi.World.PlaySoundAt(new AssetLocation($"almanacilluminated:sounds/pageturn{n}"),
            capi.World.Player.Entity, null, true, 8);
    }

    public override void OnKeyDown(KeyEvent args)
    {
        base.OnKeyDown(args);
        if (!args.Handled && args.KeyCode == (int)GlKeys.Escape) { TryClose(); args.Handled = true; }
    }

    public override void OnGuiClosed()
    {
        if (journalDirty) SaveJournal();
        frameSurface?.Dispose();
        frameSurface = null;
        base.OnGuiClosed();
    }

    public override bool PrefersUngrabbedMouse => true;
}
