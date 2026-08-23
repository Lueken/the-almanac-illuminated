using System;

namespace AlmanacIlluminated;

/// <summary>
/// Maps the raw local brightness reaching the reader (0 = pitch black, 1 = full noon) to the
/// brightness the book renders at (0..1), through a non-linear response curve: darkness is
/// punishing, but a single torch already reads comfortably.
///
/// <para>Curve shape and anchors adapted from the Scribe mod's ScribeBrightnessCurve
/// (https://github.com/EdzyWhat/vintagestory-scribe-libgui — MIT, Copyright (c) 2026 Nick
/// Edises), whose anchors were calibrated against the game's real <c>blockLightLevels</c>
/// table: the table reaches 1.0 at V=26 (open noon), V=20 (large lantern) ≈ 0.814, V=15
/// (torch nearby) ≈ 0.60. The 0.90 knee therefore sits between a large lantern and a very
/// bright source — full book brightness arrives before noon.</para>
///
/// <para>The x=0 anchor's y is the caller-supplied floor (the book's brightness in total
/// darkness), a client preference. Stored points are lifted to <c>max(y, floor)</c> so a
/// raised floor can never bend the assembled curve back down — it stays monotonic for any
/// floor in [0,1]. A floor of 1.0 flattens the curve to always-full-bright (opt out).</para>
/// </summary>
public static class IlluminationCurve
{
    /// <summary>Book brightness at zero light for a reader who never changed it:
    /// dim but faintly legible — you can tell the book is there, not read it.</summary>
    public const float DefaultFloor = 0.05f;

    /// <summary>Lowest allowed floor (effectively unreadable, for the punishing end).</summary>
    public const float MinFloor = 0.02f;

    /// <summary>Highest allowed floor (1.0 = the effect is off).</summary>
    public const float MaxFloor = 1.0f;

    public static float ClampFloor(float floor) => Math.Clamp(floor, MinFloor, MaxFloor);

    /// <summary>The interior + top control points as (localBrightness, bookBrightness) pairs,
    /// ascending x. The x=0 point is not stored — its y is the floor, prepended at
    /// evaluation time.</summary>
    private static readonly (float X, float Y)[] Points =
    {
        (0.45f, 0.53f),
        (0.90f, 1.00f),    // bright torch/lantern nearby → full book brightness
        (1.00f, 1.00f),    // table saturates at 1.0 → flat tail
    };

    /// <summary>Evaluate the response curve at <paramref name="localBrightness"/> (clamped
    /// to 0..1) with <paramref name="floor"/> as the x=0 anchor. Piecewise-linear between
    /// anchors; holds the final value past the last point.</summary>
    public static float Evaluate(float localBrightness, float floor)
    {
        float x = Math.Clamp(localBrightness, 0f, 1f);
        float f = Math.Clamp(floor, 0f, 1f);

        float prevX = 0f;
        float prevY = f;

        for (int i = 0; i < Points.Length; i++)
        {
            float px = Points[i].X;
            // Lift each point's y to the floor so a high floor never creates a downward kink.
            float py = Math.Max(Points[i].Y, f);
            if (x <= px)
            {
                float span = px - prevX;
                if (span <= 0f) return py;
                float t = (x - prevX) / span;
                return prevY + (py - prevY) * t;
            }
            prevX = px;
            prevY = py;
        }

        return prevY;
    }
}
