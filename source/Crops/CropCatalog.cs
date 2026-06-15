using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacIlluminated;

public enum CropKind { SeedCrop, BerryBush, FruitTree }

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

    // Seed-crop growing data, straight off the crop block's CropProps.
    public EnumSoilNutrient Nutrient;
    public float ColdDamageBelow;
    public float HeatDamageAbove;
    public double TotalGrowthDays;
    public int GrowthStages;
    public bool MultipleHarvests;

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
        var bySource = entries.GroupBy(e => e.Vanilla ? "vanilla" : e.SourceDomain)
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
        // Keep the highest-stage block per crop type: the ripe stage carries the produce drop.
        var bestByType = new Dictionary<string, (Block block, int stage)>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in capi.World.Blocks)
        {
            if (block?.CropProps == null || block.Code == null) continue;
            if (!TryParseCrop(block.Code, out string typeKey, out int stage)) continue;

            if (!bestByType.TryGetValue(typeKey, out var cur) || stage > cur.stage)
                bestByType[typeKey] = (block, stage);
        }

        var entries = new List<CropEntry>();
        foreach (var (typeKey, (block, _)) in bestByType)
        {
            var produce = ResolveProduce(block);
            if (produce?.Collectible?.Code == null) continue;   // no readable produce: skip rather than show a blank

            var cp = block.CropProps;
            entries.Add(new CropEntry
            {
                Kind = CropKind.SeedCrop,
                ProduceCode = produce.Collectible.Code.ToString(),
                DisplayName = produce.GetName(),
                SourceDomain = produce.Collectible.Code.Domain,   // the food's origin, so mod produce flags as modded
                Produce = produce,
                Nutrient = cp.RequiredNutrient,
                ColdDamageBelow = cp.ColdDamageBelow,
                HeatDamageAbove = cp.HeatDamageAbove,
                TotalGrowthDays = cp.TotalGrowthMonths > 0 ? cp.TotalGrowthMonths * capi.World.Calendar.DaysPerMonth : cp.TotalGrowthDays,
                GrowthStages = cp.GrowthStages,
                MultipleHarvests = cp.MultipleHarvests,
            });
        }
        return entries;
    }

    /// <summary>Crop blocks are coded "crop-{type}-{stage}" (the type itself may hold dashes).</summary>
    private static bool TryParseCrop(AssetLocation code, out string typeKey, out int stage)
    {
        typeKey = ""; stage = 0;
        string path = code.Path;
        if (!path.StartsWith("crop-", StringComparison.OrdinalIgnoreCase)) return false;

        int dash = path.LastIndexOf('-');
        if (dash <= 4 || !int.TryParse(path.Substring(dash + 1), out stage)) return false;

        string type = path.Substring(5, dash - 5);    // between "crop-" and the trailing "-{stage}"
        if (type.Length == 0) return false;
        typeKey = code.Domain + ":" + type;
        return true;
    }

    /// <summary>
    /// The food a plant drops: its configured drops minus the seed, the cutting, and
    /// the plant's own block (an unripe bush drops only itself, so it resolves to null
    /// and is skipped; a ripe bush drops the berry). First food wins.
    /// </summary>
    private static ItemStack? ResolveProduce(Block block)
    {
        if (block.Drops == null) return null;
        foreach (var drop in block.Drops)
        {
            var stack = drop?.ResolvedItemstack;
            var collCode = stack?.Collectible?.Code?.Path;
            if (stack == null || collCode == null) continue;
            if (collCode.StartsWith("seeds-", StringComparison.OrdinalIgnoreCase) ||
                collCode.StartsWith("seed-", StringComparison.OrdinalIgnoreCase) ||
                collCode.Contains("cutting", StringComparison.OrdinalIgnoreCase) ||
                collCode.Contains("berrybush", StringComparison.OrdinalIgnoreCase)) continue;
            return stack;
        }
        return null;
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

            var produce = ResolveProduce(block);
            if (produce?.Collectible?.Code == null) continue;

            entries.Add(new CropEntry
            {
                Kind = CropKind.BerryBush,
                ProduceCode = produce.Collectible.Code.ToString(),
                DisplayName = produce.GetName(),
                SourceDomain = produce.Collectible.Code.Domain,
                Produce = produce,
                ColdDamageBelow = block.Attributes?["stopBelowTemperature"].AsFloat(float.NaN) ?? float.NaN,
                HeatDamageAbove = block.Attributes?["stopAboveTemperature"].AsFloat(float.NaN) ?? float.NaN,
                MultipleHarvests = true,
            });
        }
        return entries;
    }

    /// <summary>
    /// Fruit trees: each type in the branch block's TypeProps. The fruit is the type's
    /// resolved FruitStacks; cold hardiness is DieBelowTemp (no upper limit), and the
    /// growth figure sums the flowering, fruiting, and ripening days.
    /// </summary>
    private static List<CropEntry> BuildFruitTrees(ICoreClientAPI capi)
    {
        var entries = new List<CropEntry>();
        var branch = capi.World.Blocks.OfType<BlockFruitTreeBranch>().FirstOrDefault(b => b.TypeProps != null);
        if (branch == null) return entries;

        foreach (var (_, props) in branch.TypeProps)
        {
            var produce = props.FruitStacks?
                .Select(d => d?.ResolvedItemstack)
                .FirstOrDefault(s => s?.Collectible?.Code != null);
            if (produce?.Collectible?.Code == null) continue;

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
}
