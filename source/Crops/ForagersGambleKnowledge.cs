using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;

namespace AlmanacIlluminated;

/// <summary>
/// Reads the player's Foragers Gamble food knowledge, client-side, with no hard
/// dependency on FG: it is synced onto the player entity's WatchedAttributes, so we
/// read it by string key and gate on the modlist. With FG absent, everything reads
/// as fully known, so the Crops tab degrades to a plain reference.
///
/// Note: this masks any catalogued food the player has not learned while FG is on.
/// FG only actually masks the food categories its config selects; reading that config
/// is out of scope here, so on a pack that does not mask a given category, those
/// entries will still show as unknown until eaten. See the schema reference memo.
/// </summary>
public static class ForagersGambleKnowledge
{
    private const string ModId = "foragersgamble";
    private const string Root = "foragersGamble";

    public static bool Active(ICoreClientAPI capi) => capi.ModLoader.IsModEnabled(ModId);

    /// <summary>0 = unknown, 1 = fully identified (or FG absent), in between = learning.</summary>
    public static float Progress(ICoreClientAPI capi, string produceCode)
    {
        if (string.IsNullOrEmpty(produceCode) || !Active(capi)) return 1f;

        var tree = capi.World.Player?.Entity?.WatchedAttributes?.GetTreeAttribute(Root);
        if (tree == null) return 0f;   // FG present, nothing learned yet

        if (tree["knownFoods"] is StringArrayAttribute known && known.value != null &&
            System.Array.IndexOf(known.value, produceCode) >= 0)
            return 1f;

        return tree.GetTreeAttribute("knowledgeProgress")?.GetFloat(produceCode, 0f) ?? 0f;
    }
}
