using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// Phase 0 spike content: a deliberately heavy mock chapter. This is NOT the
/// guide-pack format — it exists to stress native richtext composition with
/// the load shape a real chapter will have: headings, paragraphs, inline
/// itemstacks, and checklist lines. If composition of a page stays cheap
/// (&lt;~30 ms) and interaction stays crisp, native GUI wins Phase 0.
/// </summary>
public static class MockChapter
{
    /// <summary>One "section" of guide content — the unit a page is filled with.</summary>
    public record Section(string Title, RichTextComponentBase[] Components);

    public static List<Section> Generate(ICoreClientAPI capi, int sectionCount = 24)
    {
        // Grab a pool of real resolved itemstacks so the page renders genuine
        // 3D item slots — the expensive part we are here to measure.
        var stackPool = new List<ItemStack>();
        foreach (var item in capi.World.Items)
        {
            if (item?.Code == null || item.IsMissing) continue;
            stackPool.Add(new ItemStack(item));
            if (stackPool.Count >= 120) break;
        }
        IlluminatedLogger.Debug(capi, "mockchapter", $"Resolved {stackPool.Count} itemstacks for the spike pool");

        var headingFont = CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold);
        var bodyFont = CairoFont.WhiteSmallText();
        var checklistFont = CairoFont.WhiteSmallText().WithSlant(Cairo.FontSlant.Italic);

        string[] loremTopics =
        {
            "Harvesting animals works differently", "Crafting tools by hand", "Thirst and clean water",
            "Trees fall when felled", "Reading the stone beneath you", "Preserving the autumn glut",
            "The temporal weather and you", "Clothing against the cold", "First nights: fire and shelter",
            "Foraging without poisoning yourself", "Ores worth chasing early", "The long road to metal",
        };

        var sections = new List<Section>();
        for (int s = 0; s < sectionCount; s++)
        {
            var comps = new List<RichTextComponentBase>
            {
                new RichTextComponent(capi, loremTopics[s % loremTopics.Length] + "\n", headingFont),
                new RichTextComponent(capi,
                    "Your vanilla habit no longer works here. The pack replaces this mechanic wholesale, " +
                    "and the first time you collide with it you will assume something is broken. It is not. " +
                    "Follow the steps below and the new flow becomes second nature within a session.\n", bodyFont),
            };

            // A row of real itemstacks — clickable 3D slots inline in the text flow
            int stackRowLen = Math.Min(6, stackPool.Count);
            for (int i = 0; i < stackRowLen; i++)
            {
                var stack = stackPool[(s * stackRowLen + i) % stackPool.Count];
                comps.Add(new ItemstackTextComponent(capi, stack, 40.0, 6.0, EnumFloat.Inline,
                    cs => IlluminatedLogger.Debug(capi, "page", $"Itemstack clicked: {cs?.Collectible?.Code}")));
            }
            comps.Add(new ClearFloatTextComponent(capi, 6));

            comps.Add(new RichTextComponent(capi,
                "Work the material at the appropriate station, mind your tool's condition, and keep an eye " +
                "on spoilage timers — the new systems are generous to the prepared and merciless to the hasty.\n", bodyFont));

            // Mock quest checklist (static text in the spike; PF-backed live state in Phase 4)
            comps.Add(new RichTextComponent(capi, "  ☐ Locate the required station\n", checklistFont));
            comps.Add(new RichTextComponent(capi, "  ☐ Gather the listed materials\n", checklistFont));
            comps.Add(new RichTextComponent(capi, "  ☑ Open The Almanac (you are doing this now)\n", checklistFont));
            comps.Add(new ClearFloatTextComponent(capi, 10));

            sections.Add(new Section(loremTopics[s % loremTopics.Length], comps.ToArray()));
        }

        return sections;
    }
}
