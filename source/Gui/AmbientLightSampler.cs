using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace AlmanacIlluminated;

/// <summary>
/// Samples the light reaching the reader each frame and folds it into one smoothed
/// (brightness × warm/cool tint) shade the whole book renders at, so reading by torchlight
/// looks like reading by torchlight. Read on the render/main thread only.
///
/// <para>Adapted from the Scribe mod's ScribeAmbientLightSampler
/// (https://github.com/EdzyWhat/vintagestory-scribe-libgui — MIT, Copyright (c) 2026 Nick
/// Edises). Scribe's quantization/changed-reporting is dropped (it protected a paint cache
/// Illuminated does not have — our shade is applied at draw time, never baked into a
/// composed texture); the sampling model, curve mapping, hue weighting and smoothing are
/// kept, including the Immersive Lanterns flicker pass-through.</para>
///
/// <para><b>Read the engine's LIVE result, don't bake.</b> Every light input is read fresh
/// from an engine source each frame, so any mod that changes the light where the reader
/// stands is honored automatically. Three inputs, as the engine itself combines them:</para>
/// <list type="bullet">
/// <item><c>GetLightRGBs</c> — block-light RGB (torch warmth baked in per block) + the
/// sun-brightness scalar in W.</item>
/// <item><c>Ambient.BlendedAmbientColor</c> / <c>BlendedSceneBrightness</c> — sky/daylight
/// color (absent from the block grid) and weather/rain darkening.</item>
/// <item><b>Held light</b> — the item in either hand via <c>Collectible.GetLightHsv</c>,
/// which the block grid at the reader's own position does NOT contain. Folded in by MAX
/// (VS light is max-based, not additive), with its level mapped through the live
/// <c>BlockLightLevels</c> table and its hue from the item's own <c>lightHsv</c>.</item>
/// </list>
/// </summary>
public sealed class AmbientLightSampler
{
    /// <summary>How much of the raw hue skew to keep (the rest is pulled back to neutral).
    /// At 2/3 warm light still reads warm, just less aggressively (Scribe's author tuning,
    /// kept as the starting point).</summary>
    private const float TintStrength = 2f / 3f;

    /// <summary>Exponential smoothing time-constant (seconds). A first-order ease toward the
    /// current target each frame: ~86% of a step in 400ms, continuous even while the target
    /// keeps moving, so walking sun↔shade glides instead of snapping.</summary>
    private const float SmoothingTau = 0.2f;

    /// <summary>Immersive Lanterns Harmony-patches <c>GetLightHsv</c> to flicker a HELD
    /// torch/lantern's V per frame. We already receive that flicker via TryHeldLight; the
    /// only thing that would erase it is our own smoothing, so when IL is present the held
    /// brightness term bypasses the ease and rides on top unsmoothed.</summary>
    private const string FlickerModId = "immersivelanterns";

    private readonly ICoreClientAPI capi;
    private readonly Func<float> floorPref;

    private bool hasSmoothed;
    private float smoothedBrightness;
    private float smoothedR = 1f, smoothedG = 1f, smoothedB = 1f;

    // Resolved once and cached — mod enablement can't change mid-session.
    private bool? flickerModActive;

    public AmbientLightSampler(ICoreClientAPI capi, Func<float> floorPref)
    {
        this.capi = capi;
        this.floorPref = floorPref;
    }

    /// <summary>The shade to render this frame: final per-channel multipliers
    /// (brightness × tint, each 0..1; 1,1,1 = leave the book alone).</summary>
    public readonly struct Shade
    {
        public readonly float R, G, B;
        public Shade(float r, float g, float b) { R = r; G = g; B = b; }
        public static readonly Shade Neutral = new(1f, 1f, 1f);
    }

    /// <summary>Forget the smoothed state, so the next open adopts the current light
    /// directly instead of gliding from wherever the book was last closed.</summary>
    public void ResetSmoothing() => hasSmoothed = false;

    /// <summary>Sample the light at the reader, fold it to a smoothed <see cref="Shade"/>.
    /// Must run on the render/main thread; <paramref name="dt"/> is the frame time.</summary>
    public Shade Sample(float dt)
    {
        var world = capi.World;
        var entity = world?.Player?.Entity;
        if (world is null || entity is null)
        {
            // No player/world yet (rare, during load) — neutral, never blank the book.
            return Smooth(1f, 0f, 1f, 1f, 1f, dt);
        }

        BlockPos pos = entity.Pos.AsBlockPos;

        // (1) Block-light RGB (XYZ) + sun-brightness scalar (W), from the LIVE light grid.
        Vec4f light = world.BlockAccessor.GetLightRGBs(pos.X, pos.Y, pos.Z);
        float blockR = light.R, blockG = light.G, blockB = light.B;
        float sun = light.W;

        // (2) Sky color + weather/rain darkening from the ambient manager (also live).
        Vec3f sky = capi.Ambient?.BlendedAmbientColor ?? new Vec3f(1f, 1f, 1f);
        float sceneBrightness = capi.Ambient?.BlendedSceneBrightness ?? 1f;

        // (3) Held light — the grid at the reader's own block misses it.
        float gridLuma = 0.2126f * blockR + 0.7152f * blockG + 0.0722f * blockB;
        bool held = TryHeldLight(world, entity, out float heldLuma, out float heldR, out float heldG, out float heldB);

        // For the TINT path: when the held light out-shines the grid it dominates the
        // block-light term, carrying its own hue. The brightness split folds held via
        // curve + MAX below, so its dominance is implicit there.
        float blockLuma = gridLuma;
        if (held && heldLuma > gridLuma)
        {
            blockLuma = heldLuma;
            blockR = heldR; blockG = heldG; blockB = heldB;
        }

        // --- Brightness (environment vs. held, so a flicker mod's per-frame V survives) ---
        float floor = IlluminationCurve.ClampFloor(floorPref());
        float sunlit = sun * Clamp01(sceneBrightness);
        float envRaw = Clamp01(MathF.Max(gridLuma, sunlit));
        float envBrightness = IlluminationCurve.Evaluate(envRaw, floor);
        // Held term is 0 (not curve(0)=floor) when nothing is held, so an absent held
        // light can't floor-lift the MAX.
        float heldBrightness = held ? IlluminationCurve.Evaluate(Clamp01(heldLuma), floor) : 0f;

        // --- Hue: weight block vs. sky by which is actually lighting the reader, then
        // normalize so the max channel is 1 (brightness carries level; tint only skews hue).
        float wsum = blockLuma + sunlit;
        float r, g, b;
        if (wsum <= 1e-4f)
        {
            r = g = b = 1f;   // no light of either kind — brightness carries the darkness
        }
        else
        {
            r = (blockR * blockLuma + sky.R * sunlit) / wsum;
            g = (blockG * blockLuma + sky.G * sunlit) / wsum;
            b = (blockB * blockLuma + sky.B * sunlit) / wsum;
            float max = MathF.Max(r, MathF.Max(g, b));
            if (max <= 1e-4f) { r = g = b = 1f; }
            else { r /= max; g /= max; b /= max; }
        }

        // Pull each channel partway back toward neutral so the hue skew is gentler than
        // the physical light. Level is unaffected.
        r = 1f + (r - 1f) * TintStrength;
        g = 1f + (g - 1f) * TintStrength;
        b = 1f + (b - 1f) * TintStrength;

        return Smooth(envBrightness, heldBrightness, r, g, b, dt);
    }

    private bool FlickerModActive => flickerModActive ??= capi.ModLoader.IsModEnabled(FlickerModId);

    /// <summary>Read the light emitted by the item(s) in the reader's two hands, as a
    /// normalized (luma, RGB hue) pair from the same live sources the engine uses. Where
    /// Scribe merged the two hands with the engine's V-weighted helper, we simply take the
    /// brighter hand whole — two simultaneous held light sources are rare, and the brighter
    /// one is what you would read by.</summary>
    private static bool TryHeldLight(IWorldAccessor world, EntityPlayer entity,
        out float luma, out float r, out float g, out float b)
    {
        luma = 0f; r = g = b = 1f;

        byte[]? rightHsv = entity.RightHandItemSlot?.Itemstack?.Collectible?
            .GetLightHsv(world.BlockAccessor, null, entity.RightHandItemSlot.Itemstack);
        byte[]? leftHsv = entity.LeftHandItemSlot?.Itemstack?.Collectible?
            .GetLightHsv(world.BlockAccessor, null, entity.LeftHandItemSlot.Itemstack);

        byte[]? hsv = (rightHsv?[2] ?? 0) >= (leftHsv?[2] ?? 0) ? rightHsv : leftHsv;
        if (hsv is null || hsv[2] == 0) return false;

        // lightHsv stores INDICES, not 0..255: h∈0..63, s∈0..7, v∈0..31.
        int v = hsv[2];

        // Brightness: map V through the LIVE block-light-level table (the same table the
        // grid uses), not V/31.
        float[] levels = world.BlockLightLevels;
        luma = (levels is not null && v < levels.Length) ? levels[v] : Clamp01(v / 31f);

        // Hue direction: the item's HSV at FULL value, normalized so max channel = 1 —
        // the tint expresses only the source's color temperature.
        int rgb = ColorUtil.HsvToRgb(hsv[0] * 255 / 63, hsv[1] * 255 / 7, 255);
        float hr = ((rgb >> 16) & 0xFF) / 255f;
        float hg = ((rgb >> 8) & 0xFF) / 255f;
        float hb = (rgb & 0xFF) / 255f;
        float mx = MathF.Max(hr, MathF.Max(hg, hb));
        if (mx > 1e-4f) { r = hr / mx; g = hg / mx; b = hb / mx; }
        return true;
    }

    /// <summary>Ease the shade toward the freshly-sampled target. Only the ENVIRONMENT
    /// brightness and the hue are eased; when a flicker mod is present the HELD brightness
    /// is combined by MAX after easing, unsmoothed, so its per-frame flicker survives.
    /// Without one, held folds into the single eased value (the curve is monotonic, so
    /// max(curve a, curve b) == curve max).</summary>
    private Shade Smooth(float envBrightness, float heldBrightness, float r, float g, float b, float dt)
    {
        bool passHeldThrough = FlickerModActive;
        float smoothTarget = passHeldThrough ? envBrightness : MathF.Max(envBrightness, heldBrightness);

        if (!hasSmoothed)
        {
            // First frame — adopt directly so the opening book isn't seen fading up.
            hasSmoothed = true;
            smoothedBrightness = smoothTarget;
            smoothedR = r; smoothedG = g; smoothedB = b;
        }
        else
        {
            // alpha = 1 - e^(-dt/τ): frame-rate independent. Guard dt ≤ 0.
            float alpha = dt > 0f ? 1f - MathF.Exp(-dt / SmoothingTau) : 1f;
            smoothedBrightness += (smoothTarget - smoothedBrightness) * alpha;
            smoothedR += (r - smoothedR) * alpha;
            smoothedG += (g - smoothedG) * alpha;
            smoothedB += (b - smoothedB) * alpha;
        }

        float brightness = passHeldThrough
            ? MathF.Max(smoothedBrightness, heldBrightness)
            : smoothedBrightness;

        return new Shade(
            Clamp01(brightness * smoothedR),
            Clamp01(brightness * smoothedG),
            Clamp01(brightness * smoothedB));
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    /// <summary>Diagnostic for `.almbook light`: a one-shot, unsmoothed snapshot of what the
    /// sampler sees RIGHT NOW, so the curve anchors can be judged against real numbers
    /// (measure, don't theorize). Does not touch smoothing state.</summary>
    public static string Describe(ICoreClientAPI capi, float floorSetting)
    {
        var world = capi.World;
        var entity = world?.Player?.Entity;
        if (world is null || entity is null) return "No world/player yet.";

        BlockPos pos = entity.Pos.AsBlockPos;
        Vec4f light = world.BlockAccessor.GetLightRGBs(pos.X, pos.Y, pos.Z);
        float gridLuma = 0.2126f * light.R + 0.7152f * light.G + 0.0722f * light.B;
        float sceneBrightness = capi.Ambient?.BlendedSceneBrightness ?? 1f;
        float sunlit = light.W * Clamp01(sceneBrightness);

        bool held = TryHeldLight(world, entity, out float heldLuma, out float hr, out float hg, out float hb);
        float blockLuma = held && heldLuma > gridLuma ? heldLuma : gridLuma;
        float raw = Clamp01(MathF.Max(blockLuma, sunlit));
        float floor = IlluminationCurve.ClampFloor(floorSetting);
        float outBrightness = IlluminationCurve.Evaluate(raw, floor);

        return
            $"light @ ({pos.X},{pos.Y},{pos.Z}): grid RGB=({light.R:0.00},{light.G:0.00},{light.B:0.00}) " +
            $"luma={gridLuma:0.000}, sunW={light.W:0.00} scene={sceneBrightness:0.00} → sunlit={sunlit:0.000}; " +
            $"held: {(held ? $"luma={heldLuma:0.000} tint=({hr:0.00},{hg:0.00},{hb:0.00})" : "none")}; " +
            $"raw={raw:0.000} floor={floor:0.00} → book brightness {outBrightness:0.000} ({outBrightness * 100f:0}%)";
    }
}
