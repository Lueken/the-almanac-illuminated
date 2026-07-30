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

    /// <summary>Reveal probe for pack-level `revealedBy`, wired to the host's providers. Null
    /// (or an unregistered namespace) means every chapter is visible, matching the degrade-open
    /// rule the section-level reveals already follow.</summary>
    public Func<string, bool>? RevealProbe { get; set; }

    /// <summary>Whether a chapter is currently readable at all. A pack-level reveal hides the
    /// chapter AND its tab, so an unearned second stage does not advertise itself.</summary>
    public bool IsVisible(GuidePack pack)
        => string.IsNullOrEmpty(pack.RevealedBy) || RevealProbe == null || RevealProbe(pack.RevealedBy!);

    /// <summary>Reading order as the player may currently see it. Filtered live rather than at
    /// load, because a chapter can be earned mid-session; everything downstream (Next, Prev,
    /// Default, the thumb index, paging) inherits the filter for free by going through here.</summary>
    public IReadOnlyList<GuidePack> Ordered
        => allOrdered.Count == 0 || RevealProbe == null
            ? allOrdered
            : allOrdered.Where(IsVisible).ToList();

    /// <summary>Unfiltered reading order, for callers that must see hidden chapters.</summary>
    public IReadOnlyList<GuidePack> OrderedIncludingHidden => allOrdered;

    private readonly List<GuidePack> allOrdered;

    /// <summary>The one chapter that holds the overview slot, or null if none claims it.</summary>
    public GuidePack? Overview { get; }

    /// <summary>Extra chapters that also set overview:true and were demoted. Empty when the config is clean.</summary>
    public IReadOnlyList<GuidePack> OverviewConflicts { get; }

    /// <summary>The contents/index page, reached through the Contents tab. Kept out of the chapter flow.</summary>
    public GuidePack? ContentsPage { get; }

    /// <summary>Chapters that requested their own ribbon tab. Rendered as left-ribbon
    /// tabs (grouped with computed tabs, sorted by `order`), never in the A-Z flow.</summary>
    public IReadOnlyList<GuidePack> RibbonChapters { get; }

    /// <summary>The pinned band: chapters lifted above the A-Z run as named tabs at the
    /// top of the right thumb-index, sorted by `order` then `id`. Gated chapters may pin;
    /// the deterministic total ordering is what keeps competing packs from colliding.</summary>
    public IReadOnlyList<GuidePack> PinnedChapters
        => allPinned.Count == 0 || RevealProbe == null
            ? allPinned
            : allPinned.Where(IsVisible).ToList();

    private readonly List<GuidePack> allPinned;

    public GuideLibrary(ICoreClientAPI capi, List<GuidePack> packs)
    {
        this.capi = capi;
        All = packs;

        // Only one overview is allowed. Among the claimants, the first by order
        // then id wins the slot; the rest are demoted to ordinary front matter.
        var claimants = packs
            .Where(p => p.Overview && string.IsNullOrEmpty(p.Gate))
            .OrderBy(p => p.Order ?? int.MaxValue)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Overview = claimants.FirstOrDefault();
        OverviewConflicts = claimants.Skip(1).ToList();

        // The contents page is its own thing, reached by tab, not part of the flow.
        ContentsPage = packs.FirstOrDefault(p => p.Contents);

        // Ribbon-tab chapters are pulled out of the reading flow and shown as tabs.
        RibbonChapters = packs.Where(p => p.RibbonTab).ToList();

        // The pinned band. Ribbon tabs, the overview, and the contents page have their
        // own homes already; pinning them too would double-place them, so it is ignored.
        allPinned = packs
            .Where(p => p.Pinned && !p.RibbonTab && p != Overview && p != ContentsPage)
            .OrderBy(p => p.Order ?? int.MaxValue)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        allOrdered = BuildOrder(packs, Overview, ContentsPage);

        if (OverviewConflicts.Count > 0)
        {
            string offenders = string.Join("; ", OverviewConflicts.Select(p => $"'{p.Id}' ({p.Source})"));
            IlluminatedLogger.Warn(capi, "library",
                $"More than one chapter sets overview:true, but only one is allowed. " +
                $"Using '{Overview!.Id}' ({Overview.Source}) as the overview. " +
                $"Ignoring the overview flag on: {offenders}. " +
                "Remove overview from all but one chapter to silence this warning.");
        }
    }

    /// <summary>The chapter the book opens to: the overview, else the first front matter, else the first chapter.</summary>
    public GuidePack? Default => Ordered.Count > 0 ? Ordered[0] : null;

    public GuidePack? ById(string? id)
        => string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The next chapter in reading order, or null at the end of the book.</summary>
    public GuidePack? Next(GuidePack p) { int i = IndexOf(p); return i >= 0 && i + 1 < Ordered.Count ? Ordered[i + 1] : null; }

    /// <summary>The previous chapter in reading order, or null at the start of the book.</summary>
    public GuidePack? Prev(GuidePack p) { int i = IndexOf(p); return i > 0 ? Ordered[i - 1] : null; }

    /// <summary>This chapter's position in reading order, or -1 if absent.</summary>
    public int OrderIndex(GuidePack? p) => p == null ? -1 : IndexOf(p);

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

    private List<GuidePack> BuildOrder(List<GuidePack> packs, GuidePack? overview, GuidePack? contents)
    {
        bool IsFront(GuidePack p) => string.IsNullOrEmpty(p.Gate);
        bool InPinnedBand(GuidePack p) => PinnedChapters.Contains(p);

        // Front matter minus the overview winner, the contents page, any ribbon
        // tabs, and the pinned band, sorted by order then id. Demoted overview
        // claimants fall in here.
        var front = packs.Where(p => IsFront(p) && p != overview && p != contents && !p.RibbonTab && !InPinnedBand(p))
            .OrderBy(p => p.Order ?? int.MaxValue)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gated = packs.Where(p => !IsFront(p) && p != contents && !p.RibbonTab && !InPinnedBand(p))
            .OrderBy(p => ModName(p.Gate!), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => Title(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Reading order: overview, then the pinned band, then front matter, then the
        // A-Z run. The band sits ahead of front matter because it is the curated core
        // of the manual; the overview stays the landing page.
        var ordered = new List<GuidePack>();
        if (overview != null) ordered.Add(overview);
        ordered.AddRange(PinnedChapters);
        ordered.AddRange(front);
        ordered.AddRange(gated);
        return ordered;
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
