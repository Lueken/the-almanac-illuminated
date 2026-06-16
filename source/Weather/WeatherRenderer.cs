using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace AlmanacIlluminated;

/// <summary>
/// Turns a sampled <see cref="HomeWeather"/> into the almanac's weather pages: warm,
/// hedged prose in a lifelong farmer's voice, not a forecast or a table. The numbers
/// are exact underneath (VS weather is deterministic) but spoken, never tabulated.
/// Latitude sets the seasonal character; the temperature curve sets where summer and
/// the frosts fall, so it reads right in either hemisphere.
/// </summary>
public static class WeatherRenderer
{
    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 1 };
    private static readonly string[] Mn =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    public static List<RichTextComponentBase[]> RenderPages(ICoreClientAPI capi, HomeWeather w, double pageWidth, double pageHeight)
    {
        var heading = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative).WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);
        var body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink);

        var sections = BuildSections(w);
        var blocks = new List<List<RichTextComponentBase>>();
        foreach (var (title, text) in sections)
        {
            var b = new List<RichTextComponentBase>
            {
                new RichTextComponent(capi, title + "\n", heading) { UnscaledMarginTop = 16 },
                new RichTextComponent(capi, text + "\n", body),
            };
            blocks.Add(b);
        }
        return Pack(capi, blocks, pageWidth, pageHeight);
    }

    /// <summary>The generated almanac sections (title, prose) — exposed so a sweep can log them.</summary>
    internal static List<(string title, string text)> BuildSections(HomeWeather w)
    {
        var list = new List<(string, string)>
        {
            ("The Year's Temper", YearsTemper(w)),
            ("Rain and Drought", RainAndDrought(w)),
            ("Frost and the Growing Season", FrostAndSeason(w)),
        };
        string? snow = SnowNote(w);
        if (snow != null) list.Add(("Of Snow", snow));
        return list;
    }

    /// <summary>
    /// The land's seasonal character, taken from the actual climate it sees — the
    /// coldest month and the year's swing — not latitude alone, so a cold highland
    /// near the equator reads as the hard country it is, and the character never
    /// contradicts the frost section below it.
    /// </summary>
    private static string YearsTemper(HomeWeather w)
    {
        float swing = w.SeasonalSwing;
        float winter = w.MonthMeanTemp[w.ColdestMonth];
        string warm = Mn[w.WarmestMonth];
        string cold = Mn[w.ColdestMonth];
        int hot = (int)Math.Round(w.MonthMeanTemp[w.WarmestMonth]);
        int chill = (int)Math.Round(winter);

        string place =
            (swing < 6 && winter > 8) ? "Yours is an even-tempered country near the world's waist, where the seasons barely turn and true cold never comes." :
            winter > 2 ? "Yours is a mild country of gentle seasons, the cold soft and short." :
            winter > -15 ? "Yours is a country of four full seasons, with a real but bearable winter." :
            winter > -25 ? "Yours is a hard country of long, biting winters and short, precious summers." :
            "Yours lies in a frozen country, where deep winter rules and true summer is a brief guest.";

        string crest = swing < 8
            ? $"The warmth holds steady the year round, cresting only gently near {warm} around {hot}°."
            : $"The year crests in high summer near {warm} around {hot}°, and sinks to its floor in the deep of {cold}, when the cold can bite to {chill}°.";

        string turn = swing >= 20
            ? " Spring and autumn turn quickly between the two."
            : "";

        return place + " " + crest + turn;
    }

    private static string RainAndDrought(HomeWeather w)
    {
        float avg = 0;
        foreach (float v in w.MonthWetness) avg += v;
        avg /= 12;

        string overall =
            avg < 0.08 ? "This is a dry country; the skies give little." :
            avg < 0.20 ? "Rain comes in fair measure here, neither stingy nor generous." :
            "This is a wet country; count on the skies more often than not.";

        string when = w.WettestMonth == w.DriestMonth
            ? ""
            : $" Look for the wettest skies to gather around {Mn[w.WettestMonth]}, while {Mn[w.DriestMonth]} runs the driest.";

        return overall + when;
    }

    private static string FrostAndSeason(HomeWeather w)
    {
        if (w.FrostFreeYear)
            return "No frost troubles this ground. Sow when you please; the cold will not take your tender plants.";
        if (w.FrostBoundYear)
            return "The frost never wholly lifts here. Only the hardiest roots and the patient glasshouse will reward a planter.";

        int months = (int)Math.Round(w.GrowingSeasonMonths);
        string len = months <= 3 ? "a short season, time only for the quickest crops"
                   : months <= 6 ? "a fair season, time enough for most things but not the slowest"
                   : "a long, generous season";

        // Defensive: if the bounding frost dates didn't resolve, speak only to the season's length.
        if (w.LastFrostMonth < 0 || w.FirstFrostMonth < 0)
            return $"Frost holds for part of the year here, leaving {len} of open ground — roughly {months} months. Watch the cold rather than the calendar before trusting tender plants outside.";

        return $"Hold your tender seed until the last frost lifts, near {Mn[w.LastFrostMonth]}; the first hard frost steals back around {Mn[w.FirstFrostMonth]}. " +
               $"Between them lies {len} — roughly {months} months of open ground.";
    }

    private static string? SnowNote(HomeWeather w)
    {
        int firstSnow = -1, lastSnow = -1;
        for (int m = 0; m < 12; m++)
            if (w.MonthSnowShare[m] >= 0.5f && w.MonthWetness[m] > 0.03f)
            {
                if (firstSnow < 0) firstSnow = m;
                lastSnow = m;
            }
        if (firstSnow < 0) return null;
        if (firstSnow == 0 && lastSnow == 11)
            return "Snow lies on this ground the better part of the year. Keep the paths cleared and the woodpile high.";
        return $"Look for snow to lie from about {Mn[w.ColdestMonth]}'s cold, through the heart of winter, melting back as the warm months return.";
    }

    /// <summary>Pack whole sections into page-height columns; a section moves to the next page rather than splitting.</summary>
    private static List<RichTextComponentBase[]> Pack(ICoreClientAPI capi, List<List<RichTextComponentBase>> blocks, double pageWidth, double pageHeight)
    {
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double availH = pageHeight * scale;

        var pages = new List<RichTextComponentBase[]>();
        var current = new List<RichTextComponentBase>();
        void Flush() { if (current.Count > 0) { pages.Add(current.ToArray()); current = new List<RichTextComponentBase>(); } }

        foreach (var block in blocks)
        {
            var trial = new List<RichTextComponentBase>(current);
            trial.AddRange(block);
            if (current.Count == 0 || ChapterRenderer.MeasureHeight(capi, trial.ToArray(), pageWidth) <= availH)
                current = trial;
            else { Flush(); current.AddRange(block); }
        }
        Flush();
        if (pages.Count == 0) pages.Add(Array.Empty<RichTextComponentBase>());
        return pages;
    }
}
