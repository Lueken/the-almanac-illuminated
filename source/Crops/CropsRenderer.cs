using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace AlmanacIlluminated;

/// <summary>
/// Lays the crop catalogue out as one card per growable, flowing across the book's
/// two pages. Each card carries the produce icon, name, source (vanilla or which
/// mod), the growing facts, and the planting line for the player's home climate.
/// Cards are kept whole and packed by measured height, the same hybrid approach the
/// chapter paginator uses, so the Crops tab page-turns like any chapter.
///
/// With a crop-familiarity supplier registered, the seed crops regroup under their
/// plant families and each card says only as much as the reader has earned: figures
/// once Versed, the shape of the need in words once Acquainted, and a named blank
/// before that. Without one, the tab is the plain catalogue it has always been. The
/// two states share every font, measurement and card shape on purpose, because it is
/// meant to read as the same book either way.
/// </summary>
public static class CropsRenderer
{
    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 1 };
    private static readonly double[] Muted = { 0.42, 0.36, 0.28, 1 };
    private static readonly double[] Faint = { 0.32, 0.27, 0.21, 0.62 };

    // The sowing accent. #4f6338, the same field green The Turned Earth declares, so the
    // two surfaces about growing things speak with one colour.
    private static readonly double[] Sown = { 0.31, 0.39, 0.22, 1.0 };

    public static List<RichTextComponentBase[]> RenderPages(ICoreClientAPI capi, List<CropEntry> entries,
        BlockPos? homebase, double pageWidth, double pageHeight,
        ICropFamiliaritySource? familiarity = null, GrowthMarksStyle marks = GrowthMarksStyle.Tally)
    {
        var f = new Fonts
        {
            Name = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink),
            NameFaint = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative).WithColor(Faint),
            Body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink),
            Muted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted),
            Window = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithSlant(Cairo.FontSlant.Italic).WithColor(Ink),
            Voice = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithSlant(Cairo.FontSlant.Italic).WithColor(Muted),
            Sow = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithWeight(Cairo.FontWeight.Bold).WithColor(Sown),
            Header = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative).WithWeight(Cairo.FontWeight.Bold).WithFontSize(19f).WithColor(Ink),
            Callout = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink),
        };

        bool useFam = familiarity != null && familiarity.Enabled;
        var blocks = useFam
            ? BuildByFamily(capi, entries, homebase, familiarity!, marks, f)
            : BuildPlain(capi, entries, homebase, f);

        return PackCards(capi, blocks, pageWidth, pageHeight);
    }

    private sealed class Fonts
    {
        public CairoFont Name = null!, NameFaint = null!, Body = null!, Muted = null!, Sow = null!,
                         Window = null!, Voice = null!, Header = null!, Callout = null!;
    }

    // ------------------------------------------------------------ ordering

    /// <summary>The catalogue as Illuminated shows it alone: four kind groups, each by name.</summary>
    private static List<List<RichTextComponentBase>> BuildPlain(ICoreClientAPI capi, List<CropEntry> entries,
        BlockPos? homebase, Fonts f)
    {
        var ordered = new List<CropEntry>(entries);
        ordered.Sort(ByGroupThenName);

        var blocks = new List<List<RichTextComponentBase>>();
        CropKind? lastKind = null;
        List<RichTextComponentBase>? pending = null;
        foreach (var e in ordered)
        {
            if (lastKind == null || e.Kind != lastKind)
            {
                pending = Heading(capi, GroupTitle(e.Kind), lastKind != null, f);
                lastKind = e.Kind;
            }
            blocks.Add(Join(pending, BuildCard(capi, e, homebase, f, null, null, GrowthMarksStyle.Tally)));
            pending = null;
        }
        return blocks;
    }

    /// <summary>
    /// The catalogue as a record of the reader's own growing: seed crops under their
    /// families in the taxonomy's order, then the untracked crops, then the perennials,
    /// which no one sows and familiarity therefore says nothing about.
    /// </summary>
    private static List<List<RichTextComponentBase>> BuildByFamily(ICoreClientAPI capi, List<CropEntry> entries,
        BlockPos? homebase, ICropFamiliaritySource fam, GrowthMarksStyle marks, Fonts f)
    {
        var standing = new Dictionary<CropEntry, CropStanding>();
        var byFamily = new Dictionary<string, List<CropEntry>>(StringComparer.OrdinalIgnoreCase);
        var untracked = new List<CropEntry>();
        var perennials = new List<CropEntry>();

        foreach (var e in entries)
        {
            if (e.Kind != CropKind.SeedCrop)
            {
                // Perennials are grouped by kind rather than by family, but they still get a
                // standing if the supplier tracks them. A standing with an empty FamilyId is
                // the normal case here and must NOT fall through to `untracked` the way it
                // does for a sown crop: these have no family by design, not by omission.
                perennials.Add(e);
                var pst = fam.Of(e);
                if (pst != null) standing[e] = pst;
                continue;
            }
            var st = fam.Of(e);
            if (st == null || string.IsNullOrEmpty(st.FamilyId)) { untracked.Add(e); continue; }
            standing[e] = st;
            if (!byFamily.TryGetValue(st.FamilyId, out var list)) byFamily[st.FamilyId] = list = new List<CropEntry>();
            list.Add(e);
        }

        var blocks = new List<List<RichTextComponentBase>>();
        bool anyBefore = false;

        foreach (var family in fam.Families)
        {
            if (!byFamily.TryGetValue(family.Id, out var members) || members.Count == 0) continue;
            members.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            var head = FamilyHeading(capi, family, fam.Spread, anyBefore, f);
            anyBefore = true;
            foreach (var e in members)
            {
                blocks.Add(Join(head, BuildCard(capi, e, homebase, f, standing[e], family, marks)));
                head = null;
            }
        }

        if (untracked.Count > 0)
        {
            untracked.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            var head = Heading(capi, "OTHER CROPS", anyBefore, f);
            head.Add(new RichTextComponent(capi, "No family is recorded for these, so nothing you grow warms them.\n", f.Voice));
            anyBefore = true;
            foreach (var e in untracked)
            {
                blocks.Add(Join(head, BuildCard(capi, e, homebase, f, null, null, marks)));
                head = null;
            }
        }

        perennials.Sort(ByGroupThenName);
        CropKind? lastKind = null;
        List<RichTextComponentBase>? pending = null;
        foreach (var e in perennials)
        {
            if (lastKind == null || e.Kind != lastKind)
            {
                pending = Heading(capi, GroupTitle(e.Kind), anyBefore, f);
                pending.Add(new RichTextComponent(capi, GroupNote(e.Kind), f.Voice));
                lastKind = e.Kind;
                anyBefore = true;
            }
            standing.TryGetValue(e, out var pst);
            blocks.Add(Join(pending, BuildCard(capi, e, homebase, f, pst, null, marks)));
            pending = null;
        }

        return blocks;
    }

    private static int ByGroupThenName(CropEntry a, CropEntry b)
    {
        int g = GroupOrder(a.Kind).CompareTo(GroupOrder(b.Kind));
        return g != 0 ? g : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Glues a heading onto the card that follows it, so the packer can never
    /// strand a family name alone at the foot of a column.</summary>
    private static List<RichTextComponentBase> Join(List<RichTextComponentBase>? head, List<RichTextComponentBase> card)
    {
        if (head == null || head.Count == 0) return card;
        var joined = new List<RichTextComponentBase>(head.Count + card.Count);
        joined.AddRange(head);
        joined.AddRange(card);
        return joined;
    }

    private static int GroupOrder(CropKind k) => k switch
    {
        CropKind.SeedCrop => 0,
        CropKind.Mushroom => 1,
        CropKind.BerryBush => 2,
        CropKind.FruitTree => 3,
        _ => 4,
    };

    private static string GroupTitle(CropKind k) => k switch
    {
        CropKind.SeedCrop => "CROPS",
        CropKind.Mushroom => "MUSHROOMS",
        CropKind.BerryBush => "BUSHES",
        CropKind.FruitTree => "FRUIT TREES",
        _ => "OTHER",
    };

    /// <summary>
    /// Said once under a perennial heading and never on the cards beneath it.
    ///
    /// The bush and tree notes used to explain why their pages stood open, because nothing in
    /// those groups could be learned. Fruit familiarity made that false, so they now say the
    /// rule that replaced it: a perennial is learned by PROPAGATING it, not by picking it.
    /// That one sentence covers both gates. An orchard needs it said because wild trees can be
    /// picked forever; a bush needs it said because a cutting only counts off one somebody has
    /// already raised.
    ///
    /// The mushroom note is untouched and still true. Nothing plants a mushroom, so nothing
    /// about it is learned, and that group's pages do still stand open for the old reason.
    /// </summary>
    private static string GroupNote(CropKind k) => k switch
    {
        CropKind.Mushroom =>
            "None of what follows is planted. It is only found, and what is found is never learned by sowing.\n",
        CropKind.BerryBush =>
            "A bush is known by raising one. Cuttings taken off a bush you set yourself are what fill these pages.\n",
        CropKind.FruitTree =>
            "An orchard is known by planting it. A tree stays a stranger, however much of it you pick, until you have rooted or grafted a cutting of your own.\n",
        _ => "\n",
    };

    // ------------------------------------------------------------ headings

    private static List<RichTextComponentBase> Heading(ICoreClientAPI capi, string title, bool spaceAbove, Fonts f) =>
        new()
        {
            new RichTextComponent(capi, title + "\n", f.Header) { UnscaledMarginTop = spaceAbove ? 22 : 0 },
        };

    /// <summary>
    /// A family's name, one line on how knowledge moves inside it, and the taxonomy's own
    /// admission where the grouping is not the obvious one. Where the family-wide reading
    /// has opened, the callout says so plainly: that one is a sense the reader now has out
    /// in the world, not a fact about this page.
    /// </summary>
    private static List<RichTextComponentBase> FamilyHeading(ICoreClientAPI capi, CropFamilyStanding fam,
        double spread, bool spaceAbove, Fonts f)
    {
        var head = Heading(capi, fam.Name.ToUpperInvariant(), spaceAbove, f);

        // The family break. Its own mark, not the Callings tab's diamond: both tabs group
        // things, but a reader who learns the diamond means a strand of callings should not
        // meet it again meaning a family of plants. Without any rule at all a page of
        // families read as one column of text with bold words in it.
        head.Add(new SownRuleComponent(capi, 12));
        head.Add(new ClearFloatTextComponent(capi, 3));

        head.Add(new RichTextComponent(capi, FamilyVoice(fam, spread) + "\n", f.Voice));

        if (!string.IsNullOrEmpty(fam.Note))
            head.Add(new RichTextComponent(capi, fam.Note + "\n", f.Muted));

        if (fam.MemoryAvailable)
        {
            head.Add(new RichTextComponent(capi, "THE ROTATION MEMORY\n", f.Callout) { UnscaledMarginTop = 6 });
            head.Add(new RichTextComponent(capi, "On any ground you walk, you can tell what it last bore.\n", f.Voice));
        }
        return head;
    }

    private static string FamilyVoice(CropFamilyStanding fam, double spread)
    {
        if (fam.OfOne) return "It stands alone in its family, so nothing you grow elsewhere will warm it.";
        if (fam.Sum <= 0) return "Nothing here has been grown. The whole family is strange to you.";
        if (fam.MemoryAvailable) return "You have grown enough of this family that the ground itself remembers.";
        // Say how far it actually carries. A retuned spread must not leave the page lying.
        string far = Math.Abs(spread - 0.5) < 0.01 ? "halfway" : "part of the way";
        return $"What you learn of one carries {far} into the rest.";
    }

    // ------------------------------------------------------------ cards

    private static List<RichTextComponentBase> BuildCard(ICoreClientAPI capi, CropEntry e, BlockPos? homebase,
        Fonts f, CropStanding? st, CropFamilyStanding? fam, GrowthMarksStyle marks)
    {
        var comps = new List<RichTextComponentBase>();

        float know = ForagersGambleKnowledge.Progress(capi, e.ProduceCode);
        bool identified = know >= 1f;

        // Unidentified: a masked silhouette card, no facts. Identify it by eating to reveal.
        // Foragers Gamble is the only thing that can put a card here; with FG absent every
        // crop reads identified, so this path stays folded shut and familiarity does the
        // gating on its own.
        if (know <= 0f)
        {
            comps.Add(new SilhouetteComponent(capi, 40));
            comps.Add(new RichTextComponent(capi, MaskedName(e.Kind) + "  ", f.Name));
            comps.Add(new RichTextComponent(capi, KindLabel(e.Kind) + "\n", f.Muted));
            comps.Add(new RichTextComponent(capi, "Not yet identified\n", f.Window));
            comps.Add(new ClearFloatTextComponent(capi, 15));
            return comps;
        }

        bool stranger = st != null && st.Tier == CropTier.Stranger;

        // Card shape, fixed for every tier so a page of them reads as one hand:
        //   the name, with its source shadowed beside it
        //   the record of what you have grown, struck directly beneath
        //   one line of facts: what it stands, how long it holds, what it feeds on
        //   one line on when to sow it where you live
        // A tier removes lines from the bottom; it never rearranges them. The kind is not
        // labelled, because every card sits under a heading that already names it.
        if (e.Produce != null)
            comps.Add(new ItemstackTextComponent(capi, FreshIcon(capi, e.Produce), 40, 6, EnumFloat.Left));

        // The source label used to read "vanilla" for anything in the base game's domain, and
        // that was a claim the code could not back. A mod may ship its blocktypes under
        // assets/game/ and register straight into the base domain, which is exactly what
        // Mycodiversity does: all 36 of its mushrooms carry domain "game" and every one of them
        // was captioned "vanilla". Nor can it be repaired by looking harder. A block holds no
        // record of which mod supplied it, and one blocktype file with variant groups becomes
        // many blocks, so there is no honest path from a Block back to an origin.
        //
        // So the label is now only ever printed when it is true: a domain of its own means a
        // mod, and a mod can be named. Unmarked means the base domain and nothing more is
        // asserted, which is also the older and more legible convention.
        string? source = e.Vanilla ? null : capi.ModLoader.GetMod(e.SourceDomain)?.Info?.Name ?? e.SourceDomain;
        comps.Add(new RichTextComponent(capi, e.DisplayName + (source == null ? "\n" : "  "),
            stranger ? f.NameFaint : f.Name));
        if (source != null) comps.Add(new RichTextComponent(capi, source + "\n", f.Muted));

        if (st != null) AddProgress(capi, comps, st, marks, f);

        if (e.Kind == CropKind.Mushroom)
        {
            // The group note above says the one thing true of all 81 of them. What belongs HERE
            // is only what differs species to species, and until now nothing did, because
            // BuildMushrooms read a name and an icon and asked the block for nothing else.
            //
            // Still no figures line and still no planting line: both temperature bounds are NaN
            // by construction, so GrowingLine returns empty and PlantingWindow would fall back
            // to a bare grow floor with no ceiling and report a season derived from nothing
            // about the mushroom. The habitat band that would fill them lives in
            // worldgen/blockpatches, which is a server-only asset category and unreachable from
            // a client-side renderer without a packet.
            string warn = WarningLine(e);
            if (warn.Length > 0) comps.Add(new RichTextComponent(capi, warn + "\n", f.Voice));

            // The habitat and ground lines need the forage atlas, which only exists once the
            // server has answered. Absent it the card keeps the warning line and stops, which
            // is the same graceful degradation the familiarity supplier gets.
            var row = AlmanacIlluminatedModSystem.Instance?.ForageRowFor(e.ProduceCode);
            if (row != null)
            {
                string habitat = HabitatLine(row, AlmanacIlluminatedModSystem.Instance!.AtlasMedianRain, e);
                if (habitat.Length > 0)
                    comps.Add(new RichTextComponent(capi, habitat + "\n", f.Muted));

                // Takes the sowing accent, because it is the same promise in the same colour
                // as a crop's planting line: this is what your own ground will do.
                comps.Add(new RichTextComponent(capi, GroundLine(capi, row, homebase) + "\n", f.Sow));
            }
        }
        else if (st == null || st.Tier == CropTier.Versed)
        {
            // No familiarity for this entry, or all of it earned: the figures, then the sowing.
            comps.Add(new RichTextComponent(capi, GrowingLine(e, true) + "\n", f.Muted));
            comps.Add(new RichTextComponent(capi, PlantingLine(capi, e, homebase) + "\n", f.Sow));
        }
        else if (st.Tier == CropTier.Acquainted)
        {
            comps.Add(new RichTextComponent(capi, ShapeLine(capi, e) + "\n", f.Muted));
            comps.Add(new RichTextComponent(capi, PlantingLine(capi, e, homebase) + "\n", f.Sow));
        }
        else
        {
            // The kin line cannot apply to a perennial: it carries no family, so EffectiveCount
            // is its own count and KinOnly can never be true. Kept as an explicit kind test
            // rather than relying on that, because the reason is not visible from here.
            bool perennial = e.Kind != CropKind.SeedCrop;
            comps.Add(new RichTextComponent(capi, (!perennial && st.KinOnly
                ? "Never grown. Its kin have taught you a little."
                : StrangerLine(e.Kind)) + "\n", f.Voice));
        }

        // FG mid-learn. Unreachable while FG is absent, and left in so a pack that runs it
        // still sees its own progress rather than nothing.
        if (!identified)
            comps.Add(new RichTextComponent(capi, $"Learning… {(int)(know * 100)}%\n", f.Muted));

        comps.Add(new ClearFloatTextComponent(capi, 15));
        return comps;
    }

    /// <summary>
    /// What the reader has put in the ground, in whichever hand they have asked for. The
    /// gate holds as far as Versed and no further: past that the plant has nothing left to
    /// teach, and a tally that kept growing would be counting for its own sake.
    /// </summary>
    private static void AddProgress(ICoreClientAPI capi, List<RichTextComponentBase> comps,
        CropStanding st, GrowthMarksStyle marks, Fonts f)
    {
        int cap = Math.Max(1, st.VersedAt);

        if (marks == GrowthMarksStyle.Counts)
        {
            string line = st.OwnCount > 0
                ? $"grown {st.OwnCount} · with kin {st.EffectiveCount:0.#} of {cap}"
                : $"never grown · kin {st.EffectiveCount:0.#} of {cap}";
            comps.Add(new RichTextComponent(capi, line + "\n", f.Muted));
            return;
        }

        if (marks == GrowthMarksStyle.Fullness)
        {
            comps.Add(new GrowthMarksComponent(capi, GrowthMarksStyle.Fullness, st.CropId,
                0, 0, cap, st.EffectiveCount / cap));
            comps.Add(new RichTextComponent(capi, "\n", f.Muted));
            return;
        }

        // A tally longer than a margin can hold stops being a tally. Ladders this long are
        // a server's choice, so fall back rather than draw a hundred strokes.
        if (cap > 40)
        {
            comps.Add(new GrowthMarksComponent(capi, GrowthMarksStyle.Fullness, st.CropId,
                0, 0, cap, st.EffectiveCount / cap));
            comps.Add(new RichTextComponent(capi, "\n", f.Muted));
            return;
        }

        int own = Math.Min(st.OwnCount, cap);
        int kin = (int)Math.Round(Math.Max(0, st.EffectiveCount - st.OwnCount), MidpointRounding.AwayFromZero);
        if (own + kin == 0) return;   // nothing has happened; the margin stays empty

        comps.Add(new GrowthMarksComponent(capi, GrowthMarksStyle.Tally, st.CropId, own, kin, cap, 0));
        comps.Add(new RichTextComponent(capi, "\n", f.Muted));
    }

    /// <summary>
    /// A drawing in a book does not go off.
    ///
    /// The catalogue's stacks are built once and held for the session. A perishable stack
    /// carries a `transitionstate` tree recording the world hour it was created, and the game
    /// stamps that on first render, so after a few in-game weeks every plate on the page read
    /// as rotten: spoiled textures, and a hover saying "100% spoiled, 0 sat". Passing a season
    /// in one sitting turned the whole catalogue to compost.
    ///
    /// Each card draws a clone with that tree stripped, which is the state a freshly made item
    /// is in, so the game stamps it at the current hour as it renders. Rebuilt on every open,
    /// so it never ages. The stack stays a real stack, which is what keeps the hover tooltip:
    /// the two are independent, and fixing the rot costs nothing on the mouseover.
    /// </summary>
    private static ItemStack FreshIcon(ICoreClientAPI capi, ItemStack stack)
    {
        var icon = stack.Clone();
        icon.Attributes?.RemoveAttribute("transitionstate");
        return icon;
    }

    private static string MaskedName(CropKind k) => k switch
    {
        CropKind.SeedCrop => "Unknown crop",
        CropKind.Mushroom => "Unknown mushroom",
        CropKind.BerryBush => "Unknown berry",
        CropKind.FruitTree => "Unknown fruit",
        _ => "Unknown plant",
    };

    private static string KindLabel(CropKind k) => k switch
    {
        CropKind.SeedCrop => "crop",
        CropKind.Mushroom => "mushroom",
        CropKind.BerryBush => "bush",
        CropKind.FruitTree => "fruit tree",
        _ => "plant",
    };

    private static string PlantingLine(ICoreClientAPI capi, CropEntry e, BlockPos? homebase) =>
        homebase != null ? PlantingWindow.Compute(capi, homebase, e).Summary() : "Set a homebase to see planting times";

    /// <summary>
    /// The dry facts: temperature tolerance, time to grow, and (only once fully known,
    /// matching FG hiding nutrition until learned) the soil need.
    /// </summary>
    private static string GrowingLine(CropEntry e, bool known)
    {
        var parts = new List<string>();

        if (!float.IsNaN(e.ColdDamageBelow) && !float.IsNaN(e.HeatDamageAbove))
            parts.Add($"{e.ColdDamageBelow:0}° to {e.HeatDamageAbove:0}°");
        else if (!float.IsNaN(e.ColdDamageBelow))
            parts.Add($"hardy to {e.ColdDamageBelow:0}°");

        if (e.Kind == CropKind.SeedCrop && e.TotalGrowthDays > 0)
            parts.Add($"{e.TotalGrowthDays:0} days");
        else if (e.Kind == CropKind.FruitTree && e.TotalGrowthDays > 0)
            parts.Add($"{e.TotalGrowthDays:0}-day fruiting");

        if (known && e.Kind == CropKind.SeedCrop)
            parts.Add($"needs {e.Nutrient}");

        // The perennial figures, which are the reason the top tier is worth reaching. None of
        // these appear anywhere in vanilla's own interface, on a hover or in the handbook.
        if (known && e.Kind == CropKind.FruitTree)
        {
            if (e.Evergreen) parts.Add("evergreen");
            else if (!float.IsNaN(e.VernalizationHours))
                parts.Add($"needs {e.VernalizationHours:0}h under {e.VernalizationTemp:0}°");
        }
        else if (known && e.Kind == CropKind.BerryBush)
        {
            if (!float.IsNaN(e.DormantBelow)) parts.Add($"dormant under {e.DormantBelow:0}°");
            if (!float.IsNaN(e.WakesAbove)) parts.Add($"wakes above {e.WakesAbove:0}°");
            if (!float.IsNaN(e.DrawAtBest)) parts.Add($"draws {e.DrawAtBest:0.#} at its best");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The same facts as a grower would say them out loud, for a crop known but not known
    /// exactly: what it feeds on, then whichever of frost or pace it is most worth warning
    /// about. Frost comes first because frost is what kills a crop; pace only matters to a
    /// plan. Never both, because two hedged sentences read as a hedge.
    /// </summary>
    private static string ShapeLine(ICoreClientAPI capi, CropEntry e)
    {
        // A perennial has no CropProps and therefore no soil nutrient, so the hunger sentence
        // below would print a default enum value as though it were a fact about the plant.
        if (e.Kind != CropKind.SeedCrop) return PerennialShapeLine(capi, e);

        string hunger = $"It hungers for {e.Nutrient}.";
        string? second = FrostClause(e) ?? PaceClause(capi, e);
        if (second == null) return hunger;
        return hunger + " " + second;
    }

    /// <summary>
    /// Where a species grows: its temperature band, what it grows on or under, and the ground
    /// it wants. This is the line the whole atlas exists for.
    ///
    /// "Under conifers" is the most useful thing a forage page can say, because it is durably
    /// true and tells a reader which direction to walk. It does not depend on what worldgen
    /// did in this particular world, which is exactly the weakness of the ground line below.
    ///
    /// The wetness clause prints against the atlas's OWN median rather than a fixed number.
    /// Every one of vanilla's 45 species sits at minRain 0.4 with no ceiling, so a constant
    /// threshold would stamp the same clause on more than half the roster and say nothing.
    /// Against the median, a species only speaks up where it actually differs from its peers.
    /// </summary>
    private static string HabitatLine(ForageAtlasRow row, float medianRain, CropEntry e)
    {
        var parts = new List<string>();

        if (row.MinTemp > -90 || row.MaxTemp < 90)
            parts.Add($"{row.MinTemp:0}° to {row.MaxTemp:0}°");

        string tree = row.TreeType.ToLowerInvariant() switch
        {
            "decidious" => "broadleaf",     // vanilla's own spelling of the field value
            "conifer" => "conifer",
            "tropical" => "tropical",
            _ => "",
        };
        string where = row.Placement.ToLowerInvariant() switch
        {
            "ontrees" => tree.Length > 0 ? $"on {tree} trunks" : "on trunks",
            "undertrees" => tree.Length > 0 ? $"under {tree}" : "under trees",
            _ => row.MaxForest >= 0 ? "in the open" : "",
        };
        if (where.Length > 0) parts.Add(where);

        if (row.MaxRain >= 0) parts.Add("needs dry ground");
        else if (row.MinRain >= medianRain + 0.1f) parts.Add("wants wet ground");
        else if (row.MinRain <= medianRain - 0.15f) parts.Add("takes dry ground");
        else if (row.MaxForest >= 0 && where != "in the open") parts.Add("not in deep wood");

        if (e.StandsWinter) parts.Add("stands the winter");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Whether the reader's own ground suits the species, and in which months.
    ///
    /// Worded as CAPABILITY, never as a promise. A blockpatch envelope says where worldgen was
    /// willing to place a species, not where one stands today: a patch can be absent for want
    /// of a host tree or of fertility beneath it, and any world that has been played in has had
    /// forest felled and ground rebuilt since. So this says the ground suits it. Saying "found
    /// near you" over ground that holds none is how a field guide loses a reader's trust, and
    /// promising a specific patch is the Forager's Memory's job, which it can do honestly
    /// because it watched them pick one.
    ///
    /// Rainfall and forest cover are worldgen values rather than seasonal ones, so they are a
    /// single pass-or-fail and are checked first; only temperature varies month to month.
    /// </summary>
    private static string GroundLine(ICoreClientAPI capi, ForageAtlasRow row, BlockPos? homebase)
    {
        if (homebase == null) return "Set a homebase to see where it grows";

        var core = AlmanacIlluminatedModSystem.Instance;
        float rain = core?.HomeRain ?? -1f;
        float forest = core?.HomeForest ?? -1f;

        if (rain >= 0)
        {
            if (rain < row.MinRain) return "Too dry at your base";
            if (row.MaxRain >= 0 && rain > row.MaxRain) return "Too wet at your base";
        }
        if (forest >= 0 && row.MaxForest >= 0 && forest > row.MaxForest)
            return "Your ground is too wooded";

        // Same twelve-month sample the planting window takes, against the species' own band.
        var pos = homebase.Copy();
        int surfaceY = capi.World.BlockAccessor.GetTerrainMapheightAt(pos);
        if (surfaceY > 0) pos.Y = surfaceY;
        double dpm = capi.World.Calendar.DaysPerMonth;

        var ok = new bool[12];
        bool anyWarmer = false, anyCooler = false;
        for (int m = 0; m < 12; m++)
        {
            var cc = capi.World.BlockAccessor.GetClimateAt(
                pos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, (m + 0.5) * dpm);
            float t = cc?.Temperature ?? float.NaN;
            if (float.IsNaN(t)) continue;
            ok[m] = t >= row.MinTemp && t <= row.MaxTemp;
            if (t > row.MaxTemp) anyWarmer = true;
            if (t < row.MinTemp) anyCooler = true;
        }

        string? months = PlantingWindow.RangeString(ok);
        if (months != null)
            return months == "Year-round" ? "Your ground suits it, year-round" : $"Your ground suits it, {months}";

        // Never in band. Name the direction it misses by, which is the actionable half.
        if (anyCooler && !anyWarmer) return "Too cold at your base";
        if (anyWarmer && !anyCooler) return "Too warm at your base";
        return "Your base does not suit it";
    }

    /// <summary>
    /// What a mushroom is worth knowing before you eat it, and nothing else.
    ///
    /// Banded rather than numeric on purpose. A figure invites arithmetic ("I have 14 health,
    /// a -8 is survivable"), and the point of the line is a decision, not a calculation. The
    /// bands come from the pack's own distribution rather than from round numbers: four species
    /// sit at -20 or worse and will kill outright, ten cluster from -6 to -10, fourteen sit
    /// between -1 and -2.5 where the danger is only quantity, and the remaining fifty-three are
    /// ordinary food.
    ///
    /// Visions are a separate clause because two of the six psychedelic species are poisonous
    /// as well, and a card that printed only one of those would be the dangerous kind of wrong.
    ///
    /// Standing through winter earns its place for a forager rather than an eater: it is the
    /// difference between a species worth walking out for in the cold and one that is simply
    /// gone. Both halves of the test are required, so tolerating deep cold alone does not
    /// claim permanence.
    /// </summary>
    private static string WarningLine(CropEntry e)
    {
        var parts = new List<string>();

        if (e.Toxicity <= -20) parts.Add("Deadly.");
        else if (e.Toxicity <= -5) parts.Add("Poisonous.");
        else if (e.Toxicity < 0) parts.Add("Sickening in quantity.");

        if (e.Psychedelic > 0) parts.Add("Brings visions.");
        if (e.StandsWinter) parts.Add("It stands the winter.");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// What a stranger's page promises, in the terms of the thing being grown.
    ///
    /// A sown crop is grown from seed and the promise is a harvest. A perennial is grown from
    /// a piece of another plant, and both its ladders start at propagation rather than at a
    /// pick: an orchard because wild trees can be picked forever, a bush because a cutting only
    /// counts off one somebody has already raised. So the sentence names the act that actually
    /// opens the page instead of a generic one that would be false for two thirds of the tab.
    /// </summary>
    private static string StrangerLine(CropKind k) => k switch
    {
        CropKind.FruitTree => "A stranger. Root a cutting of your own and the page begins.",
        CropKind.BerryBush => "A stranger. Set a cutting of your own and the page begins.",
        _ => "A stranger. Grow it once and the page begins.",
    };

    /// <summary>
    /// The Acquainted voice for a perennial, which cannot use the sown-crop one: a fruit tree
    /// has no soil nutrient at all, so "It hungers for N" would be inventing a fact.
    ///
    /// Words only, no figures. That split is the whole point of the middle tier, and it is why
    /// the vernalization sentence says "a real winter" here and prints hours at Versed.
    /// </summary>
    private static string PerennialShapeLine(ICoreClientAPI capi, CropEntry e)
    {
        var parts = new List<string>();

        if (e.Kind == CropKind.FruitTree)
        {
            parts.Add(e.Evergreen ? "Evergreen." : "Deciduous.");
            // The single most opaque fact an orchard has: a deciduous fruit tree banks cold
            // hours and will not flower until it has them, and vanilla says so nowhere.
            if (!e.Evergreen && !float.IsNaN(e.VernalizationHours))
                parts.Add("It will not flower until it has had a real winter.");
            else if (e.Evergreen)
                parts.Add("It fruits without waiting on a winter.");
        }
        else if (e.Kind == CropKind.BerryBush)
        {
            if (!float.IsNaN(e.DormantBelow)) parts.Add("It sleeps through the cold and is late to wake.");
            if (!float.IsNaN(e.DrawAtBest))
                parts.Add(e.DrawAtBest <= 5 ? "It is a light feeder." : "It is a heavy feeder.");
        }

        string? frost = FrostClause(e) ?? PaceClause(capi, e);
        if (frost != null) parts.Add(frost);
        return parts.Count == 0 ? "You know its habits." : string.Join(" ", parts);
    }

    private static string? FrostClause(CropEntry e)
    {
        if (float.IsNaN(e.ColdDamageBelow)) return null;
        if (e.ColdDamageBelow <= -8) return "It stands hard frost.";
        if (e.ColdDamageBelow <= -1) return "It takes a light frost.";
        if (e.ColdDamageBelow >= 2) return "It wants warmth.";
        return null;
    }

    private static string? PaceClause(ICoreClientAPI capi, CropEntry e)
    {
        if (e.TotalGrowthDays <= 0) return null;
        double perMonth = capi.World.Calendar?.DaysPerMonth ?? 9;
        if (perMonth <= 0) return null;
        double months = e.TotalGrowthDays / perMonth;
        if (months >= 5) return "It holds the ground a long while.";
        if (months <= 2.5) return "It comes on quickly.";
        return null;
    }

    /// <summary>Pack whole cards into page-height columns; a card moves to the next page rather than splitting.</summary>
    private static List<RichTextComponentBase[]> PackCards(ICoreClientAPI capi, List<List<RichTextComponentBase>> cards, double pageWidth, double pageHeight)
    {
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double availH = pageHeight * scale;

        var pages = new List<RichTextComponentBase[]>();
        var current = new List<RichTextComponentBase>();
        void Flush() { if (current.Count > 0) { pages.Add(current.ToArray()); current = new List<RichTextComponentBase>(); } }

        foreach (var card in cards)
        {
            var trial = new List<RichTextComponentBase>(current);
            trial.AddRange(card);
            if (current.Count == 0 || ChapterRenderer.MeasureHeight(capi, trial.ToArray(), pageWidth) <= availH)
            {
                current = trial;
            }
            else
            {
                Flush();
                current.AddRange(card);   // a card taller than a page just overflows; cards are short
            }
        }

        Flush();
        if (pages.Count == 0) pages.Add(Array.Empty<RichTextComponentBase>());
        return pages;
    }
}
