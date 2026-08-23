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
        public CairoFont Name = null!, NameFaint = null!, Body = null!, Muted = null!,
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
            if (e.Kind != CropKind.SeedCrop) { perennials.Add(e); continue; }
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
                lastKind = e.Kind;
                anyBefore = true;
            }
            blocks.Add(Join(pending, BuildCard(capi, e, homebase, f, null, null, marks)));
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

    // ------------------------------------------------------------ headings

    private static List<RichTextComponentBase> Heading(ICoreClientAPI capi, string title, bool spaceAbove, Fonts f) =>
        new()
        {
            new RichTextComponent(capi, title + "\n", f.Header) { UnscaledMarginTop = spaceAbove ? 14 : 0 },
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
            comps.Add(new ClearFloatTextComponent(capi, 12));
            return comps;
        }

        bool stranger = st != null && st.Tier == CropTier.Stranger;

        if (e.Produce != null)
            comps.Add(new ItemstackTextComponent(capi, e.Produce, 40, 6, EnumFloat.Left));

        string source = e.Vanilla ? "vanilla" : capi.ModLoader.GetMod(e.SourceDomain)?.Info?.Name ?? e.SourceDomain;
        comps.Add(new RichTextComponent(capi, e.DisplayName + "  ", stranger ? f.NameFaint : f.Name));
        comps.Add(new RichTextComponent(capi, $"{KindLabel(e.Kind)} · {source}\n", f.Muted));

        if (e.Kind == CropKind.Mushroom)
        {
            comps.Add(new RichTextComponent(capi, "Foraged, not sown.\n", f.Window));
        }
        else if (st == null)
        {
            // No familiarity for this entry: the plain card, the whole of it.
            comps.Add(new RichTextComponent(capi, GrowingLine(e, true) + "\n", f.Body));
            comps.Add(new RichTextComponent(capi, PlantingLine(capi, e, homebase) + "\n", f.Window));
        }
        else if (st.Tier == CropTier.Versed)
        {
            comps.Add(new RichTextComponent(capi, GrowingLine(e, true) + "\n", f.Body));
            comps.Add(new RichTextComponent(capi, PlantingLine(capi, e, homebase) + "\n", f.Window));
        }
        else if (st.Tier == CropTier.Acquainted)
        {
            comps.Add(new RichTextComponent(capi, ShapeLine(capi, e) + "\n", f.Body));
            comps.Add(new RichTextComponent(capi, PlantingLine(capi, e, homebase) + "\n", f.Window));
        }
        else
        {
            comps.Add(new RichTextComponent(capi, (st.KinOnly
                ? "Never grown. Its kin have taught you a little."
                : "A stranger. Grow it once and the page begins.") + "\n", f.Voice));
        }

        if (st != null) AddProgress(capi, comps, st, marks, f);

        // FG mid-learn. Unreachable while FG is absent, and left in so a pack that runs it
        // still sees its own progress rather than nothing.
        if (!identified)
            comps.Add(new RichTextComponent(capi, $"Learning… {(int)(know * 100)}%\n", f.Muted));

        comps.Add(new ClearFloatTextComponent(capi, 12));
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
        string hunger = $"It hungers for {e.Nutrient}.";
        string? second = FrostClause(e) ?? PaceClause(capi, e);
        if (second == null) return hunger;
        return hunger + " " + second;
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
