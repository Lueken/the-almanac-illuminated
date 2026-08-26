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

/// <summary>Server's reply: every mushroom row, plus the reader's own ground.</summary>
[ProtoContract]
public class ForageAtlasResponse
{
    [ProtoMember(1)] public List<ForageAtlasRow> Rows = new();

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
                var token = asset.ToObject<Newtonsoft.Json.Linq.JArray>();
                entries = new List<Vintagestory.API.Datastructures.JsonObject>();
                foreach (var t in token) entries.Add(new Vintagestory.API.Datastructures.JsonObject(t));
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
