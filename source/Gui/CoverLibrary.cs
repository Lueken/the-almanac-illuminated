using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// The covers a reader may bind their Almanac in. Every one is a material the game
/// already has: the picker shows the real item, and the cover wears that item's own
/// material texture, read from the game's domain at runtime. No cover art ships with
/// this mod beyond its default.
///
/// <para>The texture path for each entry is not invented — it is the one the item
/// itself declares. Leather's item JSON carries
/// <c>inContainerTexture: block/leather/{color}</c> and cloth's carries
/// <c>block/cloth/linen/{color}</c>, so a cover and the hide it was cut from are
/// guaranteed to match, and a vanilla retexture carries through untouched.</para>
///
/// <para>The cover is the READER's choice and never a pack's (ruled 2026-08-26). A
/// guide pack has no say here: two people reading the same book should be able to
/// bind it differently.</para>
/// </summary>
public static class CoverLibrary
{
    /// <summary>One binding: what the picker shows, and what the boards wear.</summary>
    public sealed class Cover
    {
        /// <summary>Stable key written to settings. Never localized, never reordered.</summary>
        public string Key = "";
        public string Display = "";
        /// <summary>Item or block code in the game domain, for the picker icon.</summary>
        public string StackCode = "";
        public bool IsBlock;
        /// <summary>The material texture the boards are tesselated with.</summary>
        public string Texture = "";
    }

    public const string DefaultKey = "default";

    private static readonly string[] LeatherColors =
        { "plain", "brown", "black", "red", "blue", "purple", "pink", "orange", "yellow", "gray", "green", "white" };

    // Cloth's own variant list, minus the two that are not colours (mordant is a
    // dyeing agent, and plain is covered by the leather default's role here).
    private static readonly string[] ClothColors =
        { "blue", "red", "yellow", "green", "purple", "pink", "orange", "brown", "gray", "black", "white" };

    private static readonly string[] WoolTypes = { "white", "brown", "black" };

    private static List<Cover>? all;

    /// <summary>Every binding on offer, in picker order: the default, then leather by
    /// colour, then dyed cloth, then wool.</summary>
    public static List<Cover> All
    {
        get
        {
            if (all != null) return all;
            all = new List<Cover>
            {
                new Cover
                {
                    Key = DefaultKey,
                    Display = "The Almanac's own boards",
                    StackCode = "leather-normal-plain",
                    Texture = "almanacilluminated:shape/book/cover",
                },
            };

            foreach (string c in LeatherColors)
            {
                // Leather's colour list has no "brown": plain IS the undyed hide, and
                // it already heads the list as the default's twin. Skip the duplicate
                // rather than offering a binding that cannot resolve.
                if (c == "brown") continue;
                all.Add(new Cover
                {
                    Key = "leather-" + c,
                    Display = Title(c) + " leather",
                    StackCode = "leather-normal-" + c,
                    // The stitched hide rather than the plain one. Plain leather is
                    // authored to read at block scale in the world and measures a
                    // luminance stdev near 3 across 40-odd colours — blown up to a book
                    // board it reads as flat paint. The running-stitch variant carries
                    // roughly a hundred colours and visible thread, which is both livelier
                    // and more like a bound book (playtest 2026-08-26).
                    //
                    // "stiched" is vanilla's own spelling. Do not correct it.
                    Texture = "game:block/leather/stiched/running/" + c,
                });
            }

            foreach (string c in ClothColors)
            {
                all.Add(new Cover
                {
                    Key = "cloth-" + c,
                    Display = Title(c) + " cloth",
                    StackCode = "cloth-" + c,
                    Texture = "game:block/cloth/linen/" + c,
                });
            }

            foreach (string t in WoolTypes)
            {
                all.Add(new Cover
                {
                    Key = "wool-" + t,
                    Display = Title(t) + " wool",
                    // Wool is a sided decor coating: its code carries the face, and the
                    // creative-inventory representative is the down variant.
                    StackCode = "wool-" + t + "-down",
                    IsBlock = true,
                    Texture = "game:block/cloth/wool/" + t + "1",
                });
            }
            return all;
        }
    }

    public static Cover Resolve(string? key)
    {
        if (!string.IsNullOrEmpty(key))
            foreach (Cover c in All)
                if (c.Key == key) return c;
        return All[0];
    }

    public static AssetLocation TextureOf(string? key) => new(Resolve(key).Texture);

    /// <summary>The stack the picker draws for a binding, or null when the material is
    /// not in this world (a disabled vanilla item, a stripped-down pack). A binding that
    /// cannot show its own material is simply not offered.</summary>
    public static ItemStack? StackFor(ICoreClientAPI capi, Cover cover)
    {
        var loc = new AssetLocation("game", cover.StackCode);
        if (cover.IsBlock)
        {
            Block? block = capi.World.GetBlock(loc);
            return block == null ? null : new ItemStack(block);
        }
        Item? item = capi.World.GetItem(loc);
        return item == null ? null : new ItemStack(item);
    }

    private static string Title(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
