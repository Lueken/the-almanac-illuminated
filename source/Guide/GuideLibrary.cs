using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace AlmanacIlluminated;

/// <summary>
/// One mod's row in a contents block: its display name, its modid, and the
/// chapter that documents it, if any.
/// </summary>
public sealed record ModEntry(string Name, string ModId, GuidePack? Chapter);

/// <summary>
/// The set of visible chapters plus the order the book reads them in. Front
/// matter comes first (the overview pinned ahead of the rest, then by `order`,
/// then `id`); gated chapters follow, sorted by the display name of the mod each
/// documents, then by title. Also resolves links, mod names, and #langkeys.
/// </summary>
public sealed class GuideLibrary
{
    private readonly ICoreClientAPI capi;

    // Base-game mods a contents block hides unless it asks for everything.
    private static readonly HashSet<string> SystemMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "survival", "creative", "essentials"
    };

    public IReadOnlyList<GuidePack> All { get; }
    public IReadOnlyList<GuidePack> Ordered { get; }

    public GuideLibrary(ICoreClientAPI capi, List<GuidePack> packs)
    {
        this.capi = capi;
        All = packs;
        Ordered = BuildOrder(packs);

        int overviews = packs.Count(p => p.Overview && string.IsNullOrEmpty(p.Gate));
        if (overviews > 1)
            IlluminatedLogger.Warn(capi, "library",
                $"{overviews} chapters claim overview. The first by order wins; the rest sort as normal front matter.");
    }

    /// <summary>The chapter the book opens to: the overview, else the first front matter, else the first chapter.</summary>
    public GuidePack? Default => Ordered.Count > 0 ? Ordered[0] : null;

    public GuidePack? ById(string? id)
        => string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The next chapter in reading order, or null at the end of the book.</summary>
    public GuidePack? Next(GuidePack p) { int i = IndexOf(p); return i >= 0 && i + 1 < Ordered.Count ? Ordered[i + 1] : null; }

    /// <summary>The previous chapter in reading order, or null at the start of the book.</summary>
    public GuidePack? Prev(GuidePack p) { int i = IndexOf(p); return i > 0 ? Ordered[i - 1] : null; }

    private int IndexOf(GuidePack p)
    {
        for (int i = 0; i < Ordered.Count; i++) if (Ordered[i] == p) return i;
        return -1;
    }

    /// <summary>The display name of a loaded mod, or the modid if it is not loaded.</summary>
    public string ModName(string modid)
        => capi.ModLoader.Mods.FirstOrDefault(m => string.Equals(m.Info?.ModID, modid, StringComparison.OrdinalIgnoreCase))?.Info?.Name ?? modid;

    /// <summary>
    /// One row per loaded mod for a contents block. With addedOnly, the base-game
    /// mods are left out. Each row links to the mod's chapter when one exists.
    /// Sorted by display name.
    /// </summary>
    public List<ModEntry> ModEntries(bool addedOnly)
    {
        var rows = new List<ModEntry>();
        foreach (var mod in capi.ModLoader.Mods)
        {
            var info = mod.Info;
            if (info?.ModID == null) continue;
            if (addedOnly && SystemMods.Contains(info.ModID)) continue;

            var chapter = All.FirstOrDefault(p => string.Equals(p.Gate, info.ModID, StringComparison.OrdinalIgnoreCase));
            rows.Add(new ModEntry(info.Name ?? info.ModID, info.ModID, chapter));
        }
        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    /// <summary>A chapter's title, localized, for the title bar and indexes.</summary>
    public string Title(GuidePack p) => Localize(p.Title) ?? p.Id ?? "Untitled";

    private List<GuidePack> BuildOrder(List<GuidePack> packs)
    {
        bool IsFront(GuidePack p) => string.IsNullOrEmpty(p.Gate);

        var front = packs.Where(IsFront)
            .OrderByDescending(p => p.Overview)
            .ThenBy(p => p.Order ?? int.MaxValue)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gated = packs.Where(p => !IsFront(p))
            .OrderBy(p => ModName(p.Gate!), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => Title(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        front.AddRange(gated);
        return front;
    }

    /// <summary>
    /// Resolves a string field per schema section 9: a leading '#' marks a lang
    /// key, '##' escapes to a literal '#', anything else is a literal.
    /// </summary>
    public static string? Localize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.StartsWith("##")) return s.Substring(1);
        if (s[0] == '#') return Lang.Get(s.Substring(1));
        return s;
    }
}
