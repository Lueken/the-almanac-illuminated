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
/// page turning, chapter-to-chapter navigation through internal links, and a
/// jump to the contents/overview via the ribbon tabs. The book opens to the
/// overview when one exists. Frame art and the page-flip animation arrive in
/// later phases.
/// </summary>
public class GuiDialogIlluminatedBook : GuiDialog, IAlmanacBookTabHost
{
    public const string HotkeyCode = "almanacilluminatedbook";

    /// <summary>Singleplayer reading pause, supplied by the mod system (persisted with the
    /// book settings, toggled with `.almbook pause`). Null or false = never pause. The
    /// eligibility gate is vanilla's own handbook rule: a true singleplayer session that
    /// was not opened to LAN. Multiplayer is structurally untouched.</summary>
    public Func<bool>? PauseOnOpenPref;

    /// <summary>Raised with true on open and false on close. The mod system forwards it
    /// to the server, which stamps the watched attribute the in-hands book renders from.</summary>
    public Action<bool>? OnReadingChanged;

    /// <summary>True only while a pause THIS dialog issued is outstanding. Close never
    /// unpauses a pause it did not create (an already-paused game stays paused), and a
    /// preference flipped mid-read cannot strand the game paused: ownership, not the
    /// preference, is re-checked at close.</summary>
    private bool pausedByBook;

    private bool PauseEligible => capi.IsSinglePlayer && !capi.OpenedToLan && PauseOnOpenPref?.Invoke() == true;

    /// <summary>Live flip from `.almbook pause on|off` while the book is open, so the
    /// command answers immediately instead of on the next open.</summary>
    public void ApplyPauseNow(bool pause)
    {
        if (!IsOpened() || !capi.IsSinglePlayer || capi.OpenedToLan) return;
        if (pause && !capi.IsGamePaused) { capi.PauseGame(true); pausedByBook = true; }
        else if (!pause && pausedByBook) { capi.PauseGame(false); pausedByBook = false; }
    }

    /// <summary>
    /// The bridge to the modeled book. This dialog never draws itself to the
    /// screen — PhysicalBookView captures it into an offscreen framebuffer and
    /// draws it on the tilted page — and real mouse events are remapped back into
    /// the upright space the composers were laid out in. Keyboard input needs no
    /// remap and flows through untouched.
    /// </summary>
    public IPhysicalMirror? PhysicalMirror;

    /// <summary>Local-illumination shading: the book renders by the light actually reaching
    /// the reader. Both set by the mod system; null sampler or a false gate = neutral. The
    /// capture pass is never shaded — the shade is applied to the modeled book at draw
    /// time instead, so the page cannot be tinted twice.</summary>
    public AmbientLightSampler? LightSampler;
    public Func<bool>? LightEnabled;
    private bool LightActive => LightSampler != null && LightEnabled?.Invoke() != false;

    /// <summary>The book layers with the dialog stack, above chat and other huds.</summary>
    public override double DrawOrder => 0.89;

    public override void OnRenderGUI(float deltaTime)
    {
        // The composers never draw themselves: they are captured and mapped onto
        // the modeled book, drawn here so it inherits this dialog's position in
        // the render order. The ambient shade rides along so the book is lit by
        // the reader's own light.
        var shade = LightActive ? LightSampler!.Sample(deltaTime) : AmbientLightSampler.Shade.Neutral;
        PhysicalMirror!.SetAmbientShade(shade.R, shade.G, shade.B);
        PhysicalMirror!.DrawLive(deltaTime);
    }

    /// <summary>Renders the composers for the physical view's capture pass, bypassing the suppression above.</summary>
    public void RenderForCapture(float deltaTime) => base.OnRenderGUI(deltaTime);

    public override void OnMouseDown(MouseEvent args)
    {
        if (PhysicalMirror!.TryRemap(args.X, args.Y, out int rx, out int ry))
        {
            base.OnMouseDown(new MouseEvent(rx, ry, args.Button, args.Modifiers));
        }
        else if (!TryScrapClick(args.X, args.Y))   // pinned scraps hang above the page rect
        {
            TrySideTabClick(args.X, args.Y);       // modeled tabs protrude past the side edges
        }
        args.Handled = true;   // the book owns the mouse while open, on or off the page
    }

    public override void OnMouseUp(MouseEvent args)
    {
        // Clamped remap regardless of on-page: a drag released off the
        // page must still end at the nearest page point, or the pressed
        // element stays stuck.
        PhysicalMirror!.TryRemap(args.X, args.Y, out int rx, out int ry);
        base.OnMouseUp(new MouseEvent(rx, ry, args.Button, args.Modifiers));
        args.Handled = true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (PhysicalMirror!.TryRemap(args.X, args.Y, out int rx, out int ry))
        {
            base.OnMouseMove(new MouseEvent(rx, ry, args.DeltaX, args.DeltaY));
        }
        args.Handled = true;
    }

    // Fraction of the screen the open book fills.
    private const double ScreenFraction = 0.84;

    // The tab-ribbon margins on either side of the book. Full width when the
    // screen can spare it; never narrower than the longest ribbon label needs.
    //
    // The maximum is the room a named BOOKMARK gets to reach out into, and it was
    // raised from 150 for them: a bookmark should carry its chapter's name at full
    // size rather than shrink to an index slip. This can never cost the book any
    // width. The margin below is already clamped to the space left over once the
    // book is sized, so a larger cap only ever spends screen that was empty.
    // Letter slips keep the old 150 and stay short (GuiElementChapterTabs.UnLetterW),
    // which is what makes the bookmarks read as the longer thing.
    private const double TabMarginMax = 200, TabMarginMin = 90;

    /// <summary>The dialog margin held back for the ribbons. They are modeled geometry
    /// standing outside the page, not drawn furniture, so they need almost nothing:
    /// the book region takes the rest of the dialog.</summary>
    private const double TabMarginPhysical = 12;

    // Not readonly: author preview swaps the library live via ReloadLibrary.
    private GuideLibrary library;

    private GuidePack? current;
    private List<RichTextComponentBase[]>? pages;
    private Dictionary<GuidePack, List<RichTextComponentBase[]>>? cache;

    /// <summary>Reveal-state stamp per cached chapter ('1'/'0' per gated section). A
    /// chapter whose stamp drifts from the live state re-renders on next compose, so a
    /// page earned mid-session takes its ink without a relog (the pagination cache
    /// otherwise lives as long as the dialog).</summary>
    private Dictionary<GuidePack, string>? cacheRevealSig;

    private string RevealSig(GuidePack pack)
    {
        var host = capi.ModLoader.GetModSystem<AlmanacIlluminatedModSystem>();
        if (host == null) return "";
        System.Text.StringBuilder? sb = null;
        // The chapter's own reveal leads the stamp: earning a pack-level key changes the book's
        // shape (a tab appears), not merely a page's contents, so it must invalidate the cache.
        if (!string.IsNullOrEmpty(pack.RevealedBy))
            (sb ??= new System.Text.StringBuilder()).Append(host.IsRevealed(pack.RevealedBy!) ? 'P' : 'p');
        foreach (var sec in pack.Sections)
        {
            if (string.IsNullOrEmpty(sec.RevealedBy)) continue;
            (sb ??= new System.Text.StringBuilder()).Append(host.IsRevealed(sec.RevealedBy!) ? '1' : '0');
        }

        // Quest completion belongs in the stamp too. A `doneWhen` key earned mid-session
        // changes a checkbox, and nothing else in this signature would notice: the toast
        // fired, the store held the key, and the page kept serving its old ink until a
        // relog (reported 2026-08-08 against almanactcm:is-roast-collected). Probed with
        // IsEarned, not IsRevealed, so the stamp agrees with what the renderer draws.
        // The set is ordered by the walk, so the stamp is stable between composes.
        foreach (string key in ChapterRenderer.ChapterDoneWhenKeys(pack))
            (sb ??= new System.Text.StringBuilder()).Append(host.IsEarned(key) ? 'X' : 'x');

        // whenChapterComplete needs no entry of its own: it is computed from the very
        // revealedBy keys already stamped above, so it can never drift on its own.
        return sb?.ToString() ?? "";
    }
    private int spreadIndex;
    private bool pendingOpenAtEnd;
    private bool warnedConflict;
    private bool journalMode;
    private bool cropsMode;
    private List<RichTextComponentBase[]>? cropsColumns;   // one packed column each; four per spread (two a page)
    private List<CropEntry>? catalog;
    private const int CropsColsPerSpread = 4;

    /// <summary>External tabs choose 2 (page-wide) or 4 (crops-style) columns a spread.</summary>
    private int ExternalColsPerSpread() => externalTab?.ColumnsPerSpread == 2 ? 2 : 4;
    private bool weatherMode;
    private List<RichTextComponentBase[]>? weatherPages;   // prose, paginated like a chapter (two pages a spread)
    private IAlmanacBookTab? externalTab;                  // active mod-registered tab, or null
    private List<RichTextComponentBase[]>? externalColumns;
    private float lastFrameW, lastFrameH;
    private float lastGuiScale;
    private double boardLeftPx, boardWpx, bookHpx;   // book-region paint extent, for the page faces
    private string physChapterTitle = "The Almanac";  // for the running head band
    private int soundCounter;                        // rotates the three page-turn sounds
    private int leftPageNum = -1, rightPageNum = -1;  // codex footer numbers, -1 hides one

    /// <summary>The content dims the chapter cache was paginated at. A compose whose dims
    /// differ (warm pagination guessed the wrong presentation, a mode toggle, a resize)
    /// drops the cache rather than serving pages laid out for another width.</summary>
    private double cacheContentW = -1, cacheContentH = -1;

    /// <summary>When positive, the folio prints "n of N" instead of the bare numeral —
    /// external tab pages count within their own view (a calling's detail reads
    /// "2 of 6"), where a chapter counts across the whole codex. -1 = bare numeral.</summary>
    private int footerPageTotal = -1;
    private GuiElementPageCorners? corners;          // the page-turn animation overlay

    // The open book is a fixed-aspect plate; pages and margins map onto it as
    // fractions. The aspect outlived the painted frame art it was measured from
    // (Wanderer's Sketchbook) — the modeled book keeps the same proportions, so
    // every layout fraction tuned against it still holds.
    private const double FrameAspect = 876.0 / 590.0;
    // Page columns as fractions of the plate: room for the running-head band up
    // top, the folio below, and a modest gutter and edge inset.
    private const double FxContentTop = 0.06, FxContentBottom = 0.95;
    private const double FxLeftL = 0.045, FxLeftR = 0.472;
    private const double FxRightL = 0.528;

    // The journal: one writable text block per spread, saved to a single file.
    private List<string> journalSpreads = new();
    private bool journalLoaded;
    private bool journalDirty;
    private long editToken;

    private static string JournalPath => System.IO.Path.Combine(GamePaths.DataPath, "ModData", "almanacilluminated", "journal.json");

    public override string ToggleKeyCombinationCode => HotkeyCode;

    private readonly System.Func<BlockPos?> getHomebase;
    private readonly Action<Action> ensureHomebase;
    private readonly IReadOnlyList<IAlmanacBookTab> externalTabs;

    public GuiDialogIlluminatedBook(ICoreClientAPI capi, GuideLibrary library,
        System.Func<BlockPos?> getHomebase, Action<Action> ensureHomebase,
        IReadOnlyList<IAlmanacBookTab>? externalTabs = null) : base(capi)
    {
        this.library = library;
        this.getHomebase = getHomebase;
        this.ensureHomebase = ensureHomebase;
        this.externalTabs = externalTabs ?? Array.Empty<IAlmanacBookTab>();

        // Hand each tab the host it drives to drill in and back.
        foreach (IAlmanacBookTab tab in this.externalTabs) tab.OnAttached(this);
    }

    /// <summary>IAlmanacBookTabHost: a tab changed its view-state and wants to redraw.</summary>
    public void Recompose()
    {
        externalColumns = null;
        spreadIndex = 0;
        if (IsOpened()) ComposeSpread();
    }

    /// <summary>Recompose landing on a chosen spread. The rebuild inside ComposeSpread
    /// clamps the index against the freshly built column count, so a stale target from
    /// the tab's previous layout can never page past the end.</summary>
    public void Recompose(int targetSpread)
    {
        externalColumns = null;
        spreadIndex = System.Math.Max(0, targetSpread);
        if (IsOpened()) ComposeSpread();
    }

    /// <summary>
    /// Swap in a freshly built library (author preview reload). Drops the
    /// paginated cache and the current chapter so the book rebuilds from the new
    /// content, lands back on the default page, and recomposes if it is open.
    /// </summary>
    public void ReloadLibrary(GuideLibrary newLibrary)
    {
        library = newLibrary;
        cache = null;
        current = null;
        pages = null;
        cropsColumns = null;
        weatherPages = null;
        catalog = null;
        cropsMode = false;
        weatherMode = false;
        journalMode = false;
        externalTab = null;
        externalColumns = null;
        spreadIndex = 0;
        warnedConflict = false;
        if (IsOpened()) ComposeSpread();
    }

    /// <summary>
    /// One entry in the left ribbon's mod-tab band: computed tabs (Callings) and
    /// ribbon-tab chapters (Trades, Mastery) share one ordering, so a mod's tabs
    /// group together in its chosen order regardless of which mechanism each uses.
    /// </summary>
    private sealed class ModTabEntry
    {
        public string Label = "";
        public int Order;
        public bool Foot;
        public Action Activate = () => { };
        public Func<bool> IsActive = () => false;
    }

    private List<ModTabEntry> BuildModTabs()
    {
        var list = new List<ModTabEntry>();
        foreach (IAlmanacBookTab t in externalTabs)
        {
            IAlmanacBookTab tab = t;
            list.Add(new ModTabEntry
            {
                Label = tab.Label,
                Order = tab.Order,
                Foot = tab.FootGroup,
                Activate = () => OnExternalTab(tab),
                IsActive = () => externalTab == tab,
            });
        }
        foreach (GuidePack ch in library.RibbonChapters)
        {
            GuidePack chap = ch;
            list.Add(new ModTabEntry
            {
                Label = library.Title(chap),
                Order = chap.Order ?? 100,
                Activate = () => OpenChapter(chap),
                IsActive = () => !journalMode && !cropsMode && !weatherMode && externalTab == null && current == chap,
            });
        }
        list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order)
            : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Open the book directly to a chapter by id (the almanac:// link protocol
    /// entry point). No-op with a log if the id is unknown or gated off.</summary>
    public void OpenToChapter(string id)
    {
        GuidePack? target = library.ById(id);
        if (target == null)
        {
            IlluminatedLogger.Info(capi, "link", $"almanac link: chapter '{id}' is unavailable");
            return;
        }
        OpenChapter(target);
    }

    /// <summary>Open a mod-registered tab; its columns rebuild fresh (live data).</summary>
    private void OnExternalTab(IAlmanacBookTab tab)
    {
        if (externalTab == tab) return;
        externalTab = tab;
        tab.OnActivated();                 // reset the tab to its top-level view
        journalMode = false;
        cropsMode = false;
        weatherMode = false;
        externalColumns = null;
        spreadIndex = 0;
        PlayPageTurnSound();
        ComposeSpread();
    }

    /// <summary>Open the Crops tab: a computed catalogue of every growable food, gated and climate-aware.</summary>
    private void OnCrops()
    {
        if (cropsMode) return;
        ensureHomebase(() =>
        {
            cropsMode = true;
            journalMode = false;
            weatherMode = false;
            externalTab = null;
            cropsColumns = null;
            spreadIndex = 0;
            PlayPageTurnSound();
            ComposeSpread();
        });
    }

    /// <summary>Open the Weather tab: an almanac outlook for the home climate's year.</summary>
    private void OnWeather()
    {
        if (weatherMode) return;
        ensureHomebase(() =>
        {
            weatherMode = true;
            cropsMode = false;
            journalMode = false;
            externalTab = null;
            weatherPages = null;
            spreadIndex = 0;
            PlayPageTurnSound();
            ComposeSpread();
        });
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        // Adopt the current light directly on open, instead of gliding over from
        // whatever the light was when the book was last closed.
        LightSampler?.ResetSmoothing();
        // A fresh open is not a navigation. Without this the first compose would
        // measure against wherever the book was left last time and leaf through on
        // arrival, over a capture belonging to the previous session of reading.
        navValid = false;
        // The reading pause (requested by a beta reader: "I don't want to read the days
        // away"). Vanilla handbook precedent, but with ownership: only a pause this open
        // created is released at close.
        if (PauseEligible && !capi.IsGamePaused) { capi.PauseGame(true); pausedByBook = true; }
        OnReadingChanged?.Invoke(true);
        WarnOverviewConflictOnce();
        current ??= library.Default;
        // Rebuild the knowledge- and climate-dependent views on each open: the player
        // learns foods (and may move home) while the book is shut, and the Crops/Weather
        // pages should match the tooltips when they reopen. External tabs rebuild
        // too — their data (ranks, practice) moves while the book is closed.
        cropsColumns = null;
        weatherPages = null;
        externalColumns = null;
        ComposeSpread();
    }

    /// <summary>
    /// The Crops running head. With familiarity supplied, the tab is not a reference list
    /// of everything that grows: it is the reader's own record of what they have put in the
    /// ground, and the head should say which book they are holding.
    /// </summary>
    private string CropsTitle()
    {
        var host = capi.ModLoader.GetModSystem<AlmanacIlluminatedModSystem>();
        return host?.CropFamiliarity is { Enabled: true } ? "What you have grown" : "Crops";
    }

    /// <summary>Drop the composed Crops columns so the next compose rebuilds them. Called when
    /// a setting behind the tab changes mid-session, which would otherwise not show until the
    /// book was shut and reopened.</summary>
    public void InvalidateCrops()
    {
        cropsColumns = null;
        if (cropsMode && IsOpened()) ComposeSpread();
    }

    public override void OnFinalizeFrame(float dt)
    {
        base.OnFinalizeFrame(dt);
        // A window resize or GUI-scale change while the book is open invalidates the
        // whole geometry, not just the bounds the composer re-centers on its own.
        // ComposeSpread updates the last-seen values, so this fires once per change.
        // Frame size goes 0 when minimized; recomposing at 0 would go negative.
        if (capi.Render.FrameWidth == 0 || capi.Render.FrameHeight == 0) return;
        if (IsOpened() && (capi.Render.FrameWidth != lastFrameW || capi.Render.FrameHeight != lastFrameH || RuntimeEnv.GUIScale != lastGuiScale))
        {
            ComposeSpread();
        }
    }

    // THE SPOTTED PAGE was WITHDRAWN in 0.3.3. Its spatial half lived here: a
    // Generation watch that dropped the pagination cache and re-inked the spread,
    // and a hit-test that steadied the runs under the reader's held lens. It is
    // gone with the TextLens seam it drove; see PATCHNOTES for why.

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
        if (target == current && !journalMode && !cropsMode && !weatherMode && externalTab == null) return;
        journalMode = false;
        cropsMode = false;
        weatherMode = false;
        externalTab = null;
        current = target;
        pages = null;
        spreadIndex = 0;
        pendingOpenAtEnd = atEnd;
        if (!silent) PlayPageTurnSound();
        ComposeSpread();
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
        cropsMode = false;
        weatherMode = false;
        externalTab = null;
        pages = null;
        spreadIndex = 0;
        PlayPageTurnSound();
        ComposeSpread();
    }

    // --- Bookmarks --------------------------------------------------------
    // Player state: the top-edge scraps on the physical book ARE the pins
    // (ruled 2026-07-28: scraps only render for actually-bookmarked pages).
    // Pinned via the +/− paper tag at the book's top-right; clicking a scrap
    // jumps to its pin. Capped at the four modeled scrap slots.

    public class BookmarkPin
    {
        public string ChapterId = "";
        public int Spread;
        public string Label = "";
    }

    private List<BookmarkPin> pins = new();
    private bool pinsLoaded;
    private static string BookmarksPath => System.IO.Path.Combine(GamePaths.DataPath, "ModData", "almanacilluminated", "bookmarks.json");
    public static int MaxPins => PhysicalBookView.ScrapSlots;

    public int PinCount { get { EnsurePinsLoaded(); return pins.Count; } }

    /// <summary>The pin whose page is open right now, or -1 — its scrap lies
    /// ON the spread; all others read as tucked under it.</summary>
    public int FocusedPinIndex => CurrentPinIndex();

    // --- Physical side tabs -----------------------------------------------
    // Published by ComposeSpread in physical mode; the view draws them as
    // modeled slips, and clicks route back through TrySideTabClick.
    public string[] PhysLeftTabs = Array.Empty<string>();
    public string[] PhysRightTabs = Array.Empty<string>();
    public int PhysLeftActive = -1, PhysRightActive = -1;

    /// <summary>How many of each strip's trailing tabs hang from the foot of the page
    /// rather than following on from the top. See GuiElementChapterTabs for the flat
    /// side of the same contract.</summary>
    public int PhysLeftBottom, PhysRightBottom;
    private List<Action> physLeftActions = new(), physRightActions = new();

    private bool TrySideTabClick(int x, int y)
    {
        if (PhysicalMirror?.TryRemapRaw(x, y, out float a, out float b) != true) return false;
        bool leftSide = a < 0;
        float absA = Math.Abs(a);
        if (absA < PhysicalBookView.SideTabAInner) return false;

        int row = (int)Math.Round((PhysicalBookView.SideTabRow0 - b) / PhysicalBookView.SideTabRowStep);
        float rowB = PhysicalBookView.SideTabRow0 - row * PhysicalBookView.SideTabRowStep;
        if (Math.Abs(b - rowB) > PhysicalBookView.SideTabHalfB) return false;

        // A row slot is not an array position once a group hangs from the foot, so ask
        // the renderer's own mapping rather than reimplementing it here.
        var actions = leftSide ? physLeftActions : physRightActions;
        int bottom = leftSide ? PhysLeftBottom : PhysRightBottom;
        int index = PhysicalBookView.SideTabIndexForRow(row, actions.Count, bottom);
        if (index < 0 || index >= actions.Count) return false;

        // How far out this row actually reaches has to be settled AFTER the row resolves
        // to a tab, because a named bookmark is far longer than a letter slip. Testing a
        // single outer bound would either swallow clicks on the margin beside a letter or
        // drop clicks on the far end of a bookmark.
        var labels = leftSide ? PhysLeftTabs : PhysRightTabs;
        float outer = index < labels.Length && labels[index].Length > 1
            ? PhysicalBookView.NamedTabAOuter : PhysicalBookView.SideTabAOuter;
        if (absA > outer) return false;

        actions[index]();
        return true;
    }

    /// <summary>The scrap label for pin i. Pins from before labels existed
    /// resolve their chapter title lazily and keep it.</summary>
    public string PinLabel(int i)
    {
        EnsurePinsLoaded();
        if (i < 0 || i >= pins.Count) return "";
        var pin = pins[i];
        if (string.IsNullOrEmpty(pin.Label))
        {
            var pack = library.ById(pin.ChapterId);
            pin.Label = pack != null ? (library.Title(pack) ?? pin.ChapterId) : pin.ChapterId;
            if (pin.Spread > 0) pin.Label += $" · {pin.Spread + 1}";
        }
        return pin.Label;
    }

    /// <summary>Bookmarks first shipped under the physical-book fork's own modid. The fork
    /// is gone (merged here), so carry its pins over once rather than orphan them.</summary>
    private static string LegacyBookmarksPath => System.IO.Path.Combine(GamePaths.DataPath, "ModData", "almanacphysical", "bookmarks.json");

    private void EnsurePinsLoaded()
    {
        if (pinsLoaded) return;
        pinsLoaded = true;
        try
        {
            if (!File.Exists(BookmarksPath) && File.Exists(LegacyBookmarksPath))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(BookmarksPath)!);
                File.Copy(LegacyBookmarksPath, BookmarksPath);
                IlluminatedLogger.Info(capi, "bookmarks", "Carried bookmarks over from the physical-book fork.");
            }
            if (File.Exists(BookmarksPath))
            {
                var list = JsonConvert.DeserializeObject<List<BookmarkPin>>(File.ReadAllText(BookmarksPath));
                if (list != null) pins = list.Take(MaxPins).ToList();
            }
        }
        catch (Exception e) { IlluminatedLogger.Warn(capi, "bookmarks", $"Could not load bookmarks: {e.Message}"); }
    }

    private void SavePins()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(BookmarksPath)!);
            File.WriteAllText(BookmarksPath, JsonConvert.SerializeObject(pins));
        }
        catch (Exception e) { IlluminatedLogger.Warn(capi, "bookmarks", $"Could not save bookmarks: {e.Message}"); }
    }

    private bool CurrentPinnable =>
        current?.Id != null && !journalMode && !cropsMode && !weatherMode && externalTab == null;

    private int CurrentPinIndex()
    {
        EnsurePinsLoaded();
        if (!CurrentPinnable) return -1;
        return pins.FindIndex(p => p.ChapterId == current!.Id && p.Spread == spreadIndex);
    }

    private void TogglePinCurrent()
    {
        EnsurePinsLoaded();
        if (!CurrentPinnable) return;

        int i = CurrentPinIndex();
        if (i >= 0)
        {
            pins.RemoveAt(i);
            capi.ShowChatMessage("Bookmark removed.");
        }
        else if (pins.Count >= MaxPins)
        {
            capi.ShowChatMessage($"The Almanac holds {MaxPins} bookmarks. Remove one first.");
            return;
        }
        else
        {
            string label = library.Title(current!) ?? current!.Id!;
            if (spreadIndex > 0) label += $" · {spreadIndex + 1}";
            pins.Add(new BookmarkPin { ChapterId = current!.Id!, Spread = spreadIndex, Label = label });
            capi.ShowChatMessage("Bookmarked.");
        }
        SavePins();
        ComposeSpread();   // flips the tag glyph; scrap visibility reads PinCount live
    }

    /// <summary>Jump to pin i (a scrap was clicked).</summary>
    private void OpenPin(int i)
    {
        EnsurePinsLoaded();
        if (i < 0 || i >= pins.Count) return;
        var pin = pins[i];
        var target = library.ById(pin.ChapterId);
        if (target == null)
        {
            capi.ShowChatMessage("That bookmark's chapter is no longer in the library.");
            return;
        }
        OpenChapter(target, silent: true);
        spreadIndex = Math.Max(0, pin.Spread);
        PlayPageTurnSound();
        ComposeSpread();
    }

    /// <summary>Scrap click zones share the renderer's slot layout constants —
    /// the scraps hang above the page rect (page-space b 1.0..~1.28).</summary>
    private bool TryScrapClick(int x, int y)
    {
        if (PhysicalMirror?.TryRemapRaw(x, y, out float a, out float b) != true) return false;
        if (b < 0.97f || b > 1.32f) return false;
        for (int i = 0; i < PhysicalBookView.ScrapSlots && i < PinCount; i++)
        {
            if (Math.Abs(a - PhysicalBookView.ScrapSlotCenter[i]) <= PhysicalBookView.ScrapSlotHalfWidth)
            {
                OpenPin(i);
                return true;
            }
        }
        return false;
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
        // SetValue during ComposeSpread also lands here; an unchanged text is that
        // recompose, not an edit, and must not dirty the journal or schedule a save.
        if (spreadIndex >= 0 && spreadIndex < journalSpreads.Count)
        {
            if (journalSpreads[spreadIndex] == text) return;
            journalSpreads[spreadIndex] = text;
        }
        journalDirty = true;

        // Save 3s after the last keystroke: each edit supersedes the prior timer.
        // permittedWhilePaused: in singleplayer the open book pauses the game, so
        // every journal keystroke happens paused; without the flag registration
        // crashes under extendedDebugInfo and the save waits for unpause otherwise.
        long token = ++editToken;
        capi.Event.RegisterCallback(_ => { if (token == editToken && journalDirty) SaveJournal(); }, 3000, permittedWhilePaused: true);
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

    /// <summary>Rebuild the current spread in place. For data that arrived after the page
    /// was already drawn, such as a homebase the server could only answer for once the
    /// world resumed. A no-op while shut, since opening composes anyway.</summary>
    public void RefreshSpread()
    {
        if (!IsOpened()) return;

        // Both of these are DERIVED from the homebase and cached with ??=, so recomposing
        // alone would redraw the very same pages. Dropping them is the whole point of the
        // call: it is made when the homebase itself has changed under them.
        cropsColumns = null;
        weatherPages = null;
        ComposeSpread();
    }

    /// <summary>
    /// A short line to open a homebase-derived page with when the position it was built
    /// from is only a guess. Supplied by the mod system, which is what knows whether the
    /// server ever answered. Null when the homebase is the real bound spawn.
    /// </summary>
    public Func<string?>? HomebaseNote;

    /// <summary>
    /// Opens the first column with the provisional-homebase note, when there is one. Call
    /// once per BUILD, never per compose, or the note stacks up a line at a time.
    /// </summary>
    private void PrefixHomebaseNote(List<RichTextComponentBase[]> cols)
    {
        string? note = HomebaseNote?.Invoke();
        if (string.IsNullOrEmpty(note) || cols.Count == 0) return;

        var font = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithFontSize(14f).WithSlant(Cairo.FontSlant.Italic)
            .WithColor(new[] { 0.38, 0.32, 0.25, 1.0 });
        var lead = new List<RichTextComponentBase> { new RichTextComponent(capi, note + "\n\n", font) };
        lead.AddRange(cols[0]);
        cols[0] = lead.ToArray();
    }

    /// <summary>Paginate ONE not-yet-cached (or stale-sig) chapter while the book is
    /// closed, so the whole library is warm before the first Alt+J — the mod system
    /// chains this across frames after LevelFinalize instead of paying the full
    /// pagination as one first-open freeze. Returns how many chapters still need work.
    ///
    /// The dims replicate ComposeSpread's math; if that math ever drifts from this
    /// copy, the dim guard in ComposeSpread drops the warm cache and the first open
    /// simply pays the old cost — never wrong pages.</summary>
    public int WarmPaginate()
    {
        if (library.Ordered.Count == 0) return 0;

        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double screenW = capi.Render.FrameWidth / scale;
        double screenH = capi.Render.FrameHeight / scale;
        double availW = screenW * ScreenFraction;
        double availH = screenH * ScreenFraction;
        double bookW, bookH;
        if (availW / availH > FrameAspect) { bookH = availH; bookW = bookH * FrameAspect; }
        else { bookW = availW; bookH = bookW / FrameAspect; }
        const double screenPad = 8;
        const double tabMargin = TabMarginPhysical;
        if (bookW + 2 * tabMargin > screenW - screenPad)
        {
            bookW = screenW - screenPad - 2 * tabMargin;
            bookH = bookW / FrameAspect;
        }
        double contentW = (FxLeftR - FxLeftL) * bookW;
        double contentH = (FxContentBottom - FxContentTop) * bookH;

        if (cache != null && (System.Math.Abs(cacheContentW - contentW) > 0.5
            || System.Math.Abs(cacheContentH - contentH) > 0.5))
        {
            cache = null;
            cacheRevealSig = null;
        }
        cache ??= new Dictionary<GuidePack, List<RichTextComponentBase[]>>();
        cacheRevealSig ??= new Dictionary<GuidePack, string>();
        cacheContentW = contentW;
        cacheContentH = contentH;

        int remaining = 0;
        GuidePack? next = null;
        foreach (var ch in library.Ordered)
        {
            bool fresh = cache.ContainsKey(ch)
                && cacheRevealSig.TryGetValue(ch, out string? s) && s == RevealSig(ch);
            if (!fresh) { next ??= ch; remaining++; }
        }
        if (next != null)
        {
            cache[next] = ChapterRenderer.RenderPages(capi, next, library, OnLinkClicked, contentW, contentH);
            cacheRevealSig[next] = RevealSig(next);
            remaining--;
        }
        return remaining;
    }

    private void ComposeSpread()
    {
        // Size the book to a fraction of the screen. GUI bounds are unscaled
        // units that get multiplied by GUIScale at render, so divide it out.
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double screenW = capi.Render.FrameWidth / scale;
        double screenH = capi.Render.FrameHeight / scale;
        // The book is the frame art at its fixed aspect, fit inside a fraction of
        // the screen. The dialog is wider so the tab ribbons hang off both edges.
        double availW = screenW * ScreenFraction;
        double availH = screenH * ScreenFraction;
        double bookW, bookH;
        if (availW / availH > FrameAspect) { bookH = availH; bookW = bookH * FrameAspect; }
        else { bookW = availW; bookH = bookW / FrameAspect; }

        const double btnRow = 38, btnW = 104, btnGap = 6;
        bool hasTabs = library.Ordered.Count > 0 || externalTabs.Count > 0 || library.RibbonChapters.Count > 0;
        // The whole dialog must stay inside the window. The ribbons are modeled
        // geometry outside the page, so the book region takes nearly the whole
        // dialog and only a hair of margin is held back for them.
        const double screenPad = 8;
        const double tabMargin = TabMarginPhysical;
        if (bookW + 2 * tabMargin > screenW - screenPad)
        {
            bookW = screenW - screenPad - 2 * tabMargin;
            bookH = bookW / FrameAspect;
        }
        double bookX = tabMargin;                 // book region is offset right by the left margin
        double dialogW = tabMargin + bookW + tabMargin;
        double dialogH = bookH + btnRow;

        double colLeftX = bookX + FxLeftL * bookW;
        double colRightX = bookX + FxRightL * bookW;
        double colW = (FxLeftR - FxLeftL) * bookW;
        double colTopY = FxContentTop * bookH;
        double colH = (FxContentBottom - FxContentTop) * bookH;
        double contentW = colW;
        double contentH = colH;

        // Pages paginated for other dims must never be served: a warm pass that guessed
        // the wrong presentation, a physical/flat toggle, or a resize re-renders instead.
        if (cache != null && (System.Math.Abs(cacheContentW - contentW) > 0.5
            || System.Math.Abs(cacheContentH - contentH) > 0.5))
        {
            cache = null;
            cacheRevealSig = null;
        }
        cacheContentW = contentW;
        cacheContentH = contentH;

        // The Crops tab splits each page into two narrow columns (four across a spread).
        double cropsSubGap = colW * 0.06;
        double cropsSubColW = (colW - cropsSubGap) / 2;

        boardLeftPx = bookX * scale;
        boardWpx = bookW * scale;
        bookHpx = bookH * scale;

        // The page geometry changed (window resize or GUI-scale change): the
        // cached pagination is stale.
        if (capi.Render.FrameWidth != lastFrameW || capi.Render.FrameHeight != lastFrameH || RuntimeEnv.GUIScale != lastGuiScale)
        {
            cache = null;
            cropsColumns = null;
            weatherPages = null;
            externalColumns = null;
            lastFrameW = capi.Render.FrameWidth;
            lastFrameH = capi.Render.FrameHeight;
            lastGuiScale = RuntimeEnv.GUIScale;
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
        else if (cropsMode)
        {
            catalog ??= CropCatalog.Build(capi);
            if (cropsColumns == null)
            {
                var host = capi.ModLoader.GetModSystem<AlmanacIlluminatedModSystem>();
                cropsColumns = CropsRenderer.RenderPages(capi, catalog, getHomebase(), cropsSubColW, contentH,
                    host?.CropFamiliarity, host?.CropMarks ?? GrowthMarksStyle.Tally);
                PrefixHomebaseNote(cropsColumns);
            }
            int maxSpread = System.Math.Max(0, (cropsColumns.Count - 1) / CropsColsPerSpread);
            spreadIndex = GameMath.Clamp(spreadIndex, 0, maxSpread);
            if (pendingOpenAtEnd) { spreadIndex = maxSpread; pendingOpenAtEnd = false; }
            spreadCount = maxSpread + 1;
        }
        else if (externalTab != null)
        {
            int cps = ExternalColsPerSpread();
            externalColumns ??= externalTab.BuildColumns(capi, cps == 2 ? colW : cropsSubColW, contentH)
                ?? new List<RichTextComponentBase[]>();
            if (externalColumns.Count == 0) externalColumns.Add(System.Array.Empty<RichTextComponentBase>());
            int maxSpread = System.Math.Max(0, (externalColumns.Count - 1) / cps);
            spreadIndex = GameMath.Clamp(spreadIndex, 0, maxSpread);
            if (pendingOpenAtEnd) { spreadIndex = maxSpread; pendingOpenAtEnd = false; }
            spreadCount = maxSpread + 1;
        }
        else
        {
            if (weatherMode)
            {
                var hb = getHomebase();
                if (weatherPages == null)
                {
                    weatherPages = hb != null
                        ? WeatherRenderer.RenderPages(capi, HomeWeather.Sample(capi, hb), contentW, contentH)
                        : new List<RichTextComponentBase[]> { System.Array.Empty<RichTextComponentBase>() };
                    PrefixHomebaseNote(weatherPages);
                }
                pages = weatherPages;
            }
            else if (current != null)
            {
                if (cache == null)
                {
                    var psw = Stopwatch.StartNew();
                    cache = new Dictionary<GuidePack, List<RichTextComponentBase[]>>();
                    cacheRevealSig = new Dictionary<GuidePack, string>();
                    foreach (var ch in library.Ordered)
                    {
                        cache[ch] = ChapterRenderer.RenderPages(capi, ch, library, OnLinkClicked, contentW, contentH);
                        cacheRevealSig[ch] = RevealSig(ch);
                    }
                    IlluminatedLogger.Info(capi, "book",
                        $"Paginated {cache.Count} chapter(s) in {psw.ElapsedMilliseconds} ms");
                }

                // Serve the cached pagination only while its reveal stamp still matches;
                // an earned key mid-session re-renders just the affected chapter.
                string sig = RevealSig(current);
                if (cache.TryGetValue(current, out var cp)
                    && cacheRevealSig != null
                    && cacheRevealSig.TryGetValue(current, out string? oldSig)
                    && oldSig == sig)
                {
                    pages = cp;
                }
                else
                {
                    pages = ChapterRenderer.RenderPages(capi, current, library, OnLinkClicked, contentW, contentH);
                    cache[current] = pages;
                    (cacheRevealSig ??= new Dictionary<GuidePack, string>())[current] = sig;
                }
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

        ElementBounds leftText = ElementBounds.Fixed(colLeftX, colTopY, colW, colH);
        ElementBounds rightText = ElementBounds.Fixed(colRightX, colTopY, colW, colH);
        // Journal: one writing area spanning both pages, across the gutter.
        ElementBounds journalText = ElementBounds.Fixed(colLeftX, colTopY, (colRightX + colW) - colLeftX, colH);
        // Crops: four narrow columns, two per page.
        double cropsB = cropsSubColW + cropsSubGap;
        ElementBounds[] cropsCols =
        {
            ElementBounds.Fixed(colLeftX, colTopY, cropsSubColW, colH),
            ElementBounds.Fixed(colLeftX + cropsB, colTopY, cropsSubColW, colH),
            ElementBounds.Fixed(colRightX, colTopY, cropsSubColW, colH),
            ElementBounds.Fixed(colRightX + cropsB, colTopY, cropsSubColW, colH),
        };

        double btnY = bookH + 4;
        int slot = 0;
        ElementBounds Slot() => ElementBounds.Fixed(colLeftX + slot++ * (btnW + btnGap), btnY, btnW, 28);
        ElementBounds? saveBtn = journalMode ? Slot() : null;
        ElementBounds? addBtn = journalMode ? Slot() : null;

        // The page-turn overlay covers the whole book; it acts only in the corners.
        ElementBounds cornerBounds = ElementBounds.Fixed(bookX, 0, bookW, bookH);

        // Tab strips hang off both edges. Left: Contents, Journal, then the letters
        // already passed. Right: the current letter and the ones still ahead.
        ElementBounds? leftTabs = hasTabs ? ElementBounds.Fixed(0, colTopY, tabMargin, colH) : null;
        ElementBounds? rightTabs = hasTabs ? ElementBounds.Fixed(bookX + bookW, colTopY, tabMargin - 6, colH) : null;

        // The bookmark tag hangs from the top edge at the book's top-right,
        // clear of the title, on pages that can hold a bookmark (regular chapters).
        ElementBounds? bookmarkTag = CurrentPinnable
            ? ElementBounds.Fixed(bookX + bookW - 58, 2, 34, 26)
            : null;

        var children = new List<ElementBounds> { cornerBounds };
        if (journalMode) children.Add(journalText);
        else if (cropsMode || (externalTab != null && ExternalColsPerSpread() == 4)) children.AddRange(cropsCols);
        else { children.Add(leftText); children.Add(rightText); }
        if (saveBtn != null) children.Add(saveBtn);
        if (addBtn != null) children.Add(addBtn);
        if (leftTabs != null) children.Add(leftTabs);
        if (rightTabs != null) children.Add(rightTabs);
        if (bookmarkTag != null) children.Add(bookmarkTag);
        bgBounds.WithChildren(children.ToArray());

        string title = weatherMode ? "Weather" : cropsMode ? CropsTitle() : journalMode ? "Journal"
            : externalTab != null ? externalTab.Label
            : (current != null ? library.Title(current) : "The Almanac");
        // The title is not composed as an element: DrawPageFurniture letterspaces
        // it into the running-head band itself.
        physChapterTitle = title;

        var composer = capi.Gui
            .CreateCompo("illuminatedbook", dialogBounds)
            .AddStaticCustomDraw(bgBounds, DrawFrame);

        if (journalMode)
        {
            var journalFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(new[] { 0.13, 0.09, 0.05, 1.0 });
            composer.AddTextArea(journalText, OnJournalTextChanged, journalFont, "journaltext");
            var ta = composer.GetTextArea("journaltext");
            ta.Autoheight = false;
            ta.SetMaxHeight((int)colH);
        }
        else if (cropsMode)
        {
            int baseCol = spreadIndex * CropsColsPerSpread;
            for (int i = 0; i < CropsColsPerSpread; i++)
                if (baseCol + i < cropsColumns!.Count)
                    composer.AddRichtext(cropsColumns[baseCol + i], cropsCols[i], "cropcol" + i);
        }
        else if (externalTab != null)
        {
            int cps = ExternalColsPerSpread();
            int baseCol = spreadIndex * cps;
            if (cps == 4)
            {
                for (int i = 0; i < cps; i++)
                    if (baseCol + i < externalColumns!.Count)
                        composer.AddRichtext(externalColumns[baseCol + i], cropsCols[i], "extcol" + i);
            }
            else
            {
                composer.AddRichtext(externalColumns![baseCol], leftText, "extcol0");
                if (baseCol + 1 < externalColumns.Count)
                    composer.AddRichtext(externalColumns[baseCol + 1], rightText, "extcol1");
            }
        }
        else
        {
            composer.AddRichtext(pages![leftIdx], leftText, "leftpage");
            if (rightIdx < pages.Count)
                composer.AddRichtext(pages[rightIdx], rightText, "rightpage");
        }

        if (journalMode)
        {
            composer.AddSmallButton("Save", OnJournalSave, saveBtn);
            composer.AddSmallButton("+ Page", OnJournalAddPage, addBtn);
        }

        if (bookmarkTag != null)
        {
            composer.AddInteractiveElement(
                new GuiElementBookmarkTag(capi, bookmarkTag, CurrentPinIndex() >= 0, TogglePinCurrent), "bookmarktag");
        }

        if (hasTabs)
        {
            BuildTabStrips(out var lLabels, out var lActions, out var lActive,
                           out var rLabels, out var rActions, out var rActive, out int lBottom);
            // The tabs are modeled paper slips on the page edges, drawn and
            // hit-tested by the view — publish the strip data rather than
            // composing drawn ribbons.
            PhysLeftTabs = lLabels; PhysRightTabs = rLabels;
            PhysLeftActive = lActive; PhysRightActive = rActive;
            PhysLeftBottom = lBottom; PhysRightBottom = 0;
            physLeftActions = lActions; physRightActions = rActions;
        }

        // Added last so it sits atop the pages: link clicks resolve first, then the
        // corners. It only acts in the bottom-outer corners; elsewhere clicks pass through.
        corners = new GuiElementPageCorners(capi, cornerBounds, OnTurnComplete, PlayPageTurnSound,
            forward => forward ? CanGoNext() : CanGoPrev(),
            fwd => PhysicalMirror!.BeginPageTurn(fwd));
        composer.AddInteractiveElement(corners, "pagecorners");

        SingleComposer = composer.Compose();

        if (journalMode)
            SingleComposer.GetTextArea("journaltext")?.SetValue(journalSpreads[spreadIndex], false);

        NoteNavigation();
    }

    // --- flip-through -----------------------------------------------------
    //
    // Every navigation in the book funnels through ComposeSpread, so the distance
    // moved is measured here rather than at each of the dozen call sites that can
    // move it. A recompose that did not move (a refresh, a pin toggle, data
    // arriving late) measures zero and animates nothing, which is what makes one
    // hook safe for all of them.

    /// <summary>
    /// The leafing-through animation, parked by Jeffrey on 2026-08-09: the bend was right
    /// but the rapid series did not feel right, and a wrong-feeling flourish on every tab
    /// click is worse than no flourish. With this false a section jump plays the ordinary
    /// single eased turn with the tuned bend, which is what he wants for now.
    ///
    /// Nothing else is removed. The measurement below still runs (it is a subtraction and
    /// two field writes), the view still carries BeginFlipThrough and its knobs, and the
    /// `.almbook flip*` knobs still set and save. Flipping this to true is the whole of
    /// bringing it back, once the cadence has been rethought.
    ///
    /// Deliberately static readonly rather than const: a const false would let the compiler
    /// fold the branch and report the call below as unreachable, and this build holds at
    /// zero warnings.
    /// </summary>
    private static readonly bool FlipThroughEnabled = false;

    /// <summary>Lets the view's `.almbook` readout say the flip knobs are inert rather than
    /// report them as if they were driving something.</summary>
    public static bool FlipThroughParked => !FlipThroughEnabled;

    private int navSection = int.MinValue;
    private int navSpread;
    private bool navValid;

    /// <summary>How many spreads a chapter is assumed to be worth when the jump crosses
    /// chapters. True page counts are only known once a chapter has been paginated, and
    /// paginating every chapter in between to count them would cost far more than the
    /// answer is worth: the flip count is capped at seven either way, so all this
    /// number has to do is separate "next door" from "across the book".</summary>
    private const int ChapterJumpSpreads = 4;

    /// <summary>An ordinal for whatever is on screen. Ordered chapters get their real
    /// library index, so direction between two of them is meaningful; the standalone
    /// views get distinct negative sentinels, which compare as "elsewhere" and nothing
    /// finer, because there is no ordering between the journal and a chapter to have.</summary>
    private int CurrentNavSection()
    {
        if (journalMode) return -2;
        if (cropsMode) return -3;
        if (weatherMode) return -4;
        if (externalTab != null)
        {
            for (int i = 0; i < externalTabs.Count; i++)
                if (externalTabs[i] == externalTab) return -10 - i;
            return -9;
        }
        return library.OrderIndex(current);
    }

    /// <summary>
    /// Compares where the book now sits against where it sat, and leafs through when
    /// the jump is worth more than a single turn. Anything unresolvable falls through
    /// to the ordinary one-page turn, and a physical view with nothing captured yet
    /// refuses outright, so the flat presentation and a fresh open are both untouched.
    /// </summary>
    private void NoteNavigation()
    {
        int section = CurrentNavSection();
        int spread = spreadIndex;

        if (navValid)
        {
            int dist;
            bool forward;
            if (section == navSection)
            {
                dist = System.Math.Abs(spread - navSpread);
                forward = spread >= navSpread;
            }
            else if (section >= 0 && navSection >= 0)
            {
                dist = ChapterJumpSpreads * System.Math.Abs(section - navSection);
                forward = section > navSection;
            }
            else
            {
                // One side has no place in the ordering. It is still a long way, and
                // forward is the honest default for a jump with no direction.
                dist = ChapterJumpSpreads;
                forward = true;
            }

            // Only jumps land here. A single-spread step is the page corners' own turn,
            // which they start before the spread recomposes; firing again would re-snapshot
            // and restart the swing they are already halfway through.
            if (dist > 1)
            {
                if (FlipThroughEnabled) PhysicalMirror!.BeginFlipThrough(forward, dist);
                else PhysicalMirror!.BeginPageTurn(forward);
            }
        }

        navSection = section;
        navSpread = spread;
        navValid = true;
    }

    /// <summary>
    /// Sets the two footer page numbers for the current spread. The journal numbers
    /// its own sheets; a chapter numbers across the whole book, so flipping through
    /// reads like one continuous codex. A blank right page (odd last page) hides its
    /// number, as does the mock chapter, which has no library to count against.
    /// </summary>
    private void ComputeFooterNumbers(int leftIdx, int rightIdx)
    {
        footerPageTotal = -1;
        if (journalMode)
        {
            leftPageNum = spreadIndex * 2 + 1;
            rightPageNum = spreadIndex * 2 + 2;
            return;
        }
        if (cropsMode)
        {
            int total = cropsColumns?.Count ?? 0;
            leftPageNum = spreadIndex * 2 + 1;
            rightPageNum = spreadIndex * CropsColsPerSpread + 2 < total ? spreadIndex * 2 + 2 : -1;
            return;
        }
        if (weatherMode)
        {
            leftPageNum = leftIdx + 1;
            rightPageNum = pages != null && rightIdx < pages.Count ? leftIdx + 2 : -1;
            return;
        }
        if (externalTab != null)
        {
            int cps = ExternalColsPerSpread();
            int total = externalColumns?.Count ?? 0;
            leftPageNum = spreadIndex * 2 + 1;
            rightPageNum = spreadIndex * cps + cps / 2 < total ? spreadIndex * 2 + 2 : -1;
            // Pages in THIS view: one page per column at cps 2, two columns per page at 4.
            footerPageTotal = System.Math.Max(1, cps == 2 ? total : (total + 1) / 2);
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
                                out string[] rightLabels, out List<Action> rightActions, out int rightActive,
                                out int leftBottom)
    {
        var ordered = library.Ordered;
        var pinned = library.PinnedChapters;
        var letters = new List<char>();
        var firstByLetter = new Dictionary<char, int>();
        for (int i = 0; i < ordered.Count; i++)
        {
            // The pinned band and the overview all get named tabs of their own; letting
            // them claim letters would make the thumb-index jump to curated chapters
            // instead of the run. The overview is the book's front door, not an entry.
            if (pinned.Contains(ordered[i]) || ordered[i] == library.Overview) continue;
            char l = LetterOf(library.Title(ordered[i]));
            if (!firstByLetter.ContainsKey(l)) { firstByLetter[l] = i; letters.Add(l); }
        }
        letters.Sort();

        // The split point: the current chapter's letter. The fixed tabs (Contents,
        // Journal, Crops) have no letter, and neither does the pinned band, so on
        // those the whole alphabet sits on the right.
        bool curIsPinned = current != null && pinned.Contains(current);
        bool curIsOverview = current != null && current == library.Overview;
        char cur = (!journalMode && !cropsMode && !weatherMode && externalTab == null
                && current != null && !curIsPinned && !curIsOverview && library.OrderIndex(current) >= 0)
            ? LetterOf(library.Title(current)) : '\0';

        // The left edge reads top to bottom as broad to particular. The overview is the
        // front door and sits alone at the head; the reading tabs follow; the book's own
        // tools hang from the foot, held apart so the two groups never read as one list.
        var lLabels = new List<string>();
        leftActions = new List<Action>();
        int leftHit = -1;

        var overview = library.Overview;
        if (overview != null)
        {
            GuidePack front = overview;
            if (curIsOverview) leftHit = lLabels.Count;
            lLabels.Add(library.TabTitle(front));
            leftActions.Add(() => OpenChapter(front));
        }

        // The mod-tab band: computed tabs and ribbon-tab chapters, one ordering.
        // Foot-group tabs are held back and hung under the tools instead.
        var modTabs = BuildModTabs();
        var footTabs = new List<ModTabEntry>();
        foreach (ModTabEntry mt in modTabs)
        {
            if (mt.Foot) { footTabs.Add(mt); continue; }
            if (mt.IsActive()) leftHit = lLabels.Count;
            lLabels.Add(mt.Label);
            leftActions.Add(mt.Activate);
        }
        foreach (char l in letters)
        {
            if (l >= cur) continue;
            int idx = firstByLetter[l];
            lLabels.Add(l.ToString());
            leftActions.Add(() => OpenChapter(ordered[idx]));
        }

        // The foot group: the reader's own tools, not the pack's chapters.
        string[] toolLabels = { "Journal", "Crops", "Weather", "Index" };
        var toolActions = new Action[] { OnJournal, OnCrops, OnWeather, OnContents };
        int toolActive = weatherMode ? 2 : cropsMode ? 1 : journalMode ? 0
            : (current != null && current == library.ContentsPage) ? 3 : -1;
        if (toolActive >= 0) leftHit = lLabels.Count + toolActive;
        lLabels.AddRange(toolLabels);
        leftActions.AddRange(toolActions);

        // The book's own settings hang below the tools: last of the foot group,
        // because they are about the book rather than about anything in it.
        foreach (ModTabEntry mt in footTabs)
        {
            if (mt.IsActive()) leftHit = lLabels.Count;
            lLabels.Add(mt.Label);
            leftActions.Add(mt.Activate);
        }

        leftLabels = lLabels.ToArray();
        leftBottom = toolLabels.Length + footTabs.Count;
        leftActive = leftHit;

        // The pinned band heads the right strip as named tabs, always visible
        // regardless of how deep into the alphabet the reader is.
        var rLabels = new List<string>();
        rightActions = new List<Action>();
        int pinnedActive = -1;
        foreach (var p in pinned)
        {
            if (curIsPinned && p == current) pinnedActive = rLabels.Count;
            var chapter = p;
            rLabels.Add(library.TabTitle(p));
            rightActions.Add(() => OpenChapter(chapter));
        }
        foreach (char l in letters)
        {
            if (l < cur) continue;
            int idx = firstByLetter[l];
            rLabels.Add(l.ToString());
            rightActions.Add(() => OpenChapter(ordered[idx]));
        }
        rightLabels = rLabels.ToArray();
        rightActive = pinnedActive >= 0 ? pinnedActive
            : (cur != '\0') ? rLabels.IndexOf(cur.ToString()) : -1;
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
    /// The page faces under the text. The modeled book IS the book — covers, page
    /// edges, spine — so nothing paints boards here; this composes the two
    /// parchment faces and the centre-gutter shadow that makes two bound pages
    /// out of what would otherwise read as one long sheet.
    /// </summary>
    private void DrawFrame(Context ctx, ImageSurface surface, ElementBounds b)
        => DrawPhysicalPages(ctx, b);

    // The parchment palette, measured against the game's own (2026-08-26). The page
    // used to be #ECE2C6 with a 4-shade noise of ~4 luminance stdev, sampled from the
    // flat book's third-party frame art. Vanilla's page face — item/lore/opened-normal
    // with its baked ink dropped — sits at R .813 G .761 B .596 with a stdev near 15:
    // markedly more golden, and about four times as mottled.
    //
    // Vanilla's tone is that of an aged in-world prop and this page carries small
    // text, so its brightness is kept for legibility and only vanilla's channel RATIOS
    // are adopted (G/R .936, B/R .733). That lands on #E9DBAB, a hair off the vanilla
    // page stack's own #EADDB1 — the same parchment, freshly kept.
    //
    // The noise stays under vanilla's stdev on purpose: at 64x40 across the whole
    // spread a texel here is ~14 screen px against vanilla's ~1, and matching the
    // figure exactly reads as blotching rather than grain.
    private const double ParchR = 0.915, ParchG = 0.857, ParchB = 0.671;
    private const int NoiseShades = 6;
    private const double NoiseStep = 0.021;

    /// <summary>
    /// How much of the grain survives. FuegoFish's suggestion (2026-08-26): lay the
    /// parchment down at partial opacity over its own average, which softens the colour
    /// variation and lets the text sit on a steadier ground without flattening the page
    /// to a solid fill. Applied here by pulling every tone toward the palette mean
    /// rather than by drawing twice — same result, one pass.
    /// </summary>
    private const double GrainStrength = 0.40;

    /// <summary>The noise ramp is centred on the base tone, so the base IS the page
    /// colour rather than its brightest step.</summary>
    private const double NoiseCentre = NoiseStep * (NoiseShades - 1) / 2.0;

    /// <summary>
    /// Vanilla's own parchment tones, read from the game's page texture at runtime and
    /// cached for the session. The asset is never copied into this mod — it is read
    /// from the game's own domain, so the book wears the game's parchment without
    /// redistributing a byte of it.
    ///
    /// Only the COLOURS are taken, not the image: the texels are dealt out by the same
    /// hash the procedural parchment uses, so there is no tile to repeat. Tiling the
    /// crop directly would also work (measured: its wrap seam is smaller than the
    /// variation between any two interior columns, because structureless noise wraps by
    /// definition) but 24 texels at this page's scale repeat under three times across a
    /// spread, and a repeat the eye can find is worse than no texture at all.
    /// </summary>
    private static int[]? vanillaParchment;
    private static bool vanillaParchmentTried;
    private static readonly AssetLocation VanillaPagesAsset = new("game", "textures/item/lore/pages.png");

    /// <summary>
    /// The game's parchment tones, loaded once. Returns null if the texture cannot be
    /// read, and the tuned procedural palette carries the page instead — a page that
    /// fails to a slightly different cream is a far better outcome than one that fails
    /// to no page at all.
    ///
    /// The clean field is found by luminance rather than by hardcoded bounds: the page
    /// texture carries two dark rules down its left and a dark band down its right,
    /// and everything a good margin below the median is dropped. That keeps the 46
    /// parchment tones and survives a vanilla retexture that moves them.
    /// </summary>
    private int[]? VanillaParchment()
    {
        if (vanillaParchmentTried) return vanillaParchment;
        vanillaParchmentTried = true;
        try
        {
            IAsset? asset = capi.Assets.TryGet(VanillaPagesAsset);
            if (asset == null)
            {
                IlluminatedLogger.Warn(capi, "book",
                    $"{VanillaPagesAsset} not found; using the mod's own parchment palette.");
                return null;
            }

            var bmp = asset.ToBitmap(capi);
            int w = bmp.Width, h = bmp.Height;
            var lums = new List<double>(w * h);
            var raw = new List<(int R, int G, int B)>(w * h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Alpha < 128) continue;
                    raw.Add((c.Red, c.Green, c.Blue));
                    lums.Add(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);
                }
            }
            if (raw.Count < 64) return null;

            var sorted = new List<double>(lums);
            sorted.Sort();
            double median = sorted[sorted.Count / 2];
            var field = new List<(int R, int G, int B)>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                if (lums[i] < median - 25) continue;   // the rules and the edge band
                field.Add(raw[i]);
            }
            if (field.Count < 64) return null;

            // Soften toward the field's own mean (see GrainStrength). Baked in once
            // here, so the per-texel path stays a single array read.
            double mr = field.Average(p => (double)p.R);
            double mg = field.Average(p => (double)p.G);
            double mb = field.Average(p => (double)p.B);
            var keep = new List<int>(field.Count);
            foreach (var (r, g, bl) in field)
            {
                int sr = (int)Math.Round(mr + (r - mr) * GrainStrength);
                int sg = (int)Math.Round(mg + (g - mg) * GrainStrength);
                int sb = (int)Math.Round(mb + (bl - mb) * GrainStrength);
                keep.Add((Math.Clamp(sr, 0, 255) << 16) | (Math.Clamp(sg, 0, 255) << 8) | Math.Clamp(sb, 0, 255));
            }

            vanillaParchment = keep.ToArray();
            IlluminatedLogger.Info(capi, "book",
                $"Parchment read from {VanillaPagesAsset}: {keep.Count} texels kept of {raw.Count}, "
                + $"{keep.Distinct().Count()} distinct tones at {GrainStrength:P0} grain.");
            return vanillaParchment;
        }
        catch (Exception e)
        {
            IlluminatedLogger.Warn(capi, "book",
                $"Could not read the game's parchment ({e.Message}); using the mod's own palette.");
            return null;
        }
    }

    /// <summary>
    /// The page background, in the game's own pixel language: a coarse
    /// quantized-noise parchment blitted with nearest-neighbor scaling (chunky
    /// texels, like the model's leather), a center-fold shadow built from
    /// hard-edged stepped bands, a crease, and stepped edge shading. No smooth
    /// gradients — smooth reads as "drawn", and the book should read as
    /// textured. Composed once per spread.
    /// </summary>
    private void DrawPhysicalPages(Context ctx, ElementBounds b)
    {
        double dx = b.drawX, dy = b.drawY, dw = b.InnerWidth, dh = b.InnerHeight;
        double cx = dx + dw / 2;
        int[]? pal = VanillaParchment();

        // Parchment texels: ~2 noise pixels per model voxel (the spread is
        // 30x16 voxels), scaled up with Filter.Nearest for hard pixel edges.
        const int nw = 64, nh = 40;
        using (var noise = new ImageSurface(Format.Argb32, nw, nh))
        {
            using (var nctx = new Context(noise))
            {
                for (int py = 0; py < nh; py++)
                {
                    // Stepped (not smooth) vertical warmth shift.
                    double warm = 0.02 * (py * 4 / nh) / 3.0;
                    for (int px = 0; px < nw; px++)
                    {
                        uint h = (uint)(px * 73856093 ^ py * 19349663) * 0x85EBCA6Bu;
                        h ^= h >> 16;
                        double r, g, bl;
                        if (pal != null)
                        {
                            int c = pal[h % (uint)pal.Length];
                            r = ((c >> 16) & 0xFF) / 255.0;
                            g = ((c >> 8) & 0xFF) / 255.0;
                            bl = (c & 0xFF) / 255.0;
                        }
                        else
                        {
                            double v = (NoiseStep * (h % NoiseShades) - NoiseCentre) * GrainStrength;
                            r = ParchR - v; g = ParchG - v; bl = ParchB - v;
                        }
                        nctx.SetSourceRGB(r - warm * 0.5, g - warm, bl - warm * 1.7);
                        nctx.Rectangle(px, py, 1, 1);
                        nctx.Fill();
                    }
                }
            }
            var pattern = new SurfacePattern(noise) { Filter = Filter.Nearest };
            var m = new Matrix();
            m.Scale(nw / dw, nh / dh);
            m.Translate(-dx, -dy);
            pattern.Matrix = m;
            ctx.Save();
            ctx.SetSource(pattern);
            ctx.Rectangle(dx, dy, dw, dh);
            ctx.Fill();
            ctx.Restore();
            pattern.Dispose();
        }

        // Center gutter: nested hard-edged bands darkening toward the fold.
        double gw = dw * 0.055;
        const int bands = 6;
        for (int i = 0; i < bands; i++)
        {
            double w = gw * (bands - i) / bands;
            ctx.SetSourceRGBA(0.25, 0.18, 0.10, 0.055);
            ctx.Rectangle(cx - w, dy, w * 2, dh);
            ctx.Fill();
        }

        // Crease at the fold: one hard line.
        ctx.SetSourceRGBA(0.2, 0.13, 0.07, 0.3);
        ctx.Rectangle(cx - GuiElement.scaled(1), dy, GuiElement.scaled(2), dh);
        ctx.Fill();

        // Outer page edges: two stepped shadow bands each side.
        double ew = dw * 0.012;
        foreach (double edgeX in new[] { dx, dx + dw - ew })
        {
            bool leftEdge = edgeX == dx;
            ctx.SetSourceRGBA(0.25, 0.18, 0.10, 0.10);
            ctx.Rectangle(leftEdge ? edgeX : edgeX, dy, ew, dh);
            ctx.Fill();
            ctx.SetSourceRGBA(0.25, 0.18, 0.10, 0.08);
            ctx.Rectangle(leftEdge ? edgeX : edgeX + ew * 0.4, dy, ew * 0.6, dh);
            ctx.Fill();
        }

        DrawPageFurniture(ctx, b);
    }

    /// <summary>
    /// The mockup's page furniture: small-caps running heads over each page
    /// ("THE ALMANAC" left, the open chapter right) flanked by thin rules with
    /// circle ornaments, and ·page numbers· at the foot of each page.
    /// </summary>
    private void DrawPageFurniture(Context ctx, ElementBounds b)
    {
        double bookL = b.drawX + boardLeftPx, bookW = boardWpx;
        double topY = b.drawY + GuiElement.scaled(20);
        double footY = b.drawY + b.InnerHeight - GuiElement.scaled(12);
        double[] muted = { 0.36, 0.27, 0.18, 0.85 };

        var headFont = CairoFont.WhiteSmallText()
            .WithFont(FontRegistry.SerifBody)
            .WithFontSize(15f)
            .WithWeight(Cairo.FontWeight.Bold)
            .WithColor(new[] { muted[0], muted[1], muted[2], muted[3] });
        var footFont = CairoFont.WhiteSmallText()
            .WithFont(FontRegistry.SerifBody)
            .WithFontSize(16f)
            .WithSlant(Cairo.FontSlant.Italic)
            .WithColor(new[] { 0.24, 0.17, 0.10, 0.95 });

        void Head(string text, double cx)
        {
            // Letterspacing by manual per-character advance — inserting U+2009
            // rendered stray glyphs through the GDI font path (2026-07-28).
            headFont.SetupContext(ctx);
            double tracking = GuiElement.scaled(2.2);
            double width = 0;
            foreach (char c in text) width += ctx.TextExtents(c.ToString()).XAdvance + tracking;
            width -= tracking;

            double x = cx - width / 2;
            var ext = ctx.TextExtents(text);
            foreach (char c in text)
            {
                string s = c.ToString();
                ctx.MoveTo(x, topY);
                ctx.ShowText(s);
                x += ctx.TextExtents(s).XAdvance + tracking;
            }

            // Thin rules out from the text, a small circle at each far end.
            double gap = GuiElement.scaled(14);
            double reach = Math.Min(GuiElement.scaled(85), bookW * 0.16);
            double ry = topY - ext.Height / 2 + GuiElement.scaled(1);
            ctx.LineWidth = GuiElement.scaled(1);
            ctx.SetSourceRGBA(muted[0], muted[1], muted[2], 0.5);
            foreach (int dir in new[] { -1, 1 })
            {
                double x0 = cx + dir * (width / 2 + gap);
                double x1 = cx + dir * (width / 2 + gap + reach);
                ctx.MoveTo(x0, ry);
                ctx.LineTo(x1, ry);
                ctx.Stroke();
                ctx.NewPath();
                ctx.Arc(x1 + dir * GuiElement.scaled(4), ry, GuiElement.scaled(2.4), 0, Math.PI * 2);
                ctx.Stroke();
            }
        }

        void Foot(string text, double cx)
        {
            footFont.SetupContext(ctx);
            var ext = ctx.TextExtents(text);
            ctx.MoveTo(cx - ext.Width / 2 - ext.XBearing, footY);
            ctx.ShowText(text);
        }

        double leftCx = bookL + bookW * 0.25, rightCx = bookL + bookW * 0.75;
        string rightHead = physChapterTitle == "The Almanac" ? "Illvminated" : physChapterTitle;
        Head(ChapterRenderer.SmallCaps("The Almanac"), leftCx);
        Head(ChapterRenderer.SmallCaps(rightHead), rightCx);
        // Foot numerals come from ComputeFooterNumbers (already run this compose),
        // not raw spreadIndex math — an external tab counts within its own view
        // ("5 of 6") and a blank right page hides its numeral entirely.
        string FolioText(int n) => footerPageTotal > 0 ? $"{n} of {footerPageTotal}" : $"· {n} ·";
        if (leftPageNum > 0) Foot(FolioText(leftPageNum), leftCx);
        if (rightPageNum > 0) Foot(FolioText(rightPageNum), rightCx);
    }

    /// <summary>The flip animation landed: swap the spread (silently, the turn already sounded).</summary>
    private void OnTurnComplete(bool forward)
    {
        if (forward) AdvanceNext(); else AdvancePrev();
    }

    private bool CanGoPrev() => (journalMode || cropsMode || weatherMode || externalTab != null)
        ? spreadIndex > 0
        : spreadIndex > 0 || (current != null && library.Prev(current) != null);

    private bool CanGoNext()
    {
        if (journalMode) return spreadIndex + 1 < System.Math.Max(1, journalSpreads.Count);
        if (cropsMode) return cropsColumns != null && (spreadIndex + 1) * CropsColsPerSpread < cropsColumns.Count;
        if (externalTab != null) return externalColumns != null && (spreadIndex + 1) * ExternalColsPerSpread() < externalColumns.Count;
        if (weatherMode) return pages != null && (spreadIndex + 1) * 2 < pages.Count;
        return (pages != null && (spreadIndex + 1) * 2 < pages.Count) || (current != null && library.Next(current) != null);
    }

    private void AdvancePrev()
    {
        if (journalMode || cropsMode || weatherMode || externalTab != null)
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
        if (cropsMode)
        {
            if (cropsColumns != null && (spreadIndex + 1) * CropsColsPerSpread < cropsColumns.Count) { spreadIndex++; ComposeSpread(); }
            return;
        }
        if (externalTab != null)
        {
            if (externalColumns != null && (spreadIndex + 1) * ExternalColsPerSpread() < externalColumns.Count) { spreadIndex++; ComposeSpread(); }
            return;
        }
        if (weatherMode)
        {
            if (pages != null && (spreadIndex + 1) * 2 < pages.Count) { spreadIndex++; ComposeSpread(); }
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

    /// <summary>
    /// The one gate every close passes through, so the book always stows before it
    /// goes away. Guarding here rather than at each call site is deliberate: the
    /// game closes this dialog itself, without asking us. GuiDialog.OnKeyDown calls
    /// TryClose() directly when the toggle hotkey (Alt+J) is pressed on an open
    /// dialog, and OnKeyCombinationToggle does the same through Toggle(). Neither
    /// consults our hotkey handler, so patching only our own paths would leave the
    /// most common one snapping the book shut with no animation.
    ///
    /// Refusing the close leaves args.Handled false in the base handler, which is
    /// what lets our hotkey handler still see the press and treat a second Alt+J
    /// mid-stow as "reopen".
    /// </summary>
    public override bool TryClose()
    {
        if (!PhysicalMirror!.StowComplete)
        {
            // Release the reading pause as the stow BEGINS, not when the dialog finally
            // closes. Two reasons. Feel: the stow is already "done reading", so the world
            // resuming under the fold-shut is right. Correctness: the stow's real close
            // arrives via a game-time RegisterCallback, and game callbacks do not run
            // while paused — the stow would land, the callback would freeze, and the
            // renderer's draw-out branch would march openT straight back up: the book
            // sprang visibly back open (found in play, 2026-07-31).
            if (pausedByBook) { capi.PauseGame(false); pausedByBook = false; }
            PhysicalMirror.RequestClose();
            return false;
        }
        return base.TryClose();
    }

    public override void OnKeyDown(KeyEvent args)
    {
        base.OnKeyDown(args);
        if (!args.Handled && args.KeyCode == (int)GlKeys.Escape)
        {
            // TryClose above turns this into a stow when the book is physical.
            TryClose();
            args.Handled = true;
        }
    }

    public override void OnGuiClosed()
    {
        if (pausedByBook) { capi.PauseGame(false); pausedByBook = false; }
        OnReadingChanged?.Invoke(false);
        if (journalDirty) SaveJournal();
        base.OnGuiClosed();
    }

    public override bool PrefersUngrabbedMouse => true;
}
