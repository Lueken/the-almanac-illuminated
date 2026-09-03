using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AlmanacIlluminated;

/// <summary>
/// Turns a parsed <see cref="GuidePack"/> into rendered sections the book can
/// lay out. One GuideSection becomes one RenderedSection (one page) for now;
/// the hybrid paginator (keepTogether, pageBreakBefore) comes later.
///
/// Text fields run through VtmlUtil so inline VTML, itemstacks, and links work
/// the same as the handbook. Per-block `requires` gating is applied here.
/// recipe and figure are placeholders this increment; native renders follow.
/// </summary>
public static class ChapterRenderer
{
    // Dark ink, for reading on parchment.
    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 1 };

    // Warm parchment-brown default when a chapter declares no accentColor.
    private static readonly double[] DefaultAccent = { 0.47, 0.33, 0.18, 1 };

    /// <summary>Faked small-caps: plain uppercase. U+2009 thin-space tracking
    /// rendered as stray glyphs through the GDI font path (in-game 2026-07-28),
    /// so richtext gets no inserted characters; only Cairo direct draws (the
    /// running heads) letterspace, by advancing per character.</summary>
    public static string SmallCaps(string s) => s.ToUpperInvariant();

    /// <summary>Chapter accent from "#rrggbb", else the parchment default.</summary>
    public static double[] ParseAccent(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return DefaultAccent;
        string h = hex.TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int v))
            return DefaultAccent;
        return new[] { ((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0, 1.0 };
    }

    // Air above a section heading so it reads as a fresh section, never crowding the
    // paragraph before it. Kept tight below (the heading bonds to its first line).
    private const float HeadingTopMargin = 16;

    // A blank-line break placed BETWEEN sections (not before the first, not after the
    // last). UnscaledMarginTop alone did not reserve visible height across the atom
    // boundary; a ClearFloat spacer does, reliably, the way the callout margins do.
    private const float SectionGap = 18;

    /// <summary>
    /// Renders a chapter into book pages. Every block is laid out and measured at
    /// the page width, then blocks are packed into page-height columns. A section
    /// with keepTogether stays whole; pageBreakBefore forces a fresh page; a title
    /// never orphans from its first block. Returns one component array per page.
    /// </summary>
    public static List<RichTextComponentBase[]> RenderPages(ICoreClientAPI capi, GuidePack pack, GuideLibrary library, Action<LinkTextComponent>? onLink, double pageWidth, double pageHeight)
    {
        // Run ordinals restart per chapter so they are stable across recomposes of
        // the same pack — the lens's steady set is keyed on them.
        runCounter = 0;
        var atoms = BuildAtoms(capi, pack, library, onLink);
        return Paginate(capi, atoms, pageWidth, pageHeight);
    }

    // ---- THE SPOTTED PAGE (TextLens seam) --------------------------------------
    // Every text run passes through Lens() on its way to components, and the
    // components it produced get stamped with the run's ordinal via Tag(). With no
    // Filter wired both are near-free passthroughs. NOTHING is protected by rule
    // (2026-09-03): titles, hints, the contents index, quest text, ledgers,
    // captions and callouts all fox alike. The only texts kept clean are pure UI
    // glyphs — the drop cap's single letter, checkbox marks, step bullets — where
    // a stray combining mark reads as a render bug, not decay.

    private static int runCounter;

    /// <summary>One text run through the external lens: ordinal assigned, filter
    /// applied unless this run is currently steadied under the reader's lens.</summary>
    private static string Lens(string text, out int ordinal)
    {
        ordinal = runCounter++;
        var f = TextLens.Filter;
        if (f == null || string.IsNullOrEmpty(text) || TextLens.SteadyRuns.Contains(ordinal)) return text;
        try { return f(text, unchecked(ordinal * 2654435761L)); } catch { return text; }
    }

    /// <summary>As <see cref="Lens"/>, but through the PLAIN filter, for runs whose
    /// component renders text verbatim (callout boxes) — markup from the rich
    /// filter would print literally there.</summary>
    private static string LensPlain(string text, out int ordinal)
    {
        ordinal = runCounter++;
        var f = TextLens.PlainFilter;
        if (f == null || string.IsNullOrEmpty(text) || TextLens.SteadyRuns.Contains(ordinal)) return text;
        try { return f(text, unchecked(ordinal * 2654435761L)); } catch { return text; }
    }

    /// <summary>Stamp components [from..end) as belonging to a run, so the dialog's
    /// lens hit-test can steady exactly the lines under the cursor.</summary>
    private static void Tag(List<RichTextComponentBase> comps, int from, int ordinal)
    {
        if (TextLens.Filter == null && TextLens.PlainFilter == null) return;
        for (int i = from; i < comps.Count; i++)
            TextLens.RunOf.AddOrUpdate(comps[i], new System.Runtime.CompilerServices.StrongBox<int>(ordinal));
    }

    /// <summary>An indivisible run of components the paginator places as one unit.</summary>
    private sealed class Atom
    {
        public readonly List<RichTextComponentBase> Comps = new();
        public bool PageBreakBefore;
        public bool KeepTogether;
    }

    /// <summary>
    /// Turns a chapter into the flowable atoms the paginator packs. A keepTogether
    /// section is a single atom. Otherwise the title binds to the first block (so a
    /// heading never ends a page alone) and each later block is its own atom.
    /// </summary>
    private static List<Atom> BuildAtoms(ICoreClientAPI capi, GuidePack pack, GuideLibrary library, Action<LinkTextComponent>? onLink)
    {
        double[] accent = ParseAccent(pack.AccentColor);

        // The mockup's typographic voice: small-caps section headers, an airier
        // body measure, and an accent-colored drop cap two lines tall.
        var heading = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithFontSize(21f).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);
        var body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithFontSize(16.5f).WithLineHeightMultiplier(1.12).WithColor(Ink);
        var italic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithFontSize(16.5f).WithLineHeightMultiplier(1.12).WithSlant(Cairo.FontSlant.Italic).WithColor(Ink);
        // The drop cap floats left and the paragraph wraps around whatever height it
        // MEASURES, which is the font's line box and not the ink of the letter.
        //
        // At 50pt with no multiplier the box is 50px against a body line of
        // 16.5 x 1.12 = 18.48px. That is 2.71 lines, and a float reserves whole lines,
        // so three lines were indented while the letter's cap-height only reached into
        // about 1.9 of them. The third line cleared a letter that had already ended,
        // which reads as a hole punched under the capital.
        //
        // Pinning the multiplier makes the reserved box exactly two body lines
        // (18.48 x 2 = 36.96, so 36.96 / 50 = 0.739) without touching the size of the
        // letter itself. Two lines is also the ordinary book setting for a cap this
        // size. Keep this derived rather than a magic number: if the body font or its
        // multiplier moves, recompute it, because the whole point is the ratio.
        const float DropCapPt = 50f;
        const float DropCapLines = 2f;
        var dropCap = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithFontSize(DropCapPt).WithWeight(Cairo.FontWeight.Bold).WithColor(accent)
            .WithLineHeightMultiplier(16.5f * 1.12f * DropCapLines / DropCapPt);

        string initial = DeriveInitial(pack.Byline, pack.Title);
        var atoms = new List<Atom>();
        var revealHost = capi.ModLoader.GetModSystem<AlmanacIlluminatedModSystem>();

        // Completion probe for `doneWhen` and `whenChapterComplete`. Strict, not
        // degrade-open: an unregistered namespace means unearned. Without a host at
        // all, nothing can have been earned either.
        System.Func<string, bool> earned = revealHost != null ? revealHost.IsEarned : _ => false;

        // Every reveal key this chapter hangs on, chapter-level and section-level.
        // Computed once per render and only when a section asks for it.
        bool? chapterComplete = null;
        bool ChapterComplete()
        {
            if (chapterComplete.HasValue) return chapterComplete.Value;
            bool all = true;
            foreach (string key in ChapterRevealKeys(pack))
                if (!earned(key)) { all = false; break; }
            chapterComplete = all;
            return all;
        }

        for (int si = 0; si < pack.Sections.Count; si++)
        {
            var sec = pack.Sections[si];

            // An unfinished chapter hides this section outright, before the reveal
            // check below, because showing nothing is the stronger of the two rules.
            if (sec.WhenChapterComplete && !ChapterComplete()) continue;

            var title = new List<RichTextComponentBase>();
            string? secTitle = GuideLibrary.Localize(sec.Title);
            if (!string.IsNullOrEmpty(secTitle))
            {
                string tt = LensPlain(SmallCaps(secTitle!), out int titleOrd);
                title.Add(new RichTextComponent(capi, tt + "\n", heading) { UnscaledMarginTop = HeadingTopMargin });
                Tag(title, title.Count - 1, titleOrd);
            }

            // Unrevealed section: the title stays (it advertises that there is
            // something to earn), the content gives way to a faint hint line.
            // The knowledge is the reward; the page fills in once it is earned.
            if (!string.IsNullOrEmpty(sec.RevealedBy)
                && revealHost != null && !revealHost.IsRevealed(sec.RevealedBy!))
            {
                string hintText = LensPlain(GuideLibrary.Localize(sec.Hint)
                    ?? Lang.Get("almanacilluminated:guide-unrevealed"), out int hintOrd);
                var hintFont = italic.Clone().WithColor(new[] { 0.38, 0.32, 0.25, 1.0 });
                var hintAtom = new Atom { PageBreakBefore = sec.PageBreakBefore };
                hintAtom.Comps.AddRange(title);
                hintAtom.Comps.Add(new RichTextComponent(capi, hintText + "\n", hintFont));
                Tag(hintAtom.Comps, hintAtom.Comps.Count - 1, hintOrd);
                atoms.Add(hintAtom);
                if (si < pack.Sections.Count - 1)
                    hintAtom.Comps.Add(new ClearFloatTextComponent(capi, SectionGap));
                continue;
            }

            var blockLists = new List<List<RichTextComponentBase>>();
            foreach (var block in sec.Blocks)
            {
                if (!Gated(capi, block)) continue;
                var bl = new List<RichTextComponentBase>();
                RenderBlock(capi, block, bl, body, italic, dropCap, library, onLink, initial, accent, earned);
                if (bl.Count > 0) blockLists.Add(bl);
            }

            int before = atoms.Count;
            if (sec.KeepTogether)
            {
                var a = new Atom { PageBreakBefore = sec.PageBreakBefore, KeepTogether = true };
                a.Comps.AddRange(title);
                foreach (var bl in blockLists) a.Comps.AddRange(bl);
                if (a.Comps.Count > 0) atoms.Add(a);
            }
            else
            {
                var first = new Atom { PageBreakBefore = sec.PageBreakBefore };
                first.Comps.AddRange(title);
                if (blockLists.Count > 0) first.Comps.AddRange(blockLists[0]);
                if (first.Comps.Count > 0) atoms.Add(first);

                for (int i = 1; i < blockLists.Count; i++)
                {
                    var a = new Atom();
                    a.Comps.AddRange(blockLists[i]);
                    atoms.Add(a);
                }
            }

            // Blank-line break after every section but the last. Goes on the section's
            // final atom so it sits at the bottom of a page cleanly (and never adds
            // top padding when the next section opens a page).
            if (atoms.Count > before && si < pack.Sections.Count - 1)
                atoms[atoms.Count - 1].Comps.Add(new ClearFloatTextComponent(capi, SectionGap));
        }
        return atoms;
    }

    /// <summary>
    /// Packs atoms into page-height columns at the page width. A small atom moves
    /// whole to the next page when it does not fit. A splittable atom taller than
    /// the space left (a long contents list, a long step list) flows across pages
    /// by its own components, so nothing ever runs off the bottom. keepTogether
    /// atoms stay whole, and only split as a last resort when bigger than a page.
    /// </summary>
    private static List<RichTextComponentBase[]> Paginate(ICoreClientAPI capi, List<Atom> atoms, double pageWidth, double pageHeight)
    {
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double availH = pageHeight * scale;   // measured heights come back in scaled pixels

        var pages = new List<RichTextComponentBase[]>();
        var current = new List<RichTextComponentBase>();

        void Flush()
        {
            if (current.Count > 0) { pages.Add(current.ToArray()); current = new List<RichTextComponentBase>(); }
        }

        bool Fits(List<RichTextComponentBase> list)
            => MeasureHeight(capi, list.ToArray(), pageWidth) <= availH;

        // Flow components onto the current page, breaking to a new page whenever
        // the next one would overflow. A single component taller than a page is
        // left where it lands rather than looping forever.
        void PackByComponent(IReadOnlyList<RichTextComponentBase> comps)
        {
            foreach (var c in comps)
            {
                current.Add(c);
                if (current.Count > 1 && !Fits(current))
                {
                    current.RemoveAt(current.Count - 1);

                    // A page break must never fall between a component and the one it
                    // introduces. A step number is drawn as its own component ahead of
                    // the item's prose, so without this the paginator would happily end
                    // a page on a bare numeral and open the next one with the sentence
                    // it belongs to. Seen in play: a lone circled 3 at the foot of a
                    // page, and a numbered item whose text began on the far leaf.
                    //
                    // Carry any trailing binders over to the next page with the
                    // component they bind to. If that empties the page, Flush is a no-op
                    // and the pair simply starts where it already was, which is correct.
                    var carry = new List<RichTextComponentBase>();
                    while (current.Count > 0 && BindsToNext(current[current.Count - 1]))
                    {
                        carry.Insert(0, current[current.Count - 1]);
                        current.RemoveAt(current.Count - 1);
                    }

                    Flush();
                    current.AddRange(carry);
                    current.Add(c);
                }
            }
        }

        foreach (var atom in atoms)
        {
            if (atom.PageBreakBefore) Flush();

            var trial = new List<RichTextComponentBase>(current);
            trial.AddRange(atom.Comps);
            if (Fits(trial)) { current = trial; continue; }   // whole atom fits as-is

            if (atom.KeepTogether)
            {
                Flush();
                if (Fits(atom.Comps))
                    current.AddRange(atom.Comps);
                else
                    PackByComponent(atom.Comps);   // taller than a whole page: split rather than clip
            }
            else
            {
                PackByComponent(atom.Comps);        // fill the rest of this page, then flow over
            }
        }

        Flush();
        if (pages.Count == 0) pages.Add(System.Array.Empty<RichTextComponentBase>());
        return pages;
    }

    /// <summary>
    /// True for a component that introduces the one after it and must never be left
    /// stranded at the foot of a page without it. Step numbers are the only case
    /// today; anything drawn as a separate leading glyph belongs here too.
    /// </summary>
    private static bool BindsToNext(RichTextComponentBase c) => c is StepNumberComponent;

    /// <summary>
    /// Lays a component run out at the page width and returns its height in scaled
    /// pixels. The throwaway richtext is deliberately not disposed: Dispose would
    /// dispose the shared child components, and figure and recipe hold native
    /// surfaces the real render still needs.
    /// </summary>
    public static double MeasureHeight(ICoreClientAPI capi, RichTextComponentBase[] comps, double pageWidth)
    {
        if (comps.Length == 0) return 0;
        var bounds = ElementBounds.Fixed(0, 0, pageWidth, 1_000_000).WithEmptyParent();
        var rt = new GuiElementRichtext(capi, comps, bounds);
        rt.BeforeCalcBounds();
        return rt.TotalHeight;
    }

    /// <summary>The author's first initial, for the wax seal. From the byline ("... by Venah" -> "V"), else the title.</summary>
    private static string DeriveInitial(string? byline, string? title)
    {
        string src = byline ?? title ?? "";
        int by = src.LastIndexOf("by ", StringComparison.OrdinalIgnoreCase);
        if (by >= 0) src = src.Substring(by + 3);
        foreach (char c in src.Trim())
            if (char.IsLetter(c)) return char.ToUpperInvariant(c).ToString();
        return "A";
    }

    /// <summary>
    /// Every reveal key a chapter hangs on: its own `revealedBy` plus each section's.
    /// Distinct, so a key three sections share is asked about once. This is the set
    /// `whenChapterComplete` measures the chapter against.
    /// </summary>
    /// <summary>
    /// Every `doneWhen` key the chapter's quest items hang on. Distinct. These decide
    /// checkbox state, so a cache that does not stamp them serves a stale tick after a
    /// live earn: the exact bug reported on 2026-08-08 against is-roast-collected.
    /// Separate from <see cref="ChapterRevealKeys"/> because the two are probed
    /// differently, reveals degrading open and completion degrading closed.
    /// </summary>
    public static IEnumerable<string> ChapterDoneWhenKeys(GuidePack pack)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sec in pack.Sections)
            foreach (var block in sec.Blocks)
            {
                if (!string.Equals(block.Type, "quest", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var it in Arr(block, "items"))
                {
                    if (it is not JObject item) continue;
                    string? key = item["doneWhen"]?.ToString();
                    if (!string.IsNullOrEmpty(key)) keys.Add(key!);
                }
            }
        return keys;
    }

    private static IEnumerable<string> ChapterRevealKeys(GuidePack pack)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(pack.RevealedBy)) keys.Add(pack.RevealedBy!);
        foreach (var sec in pack.Sections)
            if (!string.IsNullOrEmpty(sec.RevealedBy)) keys.Add(sec.RevealedBy!);
        return keys;
    }

    private static bool Gated(ICoreClientAPI capi, GuideBlock block)
    {
        if (block.Requires == null) return true;
        foreach (var modid in block.Requires)
            if (!capi.ModLoader.IsModEnabled(modid)) return false;
        return true;
    }

    private static void RenderBlock(ICoreClientAPI capi, GuideBlock block, List<RichTextComponentBase> comps,
        CairoFont body, CairoFont italic, CairoFont dropCap, GuideLibrary library, Action<LinkTextComponent>? onLink, string authorInitial, double[] accent,
        System.Func<string, bool> earned)
    {
        switch (block.Type)
        {
            case "contents":
            {
                bool all = (block.Str("include") ?? "added") == "all";
                var grey = body.Clone().WithColor(new[] { 0.45, 0.40, 0.33, 1.0 });
                foreach (var e in library.ModEntries(addedOnly: !all))
                {
                    // Link entries parse markup (rich filter); the greyed
                    // not-yet-added entries render plain (plain filter).
                    int entryStart = comps.Count;
                    int entryOrd;
                    if (e.Chapter != null)
                    {
                        // Plain filter even though this parses markup: the name sits
                        // INSIDE an anchor, and a font span nested in <a> is the one
                        // VTML nesting not proven safe. Holes carry no markup.
                        string nm = LensPlain(e.Name, out entryOrd);
                        comps.AddRange(VtmlUtil.Richtextify(capi, $"  <a href=\"almanac://chapter/{e.Chapter.Id}\">{nm}</a>\n", body, onLink));
                    }
                    else
                    {
                        string nm = LensPlain(e.Name, out entryOrd);
                        comps.Add(new RichTextComponent(capi, "  " + nm + "\n", grey));
                    }
                    Tag(comps, entryStart, entryOrd);
                }
                break;
            }

            case "heading":
            {
                string ht = LensPlain(SmallCaps(block.Str("text") ?? ""), out int headOrd);
                comps.Add(new RichTextComponent(capi, ht + "\n",
                    CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative).WithFontSize(21f)
                        .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink))
                    { UnscaledMarginTop = HeadingTopMargin });
                Tag(comps, comps.Count - 1, headOrd);
                break;
            }

            case "paragraph":
            {
                string text = block.Str("text") ?? "";
                if (block.Bool("dropcap") && text.Length > 0)
                {
                    // The drop cap's single letter stays clean: one giant capital
                    // wearing a mark reads as a render bug, not foxing.
                    comps.Add(new RichTextComponent(capi, text.Substring(0, 1), dropCap) { Float = EnumFloat.Left, PaddingRight = 4 });
                    text = text.Substring(1);
                }
                text = Lens(text, out int paraOrd);
                int paraStart = comps.Count;
                comps.AddRange(VtmlUtil.Richtextify(capi, text + "\n", body, onLink));
                Tag(comps, paraStart, paraOrd);
                break;
            }

            case "dropcap":
                comps.Add(new RichTextComponent(capi, block.Str("letter") ?? "", dropCap) { Float = EnumFloat.Left, PaddingRight = 4 });
                break;

            case "steps":
            {
                bool ordered = !block.Props.TryGetValue("ordered", out var o) || o.Type != JTokenType.Boolean || o.Value<bool>();
                int n = 1;
                foreach (var it in Arr(block, "items"))
                {
                    // The numeral/bullet glyph stays clean; the step's prose foxes.
                    if (ordered)
                        comps.Add(new StepNumberComponent(capi, n, accent));
                    else
                        comps.Add(new RichTextComponent(capi, "  • ", body));
                    string st = Lens(it.ToString(), out int stepOrd);
                    int stepStart = comps.Count;
                    comps.AddRange(VtmlUtil.Richtextify(capi, st + "\n", body, onLink));
                    Tag(comps, stepStart, stepOrd);
                    n++;
                }
                break;
            }

            case "materials":
            {
                foreach (var it in Arr(block, "items"))
                {
                    var stack = ResolveStack(capi, it["code"]?.ToString());
                    if (stack != null)
                        comps.Add(new ItemstackTextComponent(capi, stack, 40.0, 4.0, EnumFloat.Inline));
                }
                comps.Add(new ClearFloatTextComponent(capi, 6));
                break;
            }

            case "callout":
            {
                string variant = block.Str("variant") ?? "author";
                // ClearFloat spacers, not bare "\n": they reserve real measured
                // height, so the box's Pad overhang is accounted for by the
                // paginator AND cleared from the next block (a lone newline broke
                // the line but reserved almost no height, so the box's bottom
                // collided with the following heading).
                comps.Add(new ClearFloatTextComponent(capi, (float)CalloutComponent.OuterMargin));
                string calloutText = LensPlain(block.Str("text") ?? "", out int calloutOrd);
                if (variant == "author")
                {
                    comps.Add(new AuthorCalloutComponent(capi, calloutText, italic, authorInitial));
                }
                else
                {
                    // No trailing newline: it would add a phantom empty line inside the
                    // box, padding the bottom a full line more than the top. The box's
                    // own Pad supplies even breathing room on every side.
                    var (fill, line) = CalloutColors(variant);
                    comps.Add(new CalloutComponent(capi, calloutText, italic, fill, line));
                }
                Tag(comps, comps.Count - 1, calloutOrd);
                comps.Add(new ClearFloatTextComponent(capi, (float)CalloutComponent.OuterMargin));
                break;
            }

            case "quest":
            {
                // Completed objectives recede (muted ink), open ones read at
                // full strength — the mockup's checked-off voice.
                var doneFont = italic.Clone().WithColor(new[] { 0.42, 0.36, 0.29, 1.0 });
                foreach (var it in Arr(block, "items"))
                {
                    bool done = it["done"]?.Type == JTokenType.Boolean && it["done"]!.Value<bool>();

                    // `doneWhen` ticks the box off live, from the same providers the
                    // reveals read. Strict, unlike a reveal: an item nobody can report
                    // on stays OPEN. A checklist that ticks itself for a mod that is not
                    // even loaded tells the reader they have done something they have not.
                    if (!done)
                    {
                        string? doneWhen = it["doneWhen"]?.ToString();
                        if (!string.IsNullOrEmpty(doneWhen)) done = earned(doneWhen!);
                    }
                    // NOT the Unicode ballot boxes. The body face is Lora, and Lora has no
                    // glyph for U+2610, U+2611 or U+2713: all three come out as .notdef,
                    // byte-identical to a private-use codepoint (checked against
                    // Lora-Italic.ttf, 2026-08-09). So "checked" and "unchecked" drew the
                    // SAME hollow box and the only difference left was the ink shade, which
                    // is what the reader reported not being able to see. Brackets and U+00D7
                    // are in the font, so the mark is now unmistakable on its own.
                    // The checkbox mark stays clean (it is state, not prose); the
                    // objective's text foxes with the rest of the page.
                    string questText = LensPlain(it["text"]?.ToString() ?? "", out int questOrd);
                    comps.Add(new RichTextComponent(capi,
                        "  " + (done ? "[×] " : "[ ] ") + questText + "\n",
                        done ? doneFont : italic));
                    Tag(comps, comps.Count - 1, questOrd);
                }
                break;
            }

            case "ledger":
                // Richtextify, not a bare RichTextComponent: the latter takes plain text,
                // so inline VTML printed literally and readers got "<strong>Stranger.</strong>"
                // on the page. Every other text block already goes through this path; ledger
                // was the one that did not, and nothing in the pack used it until now, so the
                // fault sat unseen since it was written.
                //
                // Body font, not italic. Italic is this book's ASIDE voice, carried by every
                // callout, and a ledger holds core content: the rank ladders a reader is meant
                // to learn, not commentary on them. Setting them in the aside face told the
                // eye to skim exactly the lines that matter most.
                foreach (var it in Arr(block, "entries"))
                {
                    string entry = Lens(it.ToString(), out int ledgerOrd);
                    int ledgerStart = comps.Count;
                    comps.AddRange(VtmlUtil.Richtextify(capi, "  " + entry + "\n", body, onLink));
                    Tag(comps, ledgerStart, ledgerOrd);
                }
                break;

            case "divider":
                comps.Add(new DividerComponent(capi));
                comps.Add(new ClearFloatTextComponent(capi, 4));
                break;

            case "link":
            {
                string to = block.Str("to") ?? "";
                // Plain filter: display text lands inside the anchor (see contents).
                string text = LensPlain(block.Str("text") ?? to, out int linkOrd);
                var parts = VtmlUtil.Richtextify(capi, $"<a href=\"{to}\">{text}</a>\n", body, onLink);
                if (parts.Length > 0) parts[0].UnscaledMarginTop = 8;   // clear the block above (often a callout)
                int linkStart = comps.Count;
                comps.AddRange(parts);
                Tag(comps, linkStart, linkOrd);
                break;
            }

            case "recipe":
            {
                comps.Add(new RichTextComponent(capi, "\n", body));
                var rc = BuildRecipe(capi, block);
                comps.Add(rc ?? new RichTextComponent(capi, "(recipe unavailable)\n", italic));
                comps.Add(new RichTextComponent(capi, "\n", body));
                break;
            }

            case "figure":
            {
                comps.Add(new RichTextComponent(capi, "\n", body));
                comps.Add(new FigureComponent(capi, block.Str("image") ?? "", block.Str("align")));
                string? caption = block.Str("caption");
                if (!string.IsNullOrEmpty(caption))
                {
                    string ct = LensPlain(caption!, out int capOrd);
                    var capFont = italic.Clone().WithOrientation(EnumTextOrientation.Center);
                    comps.Add(new RichTextComponent(capi, "\n" + ct + "\n", capFont));
                    Tag(comps, comps.Count - 1, capOrd);
                }
                else
                {
                    comps.Add(new RichTextComponent(capi, "\n", body));
                }
                break;
            }

            case "table":
                IlluminatedLogger.Warn(capi, "renderer", "table block is deferred to v0.2, skipped");
                break;

            default:
                IlluminatedLogger.Warn(capi, "renderer", $"Unknown block type '{block.Type}', skipped");
                break;
        }
    }

    /// <summary>
    /// Builds the handbook's own grid-recipe component for a recipe block.
    /// `recipe` matches one recipe by its name; `output` matches every recipe that
    /// produces the given item, the way the handbook does. Returns null (and warns)
    /// when nothing matches or the native component cannot resolve its ingredients.
    /// </summary>
    private static RichTextComponentBase? BuildRecipe(ICoreClientAPI capi, GuideBlock block)
    {
        string? recipeCode = block.Str("recipe");
        string? outputCode = block.Str("output");
        var found = new List<GridRecipe>();

        if (!string.IsNullOrEmpty(recipeCode))
        {
            var loc = new AssetLocation(recipeCode);
            foreach (var gr in capi.World.GridRecipes)
                if (gr.Name != null && gr.Name.Equals(loc)) found.Add(gr);
        }
        else if (!string.IsNullOrEmpty(outputCode))
        {
            var loc = new AssetLocation(outputCode);
            foreach (var gr in capi.World.GridRecipes)
            {
                var os = gr.Output?.ResolvedItemStack;
                if (os?.Collectible?.Code != null && os.Collectible.Code.Equals(loc)) found.Add(gr);
            }
        }

        if (found.Count == 0)
        {
            IlluminatedLogger.Warn(capi, "renderer", $"recipe block: no grid recipe matched '{recipeCode ?? outputCode ?? "(none)"}'");
            return null;
        }

        try
        {
            // onStackClicked: handbook-style stack navigation lands with the IA work (#6).
            return new SlideshowGridRecipeTextComponent(capi, found.ToArray(), 40, EnumFloat.None, _ => { });
        }
        catch (Exception e)
        {
            IlluminatedLogger.Warn(capi, "renderer", $"recipe block could not compose '{recipeCode ?? outputCode}': {e.Message}");
            return null;
        }
    }

    /// <summary>Interior fill and border color per callout variant. Variant owns the color, not the chapter accent.</summary>
    private static (double[] fill, double[] border) CalloutColors(string variant) => variant switch
    {
        "tip"     => (new[] { 0.91, 0.96, 0.89, 1.0 }, new[] { 0.15, 0.45, 0.20, 1.0 }),
        "warning" => (new[] { 0.97, 0.93, 0.82, 1.0 }, new[] { 0.65, 0.40, 0.05, 1.0 }),
        "lore"    => (new[] { 0.91, 0.90, 0.97, 1.0 }, new[] { 0.25, 0.20, 0.55, 1.0 }),
        _         => (new[] { 0.97, 0.94, 0.85, 1.0 }, new[] { 0.72, 0.13, 0.08, 1.0 }),
    };

    private static JArray Arr(GuideBlock block, string key)
        => block.Props.TryGetValue(key, out var t) && t is JArray a ? a : new JArray();

    private static ItemStack? ResolveStack(ICoreClientAPI capi, string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var loc = new AssetLocation(code);
        var item = capi.World.GetItem(loc);
        if (item != null) return new ItemStack(item);
        var blk = capi.World.GetBlock(loc);
        return blk != null ? new ItemStack(blk) : null;
    }
}
