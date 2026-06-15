using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

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
        var entries = BuildSeedCrops(capi);
        // BuildBerryBushes / BuildFruitTrees land next; their props differ from CropProps.
        entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>Logs a one-line summary plus a per-source breakdown, for validating discovery against a pack.</summary>
    public static void LogSummary(ICoreClientAPI capi, List<CropEntry> entries)
    {
        var bySource = entries.GroupBy(e => e.Vanilla ? "vanilla" : e.SourceDomain)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}");
        IlluminatedLogger.Info(capi, "crops", $"Catalog: {entries.Count} seed crop(s) — {string.Join(", ", bySource)}");
        foreach (var e in entries)
            IlluminatedLogger.Info(capi, "crops",
                $"  {e.DisplayName} [{e.SourceDomain}] {e.ProduceCode} — " +
                $"cold<{e.ColdDamageBelow:0.#} heat>{e.HeatDamageAbove:0.#}, {e.TotalGrowthDays:0.#}d, {e.Nutrient}");
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
                SourceDomain = block.Code.Domain,
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

    /// <summary>The food a ripe crop drops: its configured drops minus the seed. First non-seed wins.</summary>
    private static ItemStack? ResolveProduce(Block block)
    {
        if (block.Drops == null) return null;
        foreach (var drop in block.Drops)
        {
            var stack = drop?.ResolvedItemstack;
            var collCode = stack?.Collectible?.Code?.Path;
            if (stack == null || collCode == null) continue;
            if (collCode.StartsWith("seeds-", StringComparison.OrdinalIgnoreCase) ||
                collCode.StartsWith("seed-", StringComparison.OrdinalIgnoreCase)) continue;
            return stack;
        }
        return null;
    }
}
