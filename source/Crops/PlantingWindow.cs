using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace AlmanacIlluminated;

/// <summary>
/// When and whether a plant is worth growing at a given place, from that place's own
/// yearly temperature curve (sampled at the surface, not underground). A seed crop
/// reports a plant-by window: the months a fresh planting both survives and ripens
/// before the cold. A perennial (bush or fruit tree) reports hardiness and fruiting
/// season instead, since it is planted once and lives for years — a tropical fruit
/// tree at a cold base reads as frost-risk, a hardy one as simply fruiting in season.
/// </summary>
public sealed class PlantingWindow
{
    private const float GrowFloor = 5f;   // plants do not meaningfully grow below this, whatever their cold tolerance

    public readonly float[] MonthTemps;   // index 0 = first month of the year
    public readonly bool[] Growable;
    public readonly bool[] Plantable;
    public readonly int NeedMonths;
    private readonly CropKind kind;
    private readonly float coldLimit;

    private static readonly string[] MonthNames =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    private PlantingWindow(float[] temps, bool[] grow, bool[] plant, int needMonths, CropKind kind, float coldLimit)
    {
        MonthTemps = temps; Growable = grow; Plantable = plant; NeedMonths = needMonths;
        this.kind = kind; this.coldLimit = coldLimit;
    }

    public static PlantingWindow Compute(ICoreClientAPI capi, BlockPos homebase, CropEntry crop)
    {
        // Farming happens at the surface; sample the column top, not the player's Y.
        var pos = homebase.Copy();
        int surfaceY = capi.World.BlockAccessor.GetTerrainMapheightAt(pos);
        if (surfaceY > 0) pos.Y = surfaceY;

        double dpm = capi.World.Calendar.DaysPerMonth;
        var temps = new float[12];
        for (int m = 0; m < 12; m++)
        {
            double totalDays = (m + 0.5) * dpm;   // mid-month; seasonal curve is by day-of-year
            var cc = capi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, totalDays);
            temps[m] = cc?.Temperature ?? float.NaN;
        }

        float floor = Math.Max(GrowFloor, float.IsNaN(crop.ColdDamageBelow) ? GrowFloor : crop.ColdDamageBelow);
        float ceil = float.IsNaN(crop.HeatDamageAbove) ? float.MaxValue : crop.HeatDamageAbove;
        var grow = new bool[12];
        for (int m = 0; m < 12; m++)
            grow[m] = !float.IsNaN(temps[m]) && temps[m] >= floor && temps[m] <= ceil;

        int needMonths = Math.Max(1, (int)Math.Ceiling(crop.TotalGrowthDays / dpm));
        var plant = new bool[12];
        for (int m = 0; m < 12; m++)
        {
            bool ok = true;
            for (int k = 0; k < needMonths && ok; k++)
                if (!grow[(m + k) % 12]) ok = false;   // wrap lets a window cross the year in warm climates
            plant[m] = ok;
        }

        return new PlantingWindow(temps, grow, plant, needMonths, crop.Kind, crop.ColdDamageBelow);
    }

    /// <summary>A short human read: a plant-by window for crops, hardiness + fruiting season for perennials.</summary>
    public string Summary()
    {
        if (kind == CropKind.SeedCrop)
        {
            string? r = RangeString(Plantable);
            if (r == null) return "Not viable at your base";
            return r == "Year-round" ? "Year-round" : "Plant " + r;
        }

        // Perennial: it lives for years, so report whether it fruits here and the season.
        string? season = RangeString(Growable);
        if (season == null) return "Won't fruit at your base";

        string fruits = season == "Year-round" ? "fruits year-round" : $"fruits {season}";
        if (kind == CropKind.FruitTree && !float.IsNaN(coldLimit) && MinTemp() < coldLimit)
            return $"Frost-risk: {fruits}, but dies below {coldLimit:0}°";
        return char.ToUpperInvariant(fruits[0]) + fruits.Substring(1);
    }

    private float MinTemp()
    {
        float min = float.MaxValue;
        foreach (float t in MonthTemps) if (!float.IsNaN(t) && t < min) min = t;
        return min;
    }

    /// <summary>"Year-round", "Apr–Jun", "Apr–Jun, Sep–Oct", or null if no month is flagged.</summary>
    private static string? RangeString(bool[] flags)
    {
        int count = 0;
        foreach (bool f in flags) if (f) count++;
        if (count == 0) return null;
        if (count == 12) return "Year-round";

        int start = 0;
        while (start < 12 && flags[start]) start++;   // begin at a gap so a wrapped run reads as one range

        var ranges = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            int idx = (start + i) % 12;
            if (!flags[idx]) continue;
            int runStart = idx, runEnd = idx;
            while (i + 1 < 12 && flags[(start + i + 1) % 12]) { i++; runEnd = (start + i) % 12; }
            ranges.Add(runStart == runEnd ? MonthNames[runStart] : $"{MonthNames[runStart]}–{MonthNames[runEnd]}");
        }
        return string.Join(", ", ranges);
    }
}
