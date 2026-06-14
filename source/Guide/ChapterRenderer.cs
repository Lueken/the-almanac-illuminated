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

    /// <summary>
    /// Renders a chapter into book pages. Every block is laid out and measured at
    /// the page width, then blocks are packed into page-height columns. A section
    /// with keepTogether stays whole; pageBreakBefore forces a fresh page; a title
    /// never orphans from its first block. Returns one component array per page.
    /// </summary>
    public static List<RichTextComponentBase[]> RenderPages(ICoreClientAPI capi, GuidePack pack, GuideLibrary library, Action<LinkTextComponent>? onLink, double pageWidth, double pageHeight)
    {
        var atoms = BuildAtoms(capi, pack, library, onLink);
        return Paginate(capi, atoms, pageWidth, pageHeight);
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
        var heading = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);
        var body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink);
        var italic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithSlant(Cairo.FontSlant.Italic).WithColor(Ink);
        var dropCap = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative).WithFontSize(34f).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);

        string initial = DeriveInitial(pack.Byline, pack.Title);
        var atoms = new List<Atom>();

        foreach (var sec in pack.Sections)
        {
            var title = new List<RichTextComponentBase>();
            string? secTitle = GuideLibrary.Localize(sec.Title);
            if (!string.IsNullOrEmpty(secTitle))
                title.Add(new RichTextComponent(capi, secTitle + "\n", heading));

            var blockLists = new List<List<RichTextComponentBase>>();
            foreach (var block in sec.Blocks)
            {
                if (!Gated(capi, block)) continue;
                var bl = new List<RichTextComponentBase>();
                RenderBlock(capi, block, bl, body, italic, dropCap, library, onLink, initial);
                if (bl.Count > 0) blockLists.Add(bl);
            }

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
                    Flush();
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
    /// Lays a component run out at the page width and returns its height in scaled
    /// pixels. The throwaway richtext is deliberately not disposed: Dispose would
    /// dispose the shared child components, and figure and recipe hold native
    /// surfaces the real render still needs.
    /// </summary>
    private static double MeasureHeight(ICoreClientAPI capi, RichTextComponentBase[] comps, double pageWidth)
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

    private static bool Gated(ICoreClientAPI capi, GuideBlock block)
    {
        if (block.Requires == null) return true;
        foreach (var modid in block.Requires)
            if (!capi.ModLoader.IsModEnabled(modid)) return false;
        return true;
    }

    private static void RenderBlock(ICoreClientAPI capi, GuideBlock block, List<RichTextComponentBase> comps,
        CairoFont body, CairoFont italic, CairoFont dropCap, GuideLibrary library, Action<LinkTextComponent>? onLink, string authorInitial)
    {
        switch (block.Type)
        {
            case "contents":
            {
                bool all = (block.Str("include") ?? "added") == "all";
                var grey = body.Clone().WithColor(new[] { 0.45, 0.40, 0.33, 1.0 });
                foreach (var e in library.ModEntries(addedOnly: !all))
                {
                    if (e.Chapter != null)
                        comps.AddRange(VtmlUtil.Richtextify(capi, $"  <a href=\"almanac://chapter/{e.Chapter.Id}\">{e.Name}</a>\n", body, onLink));
                    else
                        comps.Add(new RichTextComponent(capi, "  " + e.Name + "\n", grey));
                }
                break;
            }

            case "heading":
                comps.Add(new RichTextComponent(capi, (block.Str("text") ?? "") + "\n",
                    CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink)));
                break;

            case "paragraph":
            {
                string text = block.Str("text") ?? "";
                if (block.Bool("dropcap") && text.Length > 0)
                {
                    comps.Add(new RichTextComponent(capi, text.Substring(0, 1), dropCap) { Float = EnumFloat.Left, PaddingRight = 4 });
                    text = text.Substring(1);
                }
                comps.AddRange(VtmlUtil.Richtextify(capi, text + "\n", body, onLink));
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
                    comps.Add(new RichTextComponent(capi, ordered ? $"  {n}. " : "  • ", body));
                    comps.AddRange(VtmlUtil.Richtextify(capi, it.ToString() + "\n", body, onLink));
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
                comps.Add(new RichTextComponent(capi, "\n", body));
                if (variant == "author")
                {
                    comps.Add(new AuthorCalloutComponent(capi, block.Str("text") ?? "", italic, authorInitial));
                }
                else
                {
                    var (fill, line) = CalloutColors(variant);
                    comps.Add(new CalloutComponent(capi, (block.Str("text") ?? "") + "\n", italic, fill, line));
                }
                comps.Add(new RichTextComponent(capi, "\n", body));
                break;
            }

            case "quest":
                foreach (var it in Arr(block, "items"))
                {
                    bool done = it["done"]?.Type == JTokenType.Boolean && it["done"]!.Value<bool>();
                    comps.Add(new RichTextComponent(capi, "  " + (done ? "☑ " : "☐ ") + (it["text"]?.ToString() ?? "") + "\n", italic));
                }
                break;

            case "ledger":
                foreach (var it in Arr(block, "entries"))
                    comps.Add(new RichTextComponent(capi, "  " + it.ToString() + "\n", italic));
                break;

            case "divider":
                comps.Add(new RichTextComponent(capi, "⸻⸻⸻⸻\n", body));
                break;

            case "link":
            {
                string to = block.Str("to") ?? "";
                string text = block.Str("text") ?? to;
                comps.AddRange(VtmlUtil.Richtextify(capi, $"<a href=\"{to}\">{text}</a>\n", body, onLink));
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
                    var capFont = italic.Clone().WithOrientation(EnumTextOrientation.Center);
                    comps.Add(new RichTextComponent(capi, "\n" + caption + "\n", capFont));
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
