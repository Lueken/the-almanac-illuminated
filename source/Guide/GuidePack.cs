using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AlmanacIlluminated;

/// <summary>
/// A parsed guide pack: one chapter, as authored in JSON per docs/SCHEMA.md.
/// Newtonsoft matches property names case-insensitively, so the camelCase JSON
/// fields bind to these PascalCase members without attributes.
/// </summary>
public class GuidePack
{
    public int SchemaVersion;
    public string? Id;
    public string? Gate;
    public string? Title;
    public string? Subtitle;
    public string? Byline;
    public string? Icon;
    public string? AccentColor;
    public int? Order;

    /// <summary>Gate-less chapters only. Pins this chapter first in the front matter
    /// and makes it the book's default landing page. The server overview opts in here.</summary>
    public bool Overview;

    /// <summary>Marks this chapter as the contents/index page. It is kept out of the
    /// chapter reading flow and tabs, and reached through the Contents tab instead.</summary>
    public bool Contents;

    /// <summary>Pins this chapter as its own left-ribbon tab (titled by `title`),
    /// pulled out of the A-Z reading flow. Grouped with computed tabs and sorted
    /// among them by `order`. For a mod that wants dedicated tabs, not an index entry.</summary>
    public bool RibbonTab;

    /// <summary>Lifts this chapter into the pinned band: named tabs at the top of the
    /// right thumb-index, ahead of the letters, sorted by `order` then `id`. Unlike the
    /// A-Z run this is allowed on gated chapters — the band is the server-curation shelf
    /// (the schema's no-placement rule protects the alphabetical run, and the pinned
    /// band keeps its spirit through the same total ordering front matter uses).</summary>
    public bool Pinned;

    /// <summary>Hides the WHOLE chapter, tab included, until the host mod reports this
    /// knowledge key as earned (same provider contract as a section's `revealedBy`).
    ///
    /// A section-level reveal leaves an empty titled page and a visible tab, which is the
    /// right shape for a page the reader should know is coming. A pack-level reveal is for a
    /// chapter that should not exist yet: the second stage of a road, where even the title
    /// would spoil the first. Degrades open, like every other reveal: no provider registered
    /// for the namespace means the chapter is visible.</summary>
    public string? RevealedBy;

    public List<GuideSection> Sections = new();

    /// <summary>Asset location this pack was read from. For logs and diagnostics.</summary>
    [JsonIgnore] public string Source = "";
}

public class GuideSection
{
    public string? Id;
    public string? Title;
    public bool PageBreakBefore;
    public bool KeepTogether;

    /// <summary>Reveal key ("namespace:rest"). The section's blocks render only when the
    /// registered reveal provider for that namespace says the key is earned. No provider
    /// registered = always visible (a key nobody can set must never mean a dead page).</summary>
    public string? RevealedBy;

    /// <summary>Optional placeholder line shown while the section is unrevealed. Falls
    /// back to the shipped default. Runs through the '#langkey' convention.</summary>
    public string? Hint;

    public List<GuideBlock> Blocks = new();
}

/// <summary>
/// One content block. `Type` selects the renderer; everything else is captured
/// in <see cref="Props"/> so the schema can grow without changing this class.
/// </summary>
public class GuideBlock
{
    public string? Type;
    public string[]? Requires;

    [JsonExtensionData] public Dictionary<string, JToken> Props = new();

    public string? Str(string key) => Props.TryGetValue(key, out var v) ? v.ToString() : null;
    public bool Bool(string key) => Props.TryGetValue(key, out var v) && v.Type == JTokenType.Boolean && v.Value<bool>();
}
