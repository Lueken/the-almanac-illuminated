using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// Discovers and parses guide packs. Scans every loaded mod's assets at
/// almanac/guides/*.json (path-based, so no custom asset category is needed),
/// parses each to a <see cref="GuidePack"/>, and gates by the `gate` modid.
///
/// Run in AssetsLoaded, when api.Assets is populated. See docs/SCHEMA.md.
/// </summary>
public static class GuidePackLoader
{
    public static List<GuidePack> Load(ICoreClientAPI capi)
    {
        var packs = new List<GuidePack>();

        var assets = capi.Assets.GetMany("almanac/guides/");
        foreach (var asset in assets)
        {
            if (!asset.Location.Path.EndsWith(".json")) continue;
            try
            {
                var pack = asset.ToObject<GuidePack>();
                if (pack == null) continue;
                pack.Source = asset.Location.ToString();
                packs.Add(pack);
                IlluminatedLogger.Debug(capi, "loader",
                    $"Parsed {asset.Location}: '{pack.Title}' (gate '{pack.Gate ?? "none"}', {pack.Sections.Count} sections)");
            }
            catch (System.Exception e)
            {
                IlluminatedLogger.Warn(capi, "loader", $"Failed to parse {asset.Location}: {e.Message}");
            }
        }

        var visible = new List<GuidePack>();
        foreach (var p in packs)
        {
            if (string.IsNullOrEmpty(p.Gate) || capi.ModLoader.IsModEnabled(p.Gate)) visible.Add(p);
        }

        IlluminatedLogger.Info(capi, "loader",
            $"Found {packs.Count} guide pack(s), {visible.Count} visible after gating" +
            (visible.Count > 0 ? $": {string.Join(", ", visible.ConvertAll(p => p.Title))}" : ""));

        return visible;
    }
}
