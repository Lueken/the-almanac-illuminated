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
