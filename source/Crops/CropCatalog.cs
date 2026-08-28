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

    // ---- Perennials -------------------------------------------------------------
    // Added for fruit familiarity. A perennial needs an identity a supplier can key on
    // and something worth handing over at the top of a tier ladder, and neither existed.

    /// <summary>A fruit tree's identity, as "domain:treeType". Null for everything else.
    /// Fruit trees are built from a branch block's TypeProps rather than from a block of
    /// their own, so <see cref="PlantBlock"/> is null for them and a familiarity supplier
    /// has nothing else to address them by. The domain is in the key because more than one
    /// mod registers its own branch block with its own type table.</summary>
    public string? TypeKey;

    /// <summary>Cold hours a deciduous fruit tree must bank before it will flower, and the
    /// temperature that counts as cold. Vanilla surfaces this nowhere at all, which makes
    /// it the most opaque per-species fact an orchard has.</summary>
    public float VernalizationHours = float.NaN;
    public float VernalizationTemp = float.NaN;

    /// <summary>An evergreen fruit tree, which does not vernalize and does not drop leaves.</summary>
    public bool Evergreen;

    /// <summary>A berry bush's dormancy band. Per species in the asset: currants sleep and
    /// wake at different temperatures from everything else.</summary>
    public float DormantBelow = float.NaN;
    public float WakesAbove = float.NaN;

    /// <summary>A berry bush's nutrient draw per cycle at its best health. Per species:
    /// cloudberry and strawberry draw half of what the rest do.</summary>
    public float DrawAtBest = float.NaN;

    // ---- Mushrooms --------------------------------------------------------------
    // Everything here already reaches the client on the block registry. The habitat data a
    // mushroom card really wants (temperature band, host tree, rainfall) lives in
    // worldgen/blockpatches, and `worldgen` is a SERVER-ONLY asset category, so none of it is
    // readable from here at all. That needs a packet and is a separate build.

    /// <summary>What eating one costs in health. Negative is harmful, zero is ordinary food.
    /// The only nutritional axis a mushroom varies on: every species in the pack is satiety 80,
    /// category Vegetable, so an edibility line would print one number eighty-one times.</summary>
    public float Toxicity;

    /// <summary>Above zero, it brings visions. Kept apart from toxicity because two of the six
    /// psychedelic species are also poisonous and the card must say both.</summary>
    public float Psychedelic;

    /// <summary>The temperature at which a standing flush is destroyed, and whether it dies
    /// back after fruiting. Both are read every server tick by BlockEntityMycelium, so they
    /// are live behaviour rather than decoration. Five species set -99 and false together and
    /// stand through winter forever, which is the one genuinely useful client-only fact.</summary>
    public float DieWhenTempBelow = float.NaN;
    public bool DiesAfterFruiting = true;

    /// <summary>Never dies back at all: it neither freezes off nor ends with its own fruiting.
    /// Both halves are required, so a species that merely tolerates deep cold is not claimed
    /// to be permanent.</summary>
    public bool StandsWinter => !DiesAfterFruiting && !float.IsNaN(DieWhenTempBelow) && DieWhenTempBelow <= -90;

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
    ///
    /// Plant debris joined the list on 2026-08-26, from the same failure showing up on the
    /// BUSHES pages as a card reading "Plant debris". The base game says outright what it is,
    /// in block-handbooktext-fruitingbush-*: "Wild fruiting bushes cannot be moved - if you
    /// break them they will just drop plant debris." So it is litter by the game's own account,
    /// and it was the first drop this filter did not reject. Matched on the prefix rather than
    /// the whole code because the base game ships one variant, plantdebris-leafy, and there is
    /// no reason a pack could not add another.
    /// </summary>
    private static bool IsNotProduce(string path) =>
        path.StartsWith("seeds-", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("seed-", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("plantdebris", StringComparison.OrdinalIgnoreCase) ||
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
    /// Berry bushes, one entry per species, read from the LIVE blocktype first.
    ///
    /// This method used to match any block whose path contained "berrybush" at variant
    /// state "ripe", and on 1.22 that reaches only vanilla's legacy blocktypes. Verified
    /// against the 1.22 assets on 2026-08-26:
    ///
    ///   survival/blocktypes/plant/fruitingbush.json    code "fruitingbush", TEN species,
    ///                                                  variants state(wild|grown) x type x
    ///                                                  cover(free|snow)
    ///   survival/blocktypes/legacy/bigberrybush.json   redcurrant, blackcurrant, whitecurrant
    ///   survival/blocktypes/legacy/smallberrybush.json blueberry, cranberry
    ///
    /// So the page listed the five legacy species and silently dropped beautyberry,
    /// blackberry, cloudberry, raspberry and strawberry, which exist only on the live
    /// blocktype. Neither legacy file is disabled, so they still register and would double
    /// every species they share if both sources were taken; hence live wins and legacy is
    /// only a fallback for a world that somehow has no fruitingbush at all.
    ///
    /// The "ripe" test is what hid it. On the legacy bushes state IS ripeness
    /// (empty|flowering|ripe), but on the live bush state means WILD or GROWN and ripeness
    /// lives on the block entity instead, so the same filter means two different things.
    /// Here it is only applied to legacy, where it still means what it used to.
    ///
    /// Temperatures moved too. Legacy keeps stopBelow/AboveTemperature at the top of its
    /// attributes; the live bush keeps the same 4 and 30 under
    /// growthProperties.pauseGrowthBelow/AboveTemperature. Both are read, live name first.
    /// </summary>
    private static List<CropEntry> BuildBerryBushes(ICoreClientAPI capi)
    {
        var entries = new List<CropEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Live first, then legacy, so a species present in both is taken from the live
        // blocktype and the legacy copy falls out on the produce-code dedup below.
        var live = capi.World.Blocks.Where(IsLiveFruitingBush).ToList();
        var legacy = capi.World.Blocks.Where(IsLegacyRipeBerryBush).ToList();

        IlluminatedLogger.Info(capi, "crops",
            $"Berry bushes: {live.Count} live fruitingbush block(s), {legacy.Count} legacy ripe block(s)");

        foreach (var block in live.Concat(legacy))
        {
            // Harvest first, Drops second. A live fruiting bush keeps its berry nowhere near
            // its Drops; the legacy blocktype does. Live blocks come first in the sequence, so
            // where both exist for a species the live one claims the produce code and the
            // legacy copy falls out on the dedupe, which is the intended direction.
            var produce = ResolveBushHarvest(capi, block) ?? ResolveProduce(capi, block);
            if (produce?.Collectible?.Code == null) continue;
            if (!seen.Add(produce.Collectible.Code.ToString())) continue;

            var growth = block.Attributes?["growthProperties"];
            entries.Add(new CropEntry
            {
                Kind = CropKind.BerryBush,
                ProduceCode = produce.Collectible.Code.ToString(),
                DisplayName = produce.GetName(),
                SourceDomain = produce.Collectible.Code.Domain,
                Produce = produce,
                PlantBlock = block,
                ColdDamageBelow = Temp(growth, "pauseGrowthBelowTemperature")
                    ?? block.Attributes?["stopBelowTemperature"].AsFloat(float.NaN) ?? float.NaN,
                HeatDamageAbove = Temp(growth, "pauseGrowthAboveTemperature")
                    ?? block.Attributes?["stopAboveTemperature"].AsFloat(float.NaN) ?? float.NaN,
                MultipleHarvests = true,
                // The ByType variants in the asset are already resolved per block by the time
                // the registry hands it over, so these read as plain keys here.
                DormantBelow = Temp(growth, "goDormantBelowTemperature") ?? float.NaN,
                WakesAbove = Temp(growth, "leaveDormantAboveTemperature") ?? float.NaN,
                DrawAtBest = BestDraw(growth),
            });
        }

        // Cards, not blocks. The count above says what was considered; this says what survived
        // resolution and the produce dedupe, and the gap between them is where a bush goes
        // quiet. A block whose real produce cannot resolve does not error, it simply drops out
        // or collapses onto whatever code it did resolve to, which is how fifteen blocks read
        // as one card named "Plant debris" and nobody noticed until it was on the page.
        IlluminatedLogger.Info(capi, "crops", $"Berry bushes: {entries.Count} card(s) after produce dedupe");
        return entries;
    }

    /// <summary>
    /// A live fruiting bush's berry, which is nowhere near its Drops.
    ///
    /// The base game puts it in `block.Attributes["harvestedStacks"]` and resolves it there
    /// (BlockBehaviorFruitingBush.cs:96-97), because Drops is what BREAKING the bush gives you
    /// and the game makes that plant debris deliberately: "Wild fruiting bushes cannot be moved
    /// - if you break them they will just drop plant debris." So for the ten live blocks there
    /// is no berry in Drops to find, and reading them the way the legacy blocktype is read gave
    /// ten bushes that all resolved to litter and collapsed onto one card.
    ///
    /// Taken off the behaviour rather than re-parsed out of the attribute, so the ByType pick
    /// and the {type} substitution stay the game's own and cannot drift from it. Absent on the
    /// legacy blocktype, which does keep its berry in Drops, so null here means "ask Drops".
    /// </summary>
    private static ItemStack? ResolveBushHarvest(ICoreClientAPI capi, Block block)
    {
        var beh = block.GetBehavior<Vintagestory.GameContent.BlockBehaviorFruitingBush>();
        if (beh?.harvestedStacks == null) return null;

        foreach (var hs in beh.harvestedStacks)
        {
            var stack = hs?.ResolvedItemstack;
            string? path = stack?.Collectible?.Code?.Path;
            if (stack == null || path == null || IsNotProduce(path)) continue;
            return Canonicalise(capi, stack);
        }
        return null;
    }

    /// <summary>Null rather than NaN when the key is absent, so the caller can fall through
    /// to the legacy attribute name instead of committing to a missing value.</summary>
    private static float? Temp(Vintagestory.API.Datastructures.JsonObject? growth, string key)
    {
        float v = growth?[key].AsFloat(float.NaN) ?? float.NaN;
        return float.IsNaN(v) ? null : v;
    }

    /// <summary>
    /// What a bush draws per cycle when it is at its best. The asset holds a draw for each of
    /// four health states; the best one is the honest single figure, because it is what the
    /// bed must supply for the bush to stay there. The three lower rows describe a bush that
    /// is already going short, which is a consequence rather than a property of the species.
    ///
    /// N, P and K are equal on every vanilla species, so one number says it. If a mod ever
    /// splits them this returns the largest, which is still the figure the bed has to meet.
    /// </summary>
    private static float BestDraw(Vintagestory.API.Datastructures.JsonObject? growth)
    {
        var best = growth?["nutrientUseByHealthState"]?["bountiful"];
        if (best == null || !best.Exists) return float.NaN;
        float n = best["n"].AsFloat(float.NaN);
        float p = best["p"].AsFloat(float.NaN);
        float k = best["k"].AsFloat(float.NaN);
        float max = float.NaN;
        foreach (float v in new[] { n, p, k })
            if (!float.IsNaN(v) && (float.IsNaN(max) || v > max)) max = v;
        return max;
    }

    /// <summary>One representative block per live species: grown rather than wild, and
    /// unsnowed, so the entry describes a bush somebody is keeping rather than a chance
    /// find under snow. Ripeness is not a variant here, so it is not tested.</summary>
    private static bool IsLiveFruitingBush(Block? block)
    {
        if (block?.Code == null || block.Variant == null) return false;
        if (!block.Code.FirstCodePart().Equals("fruitingbush", StringComparison.OrdinalIgnoreCase)) return false;
        return Variant(block, "state") == "grown" && Variant(block, "cover") == "free";
    }

    /// <summary>The pre-1.22 bushes, kept only so a world without the live blocktype still
    /// lists something. Here "state" really does mean ripeness.</summary>
    private static bool IsLegacyRipeBerryBush(Block? block)
    {
        if (block?.Code == null) return false;
        if (!block.Code.Path.Contains("berrybush", StringComparison.OrdinalIgnoreCase)) return false;
        return block.Variant == null || Variant(block, "state") == "ripe";
    }

    private static string? Variant(Block block, string key) =>
        block.Variant != null && block.Variant.TryGetValue(key, out var v) ? v.ToLowerInvariant() : null;

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
        {
            // The branch block's own domain, not the fruit's. bdorchard's fig drops game:fruit-fig,
            // so keying identity on the produce would file a modded tree under the base game.
            string treeDomain = roster.First().Code.Domain;

            foreach (var (treeType, props) in roster.First().TypeProps)
            {
                var produce = props.FruitStacks?
                    .Select(d => d?.ResolvedItemstack)
                    .FirstOrDefault(s => s?.Collectible?.Code != null);
                if (produce?.Collectible?.Code == null) continue;

                // One card per produce. Two rosters offering the same fruit would otherwise print
                // the same tree twice, and the reader has no way to tell which row is which.
                if (!seen.Add(produce.Collectible.Code.ToString())) continue;

                bool evergreen = props.CycleType == EnumTreeCycleType.Evergreen;
                entries.Add(new CropEntry
                {
                    Kind = CropKind.FruitTree,
                    ProduceCode = produce.Collectible.Code.ToString(),
                    DisplayName = produce.GetName(),
                    SourceDomain = produce.Collectible.Code.Domain,
                    Produce = produce,
                    TypeKey = treeDomain + ":" + treeType,
                    ColdDamageBelow = props.DieBelowTemp?.avg ?? float.NaN,
                    HeatDamageAbove = float.NaN,   // fruit trees have no modelled heat ceiling
                    TotalGrowthDays = (props.FloweringDays?.avg ?? 0) + (props.FruitingDays?.avg ?? 0) + (props.RipeDays?.avg ?? 0),
                    MultipleHarvests = true,
                    Evergreen = evergreen,
                    // An evergreen never vernalizes, so leave the figures absent rather than
                    // printing a cold requirement it will never wait on.
                    VernalizationHours = evergreen ? float.NaN : props.VernalizationHours?.avg ?? float.NaN,
                    VernalizationTemp = evergreen ? float.NaN : props.VernalizationTemp?.avg ?? float.NaN,
                });
            }
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

            // nutritionPropsByType and mushroomProps both resolve per variant on the block, so
            // the block is the right source here rather than the dropped stack.
            var food = block.NutritionProps ?? produce.Collectible.NutritionProps;
            var shroom = block.Attributes?["mushroomProps"];

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
                Toxicity = food?.Health ?? 0f,
                Psychedelic = food?.Psychedelic ?? 0f,
                DieWhenTempBelow = shroom?["dieWhenTempBelow"].AsFloat(float.NaN) ?? float.NaN,
                DiesAfterFruiting = shroom?["dieAfterFruiting"].AsBool(true) ?? true,
            });
        }
        return entries;
    }
}
