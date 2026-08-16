using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AlmanacIlluminated;

/// <summary>
/// Lookup from a reveal key to the quest-step texts it completes (`doneWhen` on quest
/// items, schema 0.1.4). A sibling mod that has just granted a knowledge key asks this
/// for the steps it closed, and raises its own toast for them.
///
/// The index is built once per guide-pack list and held until the list is replaced
/// (an author preview swaps the whole list, so identity is the right cache key).
/// Every quest item counts, whatever section it sits in: a step buried in an unrevealed
/// section is still a step the earn completed, and the toast is what tells the reader
/// there is now something new on that page.
///
/// Returning an empty list is the safe degrade — a caller with no match simply raises
/// no step toast.
/// </summary>
public static class QuestStepIndex
{
    private static readonly IReadOnlyList<string> Empty = new List<string>();

    private static readonly object gate = new();
    private static List<GuidePack>? indexedPacks;
    private static Dictionary<string, List<string>>? index;

    public static IReadOnlyList<string> StepsFor(List<GuidePack> guidePacks, string revealKey)
    {
        if (guidePacks.Count == 0 || string.IsNullOrEmpty(revealKey)) return Empty;

        lock (gate)
        {
            if (index == null || !ReferenceEquals(indexedPacks, guidePacks))
            {
                index = Build(guidePacks);
                indexedPacks = guidePacks;
            }
            return index.TryGetValue(revealKey, out var steps) ? steps : Empty;
        }
    }

    /// <summary>
    /// Walks every quest block in every chapter and files each item's text under its
    /// `doneWhen` key. Keys match case-insensitively, the way reveal namespaces do.
    /// Item text is filed exactly as authored, so a toast reads the same words the page
    /// does. Malformed authoring is skipped rather than thrown on: this runs on a live
    /// knowledge earn, and one bad guide file must not take the earn with it.
    /// </summary>
    private static Dictionary<string, List<string>> Build(List<GuidePack> packs)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in packs)
        {
            foreach (var sec in pack.Sections)
            {
                foreach (var block in sec.Blocks)
                {
                    if (!string.Equals(block.Type, "quest", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!block.Props.TryGetValue("items", out var raw) || raw is not JArray items) continue;

                    foreach (var it in items)
                    {
                        if (it is not JObject item) continue;
                        string? key = item["doneWhen"]?.ToString();
                        if (string.IsNullOrEmpty(key)) continue;
                        string? text = item["text"]?.ToString();
                        if (string.IsNullOrEmpty(text)) continue;

                        if (!map.TryGetValue(key!, out var steps)) map[key!] = steps = new List<string>();
                        steps.Add(text!);
                    }
                }
            }
        }

        return map;
    }
}
