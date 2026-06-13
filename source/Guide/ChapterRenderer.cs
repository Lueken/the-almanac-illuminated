using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

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

    public static List<RenderedSection> Render(ICoreClientAPI capi, GuidePack pack, Action<LinkTextComponent>? onLink)
    {
        var heading = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);
        var body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink);
        var italic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithSlant(Cairo.FontSlant.Italic).WithColor(Ink);
        var dropCap = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative).WithFontSize(34f).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);

        string initial = DeriveInitial(pack.Byline, pack.Title);

        var sections = new List<RenderedSection>();
        foreach (var sec in pack.Sections)
        {
            var comps = new List<RichTextComponentBase>();
            if (!string.IsNullOrEmpty(sec.Title))
                comps.Add(new RichTextComponent(capi, sec.Title + "\n", heading));

            foreach (var block in sec.Blocks)
            {
                if (!Gated(capi, block)) continue;
                RenderBlock(capi, block, comps, body, italic, dropCap, onLink, initial);
            }

            sections.Add(new RenderedSection(sec.Title ?? "", comps.ToArray()));
        }
        return sections;
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
        CairoFont body, CairoFont italic, CairoFont dropCap, Action<LinkTextComponent>? onLink, string authorInitial)
    {
        switch (block.Type)
        {
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
