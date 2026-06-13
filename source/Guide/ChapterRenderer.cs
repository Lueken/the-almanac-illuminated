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

        var sections = new List<RenderedSection>();
        foreach (var sec in pack.Sections)
        {
            var comps = new List<RichTextComponentBase>();
            if (!string.IsNullOrEmpty(sec.Title))
                comps.Add(new RichTextComponent(capi, sec.Title + "\n", heading));

            foreach (var block in sec.Blocks)
            {
                if (!Gated(capi, block)) continue;
                RenderBlock(capi, block, comps, body, italic, dropCap, onLink);
            }

            sections.Add(new RenderedSection(sec.Title ?? "", comps.ToArray()));
        }
        return sections;
    }

    private static bool Gated(ICoreClientAPI capi, GuideBlock block)
    {
        if (block.Requires == null) return true;
        foreach (var modid in block.Requires)
            if (!capi.ModLoader.IsModEnabled(modid)) return false;
        return true;
    }

    private static void RenderBlock(ICoreClientAPI capi, GuideBlock block, List<RichTextComponentBase> comps,
        CairoFont body, CairoFont italic, CairoFont dropCap, Action<LinkTextComponent>? onLink)
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
                var (box, bar) = CalloutColors(block.Str("variant") ?? "author");
                comps.Add(new RichTextComponent(capi, "\n", body));
                comps.Add(new CalloutComponent(capi, (block.Str("text") ?? "") + "\n", italic, box, bar));
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
                comps.Add(new RichTextComponent(capi, $"[recipe: {block.Str("recipe") ?? block.Str("output") ?? "?"}]\n", italic));
                break;

            case "figure":
                comps.Add(new RichTextComponent(capi, $"[figure: {block.Str("image") ?? "?"}]\n", italic));
                break;

            case "table":
                IlluminatedLogger.Warn(capi, "renderer", "table block is deferred to v0.2, skipped");
                break;

            default:
                IlluminatedLogger.Warn(capi, "renderer", $"Unknown block type '{block.Type}', skipped");
                break;
        }
    }

    /// <summary>Box fill and left-bar colors per callout variant. Variant owns the color, not the chapter accent.</summary>
    private static (double[] box, double[] bar) CalloutColors(string variant) => variant switch
    {
        "tip"     => (new[] { 0.80, 0.86, 0.70, 1.0 }, new[] { 0.34, 0.50, 0.20, 1.0 }),
        "warning" => (new[] { 0.92, 0.80, 0.72, 1.0 }, new[] { 0.70, 0.28, 0.14, 1.0 }),
        "lore"    => (new[] { 0.80, 0.82, 0.90, 1.0 }, new[] { 0.30, 0.34, 0.55, 1.0 }),
        _         => (new[] { 0.87, 0.79, 0.61, 1.0 }, new[] { 0.48, 0.36, 0.18, 1.0 }), // author
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
