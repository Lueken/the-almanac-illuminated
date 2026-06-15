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
/// </summary>
public static class CropsRenderer
{
    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 1 };
    private static readonly double[] Muted = { 0.42, 0.36, 0.28, 1 };

    public static List<RichTextComponentBase[]> RenderPages(ICoreClientAPI capi, List<CropEntry> entries, BlockPos? homebase, double pageWidth, double pageHeight)
    {
        var name = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);
        var body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink);
        var muted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted);
        var window = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithSlant(Cairo.FontSlant.Italic).WithColor(Ink);

        var cards = new List<List<RichTextComponentBase>>();
        foreach (var e in entries) cards.Add(BuildCard(capi, e, homebase, name, body, muted, window));
        return PackCards(capi, cards, pageWidth, pageHeight);
    }

    private static List<RichTextComponentBase> BuildCard(ICoreClientAPI capi, CropEntry e, BlockPos? homebase,
        CairoFont name, CairoFont body, CairoFont muted, CairoFont window)
    {
        var comps = new List<RichTextComponentBase>();

        if (e.Produce != null)
            comps.Add(new ItemstackTextComponent(capi, e.Produce, 40, 6, EnumFloat.Left));

        string source = e.Vanilla ? "vanilla" : capi.ModLoader.GetMod(e.SourceDomain)?.Info?.Name ?? e.SourceDomain;
        comps.Add(new RichTextComponent(capi, e.DisplayName + "  ", name));
        comps.Add(new RichTextComponent(capi, $"{KindLabel(e.Kind)} · {source}\n", muted));
        comps.Add(new RichTextComponent(capi, GrowingLine(e) + "\n", body));

        string win = homebase != null ? PlantingWindow.Compute(capi, homebase, e).Summary() : "Set a homebase to see planting times";
        comps.Add(new RichTextComponent(capi, win + "\n", window));

        comps.Add(new ClearFloatTextComponent(capi, 12));
        return comps;
    }

    private static string KindLabel(CropKind k) => k switch
    {
        CropKind.SeedCrop => "crop",
        CropKind.BerryBush => "bush",
        CropKind.FruitTree => "fruit tree",
        _ => "plant",
    };

    /// <summary>The dry facts: temperature tolerance, time to grow, and soil need where it applies.</summary>
    private static string GrowingLine(CropEntry e)
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

        if (e.Kind == CropKind.SeedCrop)
            parts.Add($"needs {e.Nutrient}");

        return string.Join("   ", parts);
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
