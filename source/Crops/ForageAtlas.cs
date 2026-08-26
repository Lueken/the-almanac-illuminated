using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacIlluminated;

/// <summary>Client asks the server for the forage atlas. Sent once per session.</summary>
[ProtoContract]
public class ForageAtlasRequest { }

/// <summary>
/// One species' worldgen envelope: where the generator was willing to put it.
///
/// Read that carefully, because the wording on the page depends on it. This is NOT a
/// statement about where mushrooms stand today. A patch can be absent for want of a host
/// tree, or for want of fertility beneath it, and any world that has been played in has had
/// forest felled and ground rebuilt since. So the card says the ground SUITS a species, and
/// never that one will be found there. Promising a specific patch is the Forager's Memory's
/// job, and it can promise it because it watched the reader pick one.
/// </summary>
[ProtoContract]
public class ForageAtlasRow
{
    /// <summary>Species key: the block path with the compass suffix stripped. See
    /// <see cref="ForageAtlas.Key"/>.</summary>
    [ProtoMember(1)] public string Key = "";
    [ProtoMember(2)] public float MinTemp;
    [ProtoMember(3)] public float MaxTemp;
    [ProtoMember(4)] public float MinRain;
    [ProtoMember(5)] public float MaxRain = -1f;      // -1 = unbounded
    [ProtoMember(6)] public float MaxForest = -1f;    // -1 = unbounded
    [ProtoMember(7)] public string Placement = "";    // OnTrees | UnderTrees | "" (open ground)
    [ProtoMember(8)] public string TreeType = "";     // Any | Conifer | Decidious | Tropical | ""

    /// <summary>Which mod actually supplied the species, resolved from the ASSET ORIGIN
    /// rather than the block's code domain. A mod may register into the base game's domain
    /// (Mycodiversity does, for all 36 of its mushrooms), so the code domain cannot tell
    /// them apart and the client has no other way to know. This is the only place that
    /// information exists.</summary>
    [ProtoMember(9)] public string Origin = "";
}

/// <summary>
/// One species' dye yield: the colour a sealed barrel gives back for it.
///
/// Carried separately from the habitat rows rather than as a field on one, because the two
/// facts come from different asset categories and neither implies the other. A species can
/// dye without appearing in any blockpatch, and a mod could add a dye for something the
/// generator never places at all.
/// </summary>
[ProtoContract]
public class ForageDye
{
    [ProtoMember(1)] public string Key = "";
    [ProtoMember(2)] public string Colour = "";
}

/// <summary>Server's reply: every mushroom row, plus the reader's own ground.</summary>
[ProtoContract]
public class ForageAtlasResponse
{
    [ProtoMember(1)] public List<ForageAtlasRow> Rows = new();

    /// <summary>Species that yield a dye. Empty in a pack that adds none, which is every pack
    /// without a mushroom mod: the base game has six dye barrels and not one of them takes a
    /// mushroom.</summary>
    [ProtoMember(2)] public List<ForageDye> Dyes = new();

    /// <summary>The atlas's own median rainfall floor, computed server-side over the rows it
    /// actually built. The wetness clause is printed relative to this rather than to a
    /// constant, because vanilla's 45 species all sit at exactly 0.4 and a fixed threshold
    /// would print the same clause on every one of them.</summary>
    [ProtoMember(4)] public float MedianRain = 0.4f;
}

/// <summary>
/// The forage atlas: worldgen habitat data for mushrooms, carried server to client.
///
/// It has to be carried, because `worldgen` is a server-only asset category
/// (`AssetCategory.worldgen` is declared `EnumAppSide.Server`) and the book renders on the
/// client. Block ATTRIBUTES do reach the client on the block registry, which is why toxicity
/// and the mycelium props need no packet; the blockpatch envelopes do.
///
/// Built by walking the assets already loaded into the reader's own server, from their own
/// copy of whatever mods they run. Nothing is copied into this mod and nothing is shipped.
/// That matters legally, since Mycodiversity carries no licence at all, and it happens to be
/// the better engineering answer too: it picks up vanilla, Mycodiversity and any mushroom mod
/// added later with no per-mod knowledge and nothing to go stale.
/// </summary>
public static class ForageAtlas
{
    private static readonly string[] Compass = { "-north", "-south", "-east", "-west" };

    /// <summary>
    /// Species key from any block or item code: drop the domain, drop the compass suffix.
    ///
    /// A side-growing mushroom registers four rotations of one species and the blockpatch
    /// names one of them, while the card is keyed on the produce. Stripping the rotation is
    /// what makes those two meet. The domain goes because Mycodiversity registers into the
    /// base domain and vanilla is already there, so it carries no information here.
    /// </summary>
    public static string Key(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        string s = code!;
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s.Substring(colon + 1);
        foreach (string c in Compass)
            if (s.EndsWith(c, StringComparison.OrdinalIgnoreCase))
                return s.Substring(0, s.Length - c.Length);
        return s;
    }

    /// <summary>
    /// Walk every worldgen blockpatch and keep the mushroom rows.
    ///
    /// Filtered by resolving `blockCodes[0]` against the block registry and testing for
    /// BlockMushroom, rather than by matching on the word "mushroom" in a path. A code match
    /// would take Substrate's spore paper and miss any mod that names its species otherwise.
    /// </summary>
    public static List<ForageAtlasRow> Build(ICoreServerAPI sapi)
    {
        var rows = new List<ForageAtlasRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in sapi.Assets.GetManyInCategory("worldgen", "blockpatches/"))
        {
            List<Vintagestory.API.Datastructures.JsonObject>? entries = null;
            try
            {
                entries = Entries(asset);
            }
            catch (Exception e)
            {
                IlluminatedLogger.Warn(sapi, "crops", $"forage atlas: {asset.Location} unreadable ({e.Message}); skipped");
                continue;
            }

            foreach (var entry in entries)
            {
                var codes = entry["blockCodes"]?.AsArray<string>();
                if (codes == null || codes.Length == 0) continue;

                var block = sapi.World.GetBlock(new AssetLocation(codes[0]));
                if (block is not BlockMushroom) continue;

                string key = Key(block.Code?.ToString());
                if (key.Length == 0 || !seen.Add(key)) continue;

                rows.Add(new ForageAtlasRow
                {
                    Key = key,
                    MinTemp = entry["minTemp"].AsFloat(-99f),
                    MaxTemp = entry["maxTemp"].AsFloat(99f),
                    MinRain = entry["minRain"].AsFloat(0f),
                    MaxRain = entry["maxRain"].Exists ? entry["maxRain"].AsFloat(-1f) : -1f,
                    MaxForest = entry["maxForest"].Exists ? entry["maxForest"].AsFloat(-1f) : -1f,
                    Placement = entry["placement"].AsString("") ?? "",
                    TreeType = entry["treeType"].AsString("") ?? "",
                    Origin = OriginName(sapi, asset),
                });
            }
        }
        return rows;
    }

    /// <summary>
    /// Walk every barrel recipe and keep the ones a mushroom dyes for.
    ///
    /// Same posture as the habitat walk: nothing is copied in and no mod is named. The base
    /// game dyes from flowers and berries and never from a mushroom, so every row this returns
    /// came from whatever mushroom mod the reader happens to run, and one added tomorrow is
    /// picked up with no change here.
    ///
    /// `recipes` is a server-only asset category (AssetCategory.recipes is EnumAppSide.Server),
    /// which is the whole reason this rides a packet rather than being read on the client.
    /// </summary>
    public static List<ForageDye> BuildDyes(ICoreServerAPI sapi)
    {
        var dyes = new List<ForageDye>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in sapi.Assets.GetManyInCategory("recipes", "barrel/"))
        {
            List<Vintagestory.API.Datastructures.JsonObject>? entries = null;
            try
            {
                entries = Entries(asset);
            }
            catch (Exception e)
            {
                IlluminatedLogger.Warn(sapi, "crops", $"forage dyes: {asset.Location} unreadable ({e.Message}); skipped");
                continue;
            }

            foreach (var recipe in entries)
            {
                string colour = DyeColour(recipe["output"]["code"].AsString(""));
                if (colour.Length == 0) continue;

                var ingredients = recipe["ingredients"].AsArray();
                if (ingredients == null) continue;

                foreach (var ing in ingredients)
                {
                    if (ing == null) continue;
                    foreach (var block in IngredientBlocks(sapi, ing))
                    {
                        if (block is not BlockMushroom) continue;
                        string key = Key(block.Code?.ToString());
                        // First colour wins. A species that appears in two dye recipes is
                        // reported once rather than contradicting itself on the page.
                        if (key.Length == 0 || !seen.Add(key)) continue;
                        dyes.Add(new ForageDye { Key = key, Colour = colour });
                    }
                }
            }
        }
        return dyes;
    }

    /// <summary>A file's entries, whether it holds an array of them or a single one. Both
    /// shapes occur in the base game's own assets.</summary>
    private static List<Vintagestory.API.Datastructures.JsonObject> Entries(IAsset asset)
    {
        var list = new List<Vintagestory.API.Datastructures.JsonObject>();
        var token = asset.ToObject<Newtonsoft.Json.Linq.JToken>();
        if (token is Newtonsoft.Json.Linq.JArray arr)
        {
            foreach (var t in arr) list.Add(new Vintagestory.API.Datastructures.JsonObject(t));
        }
        else if (token != null)
        {
            list.Add(new Vintagestory.API.Datastructures.JsonObject(token));
        }
        return list;
    }

    /// <summary>The colour word out of a dye output code, or empty when the recipe makes
    /// something else. Anything still holding a wildcard or a substitution placeholder is
    /// dropped, because it is not a word the page could print.</summary>
    private static string DyeColour(string? outputCode)
    {
        if (string.IsNullOrEmpty(outputCode)) return "";
        string s = outputCode!;
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s.Substring(colon + 1);
        if (!s.StartsWith("dye-", StringComparison.OrdinalIgnoreCase)) return "";
        string colour = s.Substring(4);
        return colour.IndexOf('{') >= 0 || colour.IndexOf('*') >= 0 ? "" : colour;
    }

    /// <summary>Every block one ingredient line can stand for. Concrete codes resolve directly;
    /// a wildcard is expanded through the block registry and narrowed by allowedVariants when
    /// the recipe supplies one. No mushroom dye in the pack today uses a wildcard, but a
    /// recipe walker that ignored them would fail quietly rather than loudly.</summary>
    private static IEnumerable<Block> IngredientBlocks(ICoreServerAPI sapi, Vintagestory.API.Datastructures.JsonObject ing)
    {
        string code = ing["code"].AsString("") ?? "";
        if (code.Length == 0) yield break;
        var loc = new AssetLocation(code);

        if (code.IndexOf('*') < 0)
        {
            var one = sapi.World.GetBlock(loc);
            if (one != null) yield return one;
            yield break;
        }

        var allowed = ing["allowedVariants"].AsArray<string>();
        foreach (var b in sapi.World.SearchBlocks(loc))
        {
            if (b == null) continue;
            if (allowed == null || allowed.Length == 0) { yield return b; continue; }
            string s = b.Code?.ToString() ?? "";
            foreach (var v in allowed)
            {
                if (string.IsNullOrEmpty(v)) continue;
                if (s.EndsWith("-" + v, StringComparison.OrdinalIgnoreCase)
                    || s.IndexOf("-" + v + "-", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    yield return b;
                    break;
                }
            }
        }
    }

    /// <summary>The mod that supplied this asset, by its display name. `asset.Origin` names
    /// the loaded source, which is what tells Mycodiversity apart from the base game even
    /// though both sit in the `game` domain.</summary>
    private static string OriginName(ICoreServerAPI sapi, IAsset asset)
    {
        string? path = asset.Origin?.OriginPath;
        if (string.IsNullOrEmpty(path)) return "";
        foreach (var mod in sapi.ModLoader.Mods)
        {
            string? src = mod.FileName;
            if (!string.IsNullOrEmpty(src) && path!.IndexOf(
                    System.IO.Path.GetFileNameWithoutExtension(src), StringComparison.OrdinalIgnoreCase) >= 0)
                return mod.Info?.Name ?? "";
        }
        return "";
    }

    /// <summary>Median of the rows' rainfall floors, so the wetness clause can be printed
    /// relative to the pack the reader actually runs.</summary>
    public static float MedianRain(List<ForageAtlasRow> rows)
    {
        if (rows.Count == 0) return 0.4f;
        var mins = new List<float>(rows.Count);
        foreach (var r in rows) mins.Add(r.MinRain);
        mins.Sort();
        return mins[mins.Count / 2];
    }
}
