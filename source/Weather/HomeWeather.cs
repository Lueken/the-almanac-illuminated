using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace AlmanacIlluminated;

/// <summary>
/// A year's weather outlook for the player's home column, sampled from the live game.
/// VS weather is deterministic — temperature follows a fixed seasonal curve by date
/// and place, and precipitation is seeded noise read by (position, day) — so sampling
/// the current year forward is exact, not a guess. Latitude (equator → pole) sets the
/// seasonal character; the temperature curve sets where summer and the frosts fall.
///
/// Both temperature and the forecast precipitation come from one cheap, never-null
/// climate read (ForSuppliedDate_TemperatureRainfallOnly), so no weather-system
/// reference is needed: the weather mod injects the precip into that climate value.
/// </summary>
public sealed class HomeWeather
{
    public double Latitude;          // signed −1..1; sign is the hemisphere, magnitude 0=equator..1=pole
    public bool North;

    public float[] MonthMeanTemp = new float[12];
    public float[] MonthMinTemp = new float[12];
    public float[] MonthMaxTemp = new float[12];
    public float[] MonthWetness = new float[12];     // mean precip intensity 0..1
    public float[] MonthSnowShare = new float[12];   // of that month's precip, the share falling at/below freezing

    public int WarmestMonth, ColdestMonth, WettestMonth, DriestMonth;
    public float YearMinTemp = float.MaxValue, YearMaxTemp = float.MinValue;

    public int LastFrostMonth = -1;   // last frost before the growing season
    public int FirstFrostMonth = -1;  // first frost after the growing season
    public float GrowingSeasonMonths; // length of the frost-free season
    public bool FrostFreeYear;        // never freezes
    public bool FrostBoundYear;       // freezes the whole year

    /// <summary>How far the year swings, warmest mean month minus coldest mean month — small near the equator.</summary>
    public float SeasonalSwing => MonthMeanTemp[WarmestMonth] - MonthMeanTemp[ColdestMonth];

    public static HomeWeather Sample(ICoreClientAPI capi, BlockPos home)
    {
        var w = new HomeWeather();

        var pos = home.Copy();
        int surfaceY = capi.World.BlockAccessor.GetTerrainMapheightAt(pos);
        if (surfaceY > 0) pos.Y = surfaceY;

        w.Latitude = capi.World.Calendar.OnGetLatitude(pos.Z);
        w.North = w.Latitude >= 0;

        double dpm = capi.World.Calendar.DaysPerMonth;
        int year = Math.Max(12, (int)Math.Round(dpm * 12));
        double yearLen = dpm * 12;
        double yearStart = Math.Floor(capi.World.Calendar.TotalDays / yearLen) * yearLen;

        var sum = new double[12];
        var cnt = new int[12];
        var wet = new double[12];
        var snowWet = new double[12];
        for (int m = 0; m < 12; m++) { w.MonthMinTemp[m] = float.MaxValue; w.MonthMaxTemp[m] = float.MinValue; }

        var dayTemp = new float[year];
        bool anyFrost = false, anyAbove = false;

        for (int d = 0; d < year; d++)
        {
            double totalDays = yearStart + d + 0.5;
            int m = Math.Min(11, (int)(d / dpm));

            var cc = capi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.ForSuppliedDate_TemperatureRainfallOnly, totalDays);
            float t = cc?.Temperature ?? 4f;
            float p = cc?.Rainfall ?? 0f;

            dayTemp[d] = t;
            sum[m] += t; cnt[m]++;
            if (t < w.MonthMinTemp[m]) w.MonthMinTemp[m] = t;
            if (t > w.MonthMaxTemp[m]) w.MonthMaxTemp[m] = t;
            wet[m] += p;
            if (t <= 0) snowWet[m] += p;

            if (t < 0) anyFrost = true; else anyAbove = true;
            w.YearMinTemp = Math.Min(w.YearMinTemp, t);
            w.YearMaxTemp = Math.Max(w.YearMaxTemp, t);
        }

        for (int m = 0; m < 12; m++)
        {
            w.MonthMeanTemp[m] = cnt[m] > 0 ? (float)(sum[m] / cnt[m]) : 4f;
            w.MonthWetness[m] = cnt[m] > 0 ? (float)(wet[m] / cnt[m]) : 0f;
            w.MonthSnowShare[m] = wet[m] > 0 ? (float)(snowWet[m] / wet[m]) : 0f;
        }

        w.WarmestMonth = ArgExtreme(w.MonthMeanTemp, max: true);
        w.ColdestMonth = ArgExtreme(w.MonthMeanTemp, max: false);
        w.WettestMonth = ArgExtreme(w.MonthWetness, max: true);
        w.DriestMonth = ArgExtreme(w.MonthWetness, max: false);

        // The growing season is the longest frost-free run of days, found circularly so
        // it reads right in either hemisphere (the warm season can straddle the year
        // boundary). The frost that bounds it gives the last and first frost dates.
        w.FrostFreeYear = !anyFrost;
        w.FrostBoundYear = !anyAbove;
        if (anyFrost && anyAbove)
        {
            int anchor = 0;
            for (int d = 0; d < year; d++) if (dayTemp[d] < 0) { anchor = d; break; }   // start on a frost day

            int bestStart = anchor, bestLen = 0, curStart = -1, curLen = 0;
            for (int i = 0; i < year; i++)
            {
                int idx = (anchor + i) % year;
                if (dayTemp[idx] >= 0)
                {
                    if (curLen == 0) curStart = idx;
                    curLen++;
                    if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; }
                }
                else curLen = 0;
            }

            int lastDay = (bestStart - 1 + year) % year;
            int firstDay = (bestStart + bestLen) % year;
            w.LastFrostMonth = Math.Min(11, (int)(lastDay / dpm));
            w.FirstFrostMonth = Math.Min(11, (int)(firstDay / dpm));
            w.GrowingSeasonMonths = (float)(bestLen / dpm);
        }

        return w;
    }

    private static int ArgExtreme(float[] vals, bool max)
    {
        int idx = 0;
        for (int i = 1; i < vals.Length; i++)
            if (max ? vals[i] > vals[idx] : vals[i] < vals[idx]) idx = i;
        return idx;
    }
}
