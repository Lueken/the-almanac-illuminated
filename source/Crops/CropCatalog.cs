using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacIlluminated;

public enum CropKind { SeedCrop, Mushroom, BerryBush, FruitTree }

/// <summary>
/// One growable food source the Crops tab can list: the produce it yields, where it
/// came from (vanilla or a mod), and its growing data. Climate-derived planting
/// windows (phase 2) and Foragers Gamble gating (phase 4) layer on top of this.
/// </summary>
public sealed class CropEntry
{
    public CropKind Kind;
    public string ProduceCode = "";     // produce collectible code: identity and the FG knowledge key
    public string DisplayName = "";
    public string SourceDomain = "game";
    public ItemStack? Produce;          // for the icon and the masked silhouette

    /// <summary>The ripe plant block this entry was built from, kept so a familiarity
    /// source can map the entry back to its own crop id by block code (the produce code
    /// cannot do that job: breeding varietals give one species several produce items).
    /// Null for fruit trees, which come from TypeProps rather than a block.</summary>
    public Block? PlantBlock;

    // Seed-crop growing data, straight off the crop block's CropProps.
    public EnumSoilNutrient Nutrient;
    public float ColdDamageBelow;
    public float HeatDamageAbove;
    public double TotalGrowthDays;
    public int GrowthStages;
    public bool MultipleHarvests;

    /// <summary>
    /// True when the produce declares the BASE GAME's domain. That is not the same as being
    /// vanilla content and must never be captioned as such: a mod is free to ship its
    /// blocktypes under assets/game/ and register straight into the base domain, which is
    /// what Mycodiversity does for all 36 of its mushrooms. Read this as "carries no domain
    /// of its own", which is all the block registry actually knows.
    /// </summary>
    public bool Vanilla => SourceDomain is "game" or "survival";
}

/// <summary>
/// Discovers every growable food source from the loaded blocks, mod-agnostic: a crop
/// is any block carrying CropProps, keyed by type and taken at its final growth stage.
/// Source is the block's own domain, so mod crops light up automatically and carry
/// their origin. Berry bushes and fruit trees follow (their props differ); this first
/// pass lands seed crops and logs what it found.
/// </summary>
public static class CropCatalog
{
    public static List<CropEntry> Build(ICoreClientAPI capi)
    {
        var entries = new List<CropEntry>();
        entries.AddRange(BuildSeedCrops(capi));
        entries.AddRange(BuildMushrooms(capi));
        entries.AddRange(BuildBerryBushes(capi));
        entries.AddRange(BuildFruitTrees(capi));

        // Dedupe on produce: a berry grown by both a small and large bush, or a crop
        // that also drops as something else, should appear once.
        var byProduce = new Dictionary<string, CropEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            if (!byProduce.ContainsKey(e.ProduceCode)) byProduce[e.ProduceCode] = e;

        var result = byProduce.Values.ToList();
        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Logs a one-line summary plus a per-source breakdown, for validating discovery
    /// against a pack. With a homebase position, also logs each crop's planting window
    /// computed from that base's climate.
    /// </summary>
    public static void LogSummary(ICoreClientAPI capi, List<CropEntry> entries, BlockPos? homebase = null)
    {
        var byKind = entries.GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}");
        // Grouped by the raw domain, never relabelled "vanilla". A mod registering into the
        // base domain lands in the "game" bucket, which is true, where "vanilla" would not be.
        var bySource = entries.GroupBy(e => e.SourceDomain)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}");
        string at = homebase != null ? $" — homebase climate at {homebase}" : "";
        IlluminatedLogger.Info(capi, "crops",
            $"Catalog: {entries.Count} growable(s) — by kind: {string.Join(", ", byKind)}; by source: {string.Join(", ", bySource)}{at}");
        foreach (var e in entries)
        {
            string cold = float.IsNaN(e.ColdDamageBelow) ? "—" : $"cold<{e.ColdDamageBelow:0.#}";
            string heat = float.IsNaN(e.HeatDamageAbove) ? "—" : $"heat>{e.HeatDamageAbove:0.#}";
            string nutrient = e.Kind == CropKind.SeedCrop ? e.Nutrient.ToString() : "—";
            string window = homebase != null ? "  |  " + PlantingWindow.Compute(capi, homebase, e).Summary() : "";
            IlluminatedLogger.Info(capi, "crops",
                $"  [{e.Kind}] {e.DisplayName} [{e.SourceDomain}] {e.ProduceCode} — {cold} {heat}, {e.TotalGrowthDays:0.#}d, {nutrient}{window}");
        }
    }

    private static List<CropEntry> BuildSeedCrops(ICoreClientAPI capi)
    {
        // One entry per species, built from whichever of that species' blocks can actually
        // name its produce.
        //
        // Preference order is unsized block, then highest stage, then code, but it is only a
        // PREFERENCE and the loop falls through when a block yields nothing. That fallthrough
        // is the whole point. Art of Growing adds a size variantgroup to vanilla's own crop
        // file and points its drops at `artofgrowing:vegetable-{size}-cabbage`, which leaves a
        // vestigial unsized block behind whose drop still carries the literal {size} and
        // cannot resolve. Preferring it and stopping there lost cabbage, turnip, onion,
        // parsnip and carrot from the catalogue entirely, and left them showing as one "Rot"
        // and one "Dry grass" card once dedupe had collapsed the byproducts they fell back to.
        var byType = new Dictionary<string, List<(Block block, int stage, bool sized)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in capi.World.Blocks)
        {
            if (block?.CropProps == null || block.Code == null) continue;
            if (!TryParseCrop(block.Code, out string typeKey, out int stage, out bool sized)) continue;

            if (!byType.TryGetValue(typeKey, out var list)) byType[typeKey] = list = new();
            list.Add((block, stage, sized));
        }

        var entries = new List<CropEntry>();
        foreach (var (typeKey, candidates) in byType)
        {
            candidates.Sort((a, b) =>
            {
                int s = a.sized.CompareTo(b.sized);                 // unsized first
                if (s != 0) return s;
                int st = b.stage.CompareTo(a.stage);                // then ripest
                if (st != 0) return st;
                return string.CompareOrdinal(a.block.Code.Path, b.block.Code.Path);   // then stable
            });

            string type = typeKey.Substring(typeKey.IndexOf(':') + 1);
            foreach (var (block, _, _) in candidates)
            {
                var produce = ResolveProduce(capi, block, type);
                if (produce?.Collectible?.Code == null) continue;   // this block cannot name it; try the next

                var cp = block.CropProps;
                entries.Add(new CropEntry
                {
                    Kind = CropKind.SeedCrop,
                    ProduceCode = produce.Collectible.Code.ToString(),
                    DisplayName = produce.GetName(),
                    SourceDomain = produce.Collectible.Code.Domain,   // the food's origin, so mod produce flags as modded
                    Produce = produce,
                    PlantBlock = block,
                    Nutrient = cp.RequiredNutrient,
                    ColdDamageBelow = cp.ColdDamageBelow,
                    HeatDamageAbove = cp.HeatDamageAbove,
                    TotalGrowthDays = cp.TotalGrowthMonths > 0 ? cp.TotalGrowthMonths * capi.World.Calendar.DaysPerMonth : cp.TotalGrowthDays,
                    GrowthStages = cp.GrowthStages,
                    MultipleHarvests = cp.MultipleHarvests,
                });
                break;
            }
        }
        return entries;
    }

    /// <summary>
    /// The Art of Growing breeding addon's size ladder. A varietal is not its own plant:
    /// wild carrots and gigantic carrots are carrots, and the catalogue lists the species
    /// once. Folded here so the tab reads right whether or not TCM is installed (TCM keeps
    /// its own copy of this ladder for familiarity ids; neither mod may depend on the other).
    /// </summary>
    private static readonly string[] VarietalSizes =
        { "wild", "small", "medium", "decent", "large", "hefty", "gigantic" };

    /// <summary>
    /// Crop blocks are coded "crop-{type}-{stage}" (the type itself may hold dashes).
    /// The breeding addon inserts a size ahead of the type — "crop-{size}-{type}-{stage}" —
    /// which is folded away, with <paramref name="sized"/> reporting whether it was.
    /// Only the game domain is folded, matching where the addon restages its crops, so a
    /// mod crop that happens to begin with one of those words keeps its own identity.
    /// </summary>
    private static bool TryParseCrop(AssetLocation code, out string typeKey, out int stage, out bool sized)
    {
        typeKey = ""; stage = 0; sized = false;
        string path = code.Path;
        if (!path.StartsWith("crop-", StringComparison.OrdinalIgnoreCase)) return false;

        int dash = path.LastIndexOf('-');
        if (dash <= 4 || !int.TryParse(path.Substring(dash + 1), out stage)) return false;

        string type = path.Substring(5, dash - 5);    // between "crop-" and the trailing "-{stage}"
        if (type.Length == 0) return false;

        // Second-year seed crops (crop-seed-{size}-onion) are the same plant grown on for
        // seed, not another plant. The species already has a card, and letting these in put
        // the onion's whole family under "Scallions", which is what a seed-onion drops.
        if (type.StartsWith("seed-", StringComparison.OrdinalIgnoreCase)) return false;

        if (code.Domain.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string size in VarietalSizes)
            {
                if (!type.StartsWith(size + "-", StringComparison.OrdinalIgnoreCase)) continue;
                type = type.Substring(size.Length + 1);
                sized = true;
                break;
            }
            if (type.Length == 0) return false;
        }

        typeKey = code.Domain + ":" + type;
        return true;
    }

    /// <summary>
    /// The food a plant drops: its configured drops minus the seed, the cutting, and
    /// the plant's own block (an unripe bush drops only itself, so it resolves to null
    /// and is skipped; a ripe bush drops the berry). Each candidate is canonicalised so
    /// the card names the end product rather than a step on the way to it.
    ///
    /// Where a crop drops several foods, the one that shares the crop's own name wins.
    /// A plant can shed a by-product before its crop: Art of Growing's onion lists
    /// scallions ahead of the onion itself, and first-food-wins filed the whole species
    /// under "Scallions". <paramref name="typeHint"/> is the crop type for seed crops and
    /// null elsewhere, where first food remains the right answer.
    /// </summary>
    private static ItemStack? ResolveProduce(ICoreClientAPI capi, Block block, string? typeHint = null)
    {
        if (block.Drops == null) return null;
        ItemStack? first = null;
        foreach (var drop in block.Drops)
        {
            var stack = drop?.ResolvedItemstack;
            var collCode = stack?.Collectible?.Code?.Path;
            if (stack == null || collCode == null) continue;
            if (IsNotProduce(collCode)) continue;

            var resolved = Canonicalise(capi, stack);
            first ??= resolved;
            if (typeHint == null) return first;
            if (resolved.Collectible.Code.Path.Contains(typeHint, StringComparison.OrdinalIgnoreCase))
                return resolved;
        }
        return first;
    }

    /// <summary>
    /// Trades a drop for the thing the player actually ends up with, when the drop is a
    /// step on the way rather than the food itself. Two cases, both from the Art of Growing
    /// family: a wet grain bundle is threshing stock, not grain, and a size-tagged vegetable
    /// is one varietal of a species the catalogue lists once. Every substitution is checked
    /// against the loaded item and block registries first, so an unrecognised shape falls
    /// through unchanged rather than blanking the card.
    /// </summary>
    private static ItemStack Canonicalise(ICoreClientAPI capi, ItemStack stack)
    {
        var code = stack.Collectible.Code;
        string path = code.Path;
        var tries = new List<string>();

        // artofgrowing:grainbundle-{type}-{size}-wet -> game:grain-{type}
        if (path.StartsWith("grainbundle-", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = path.Split('-');
            if (parts.Length >= 2 && parts[1].Length > 0) tries.Add("game:grain-" + parts[1]);
        }

        // A size segment anywhere in the path: try the same code without it, in its own
        // domain and in game's (artofgrowing:vegetable-large-cabbage -> game:vegetable-cabbage).
        string plain = StripSizeSegment(path);
        if (plain != path)
        {
            tries.Add(code.Domain + ":" + plain);
            tries.Add("game:" + plain);
        }

        // A bare wet suffix, for any bundle shape the first rule did not catch.
        if (path.EndsWith("-wet", StringComparison.OrdinalIgnoreCase))
            tries.Add(code.Domain + ":" + path.Substring(0, path.Length - 4));

        foreach (string candidate in tries)
        {
            var loc = new AssetLocation(candidate);
            var item = capi.World.GetItem(loc);
            if (item != null) return new ItemStack(item);
            var blk = capi.World.GetBlock(loc);
            if (blk != null && blk.Id != 0) return new ItemStack(blk);
        }
        return stack;
    }

    /// <summary>
    /// The litter a harvest leaves that is not the harvest. Seeds and cuttings are how the
    /// plant continues rather than what it gives, and rot, dry grass and thatch are what
    /// falls off it. Filtering these is what stops a crop whose real produce cannot resolve
    /// from being filed under "Rot": after dedupe on produce, five separate crops in that
    /// state collapsed into one "Rot" card and one "Dry grass" card, and vanished from
    /// their families.
    /// </summary>
    private static bool IsNotProduce(string path) =>
        path.StartsWith("seeds-", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("seed-", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("cutting", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("berrybush", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("rot", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("drygrass", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("dryhay", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("thatch", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("firewood", StringComparison.OrdinalIgnoreCase);

    /// <summary>Drops one varietal-size segment from a dash-separated path, wherever it sits.</summary>
    private static string StripSizeSegment(string path)
    {
        string[] parts = path.Split('-');
        if (parts.Length < 3) return path;   // never strip a two-part code down to one
        for (int i = 1; i < parts.Length; i++)
        {
            foreach (string size in VarietalSizes)
            {
                if (!parts[i].Equals(size, StringComparison.OrdinalIgnoreCase)) continue;
                var kept = new List<string>(parts);
                kept.RemoveAt(i);
                return string.Join("-", kept);
            }
        }
        return path;
    }

    /// <summary>
    /// Berry bushes: any block coded "berrybush" at its ripe state. Growing band comes
    /// from the block attributes the bush's growth check reads (stop-below/above temp);
    /// the berry is the ripe bush's food drop.
    /// </summary>
    private static List<CropEntry> BuildBerryBushes(ICoreClientAPI capi)
    {
        var entries = new List<CropEntry>();
        foreach (var block in capi.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (!block.Code.Path.Contains("berrybush", StringComparison.OrdinalIgnoreCase)) continue;
            if (block.Variant != null && block.Variant.TryGetValue("state", out var state)
                && !state.Equals("ripe", StringComparison.OrdinalIgnoreCase)) continue;

            var produce = ResolveProduce(capi, block);
            if (produce?.Collectible?.Code == null) continue;

            entries.Add(new CropEntry
            {
                Kind = CropKind.BerryBush,
                ProduceCode = produce.Collectible.Code.ToString(),
                DisplayName = produce.GetName(),
                SourceDomain = produce.Collectible.Code.Domain,
                Produce = produce,
                PlantBlock = block,
                ColdDamageBelow = block.Attributes?["stopBelowTemperature"].AsFloat(float.NaN) ?? float.NaN,
                HeatDamageAbove = block.Attributes?["stopAboveTemperature"].AsFloat(float.NaN) ?? float.NaN,
                MultipleHarvests = true,
            });
        }
        return entries;
    }

    /// <summary>
    /// Fruit trees: each type in every branch block's TypeProps. The fruit is the type's
    /// resolved FruitStacks; cold hardiness is DieBelowTemp (no upper limit), and the
    /// growth figure sums the flowering, fruiting, and ripening days.
    ///
    /// Vanilla registers one BlockFruitTreeBranch per variant and they all share the one
    /// TypeProps dictionary loaded from a single blocktype file, so taking every block would
    /// list every fruit dozens of times. What we want is one representative per BLOCKTYPE, so
    /// blocks are grouped by domain and first code part, and a mod that ships its own branch
    /// blocktype contributes its roster alongside vanilla's instead of one silently winning.
    ///
    /// This used to take FirstOrDefault of all branch blocks, which meant exactly one roster
    /// could ever be seen. Nothing in The Quire's pack is hidden by that today, verified
    /// 2026-08-26: the pack's only other fruit tree blocktype is bdorchard's, and it declares
    /// class BlockDynamicTreeBranch, a name that appears in none of the pack's 185 assemblies
    /// and none of the game's 6, so it cannot be a BlockFruitTreeBranch and was never visible
    /// here to begin with. The roster count is logged now so that question is answerable from
    /// a log line rather than from a byte scan of the mod folder.
    /// </summary>
    private static List<CropEntry> BuildFruitTrees(ICoreClientAPI capi)
    {
        var entries = new List<CropEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rosters = capi.World.Blocks
            .OfType<BlockFruitTreeBranch>()
            .Where(b => b.TypeProps != null && b.Code != null)
            .GroupBy(b => b.Code.Domain + ":" + b.Code.FirstCodePart())
            .ToList();

        IlluminatedLogger.Info(capi, "crops",
            $"Fruit tree rosters: {rosters.Count} — {string.Join(", ", rosters.Select(g => $"{g.Key} ({g.First().TypeProps.Count} types)"))}");

        foreach (var roster in rosters)
        foreach (var (_, props) in roster.First().TypeProps)
        {
            var produce = props.FruitStacks?
                .Select(d => d?.ResolvedItemstack)
                .FirstOrDefault(s => s?.Collectible?.Code != null);
            if (produce?.Collectible?.Code == null) continue;

            // One card per produce. Two rosters offering the same fruit would otherwise print
            // the same tree twice, and the reader has no way to tell which row is which.
            if (!seen.Add(produce.Collectible.Code.ToString())) continue;

            entries.Add(new CropEntry
            {
                Kind = CropKind.FruitTree,
                ProduceCode = produce.Collectible.Code.ToString(),
                DisplayName = produce.GetName(),
                SourceDomain = produce.Collectible.Code.Domain,
                Produce = produce,
                ColdDamageBelow = props.DieBelowTemp?.avg ?? float.NaN,
                HeatDamageAbove = float.NaN,   // fruit trees have no modelled heat ceiling
                TotalGrowthDays = (props.FloweringDays?.avg ?? 0) + (props.FruitingDays?.avg ?? 0) + (props.RipeDays?.avg ?? 0),
                MultipleHarvests = true,
            });
        }
        return entries;
    }

    /// <summary>
    /// Mushrooms: foraged, not sown, but still food worth cataloguing. Any BlockMushroom
    /// at a grown (not "harvested") state, keyed by the mushroom it drops. No growing
    /// data — the card reads as a forage, not a planting.
    /// </summary>
    private static List<CropEntry> BuildMushrooms(ICoreClientAPI capi)
    {
        var entries = new List<CropEntry>();
        foreach (var block in capi.World.Blocks)
        {
            if (block is not BlockMushroom || block.Code == null) continue;
            if (block.Variant != null && block.Variant.TryGetValue("state", out var state)
                && state.Equals("harvested", StringComparison.OrdinalIgnoreCase)) continue;

            var produce = ResolveProduce(capi, block);
            if (produce?.Collectible?.Code == null) continue;

            entries.Add(new CropEntry
            {
                Kind = CropKind.Mushroom,
                ProduceCode = produce.Collectible.Code.ToString(),
                DisplayName = produce.GetName(),
                SourceDomain = produce.Collectible.Code.Domain,
                Produce = produce,
                PlantBlock = block,
                ColdDamageBelow = float.NaN,
                HeatDamageAbove = float.NaN,
            });
        }
        return entries;
    }
}
