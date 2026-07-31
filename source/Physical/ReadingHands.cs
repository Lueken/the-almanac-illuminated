using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace AlmanacIlluminated;

/// <summary>
/// While a player is reading the Almanac, their in-world character holds the open
/// book instead of whatever the hotbar says (the sketchbook social signal, but for
/// a summoned book with no hotbar slot).
///
/// Mechanism (verified against the VSEssentials 1.22 decompile, 2026-07-31):
/// EntityShapeRenderer.RenderHeldItem is a thin dispatcher — slot, attachment-point
/// pose, GetItemStackRenderInfo, then a protected virtual RenderItem that owns all
/// matrix work. One prefix on the dispatcher gives everything: not-reading players
/// fall through untouched; a reading player's LEFT hand renders nothing (the held
/// item hides), and the RIGHT hand renders the almanacilluminated:almanac dummy
/// item through the same RenderItem, so shadows, first person and the player
/// renderer's own overrides all behave. Empty hands work because the real slot is
/// never consulted. The reading flag is the watched attribute the server stamps
/// from the ReadingState message, so other clients render it too.
///
/// Resolved-by-name and warn-and-skip on any miss (the TCM seam lesson): a future
/// engine rename costs this feature alone, never the book.
/// </summary>
public static class ReadingHands
{
    public const string Attr = "almanacReading";
    private const string HarmonyId = "almanacilluminated.readinghands";

    /// <summary>Local truth for the LOCAL player: set directly by the dialog's open/close
    /// hooks. The watched attribute is only consulted for OTHER players. This is not an
    /// optimization: in paused singleplayer the server does not tick, so the ReadingState
    /// round-trip sits queued until the book closes and the pause releases — rendering
    /// the reader's own book off the attribute meant it appeared exactly when it should
    /// have vanished (found in play, 2026-07-31).</summary>
    public static bool LocalReading;

    private static ICoreClientAPI? capi;
    private static Harmony? harmony;
    private static DummySlot? bookSlot;
    private static bool warnedNoItem;

    /// <summary>Open instance delegate onto the protected virtual RenderItem; virtual
    /// dispatch on purpose so the player renderer's first-person shader override runs.</summary>
    private static Action<EntityShapeRenderer, float, bool, ItemStack, AttachmentPointAndPose, ItemRenderInfo>? renderItem;

    public static void Init(ICoreClientAPI api)
    {
        capi = api;
        try
        {
            var target = AccessTools.DeclaredMethod(typeof(EntityShapeRenderer), "RenderHeldItem");
            var ri = AccessTools.DeclaredMethod(typeof(EntityShapeRenderer), "RenderItem");
            if (target == null || ri == null)
            {
                IlluminatedLogger.Warn(api, "readinghands",
                    "EntityShapeRenderer.RenderHeldItem/RenderItem not found; the in-hands book is inactive this build");
                return;
            }
            renderItem = AccessTools.MethodDelegate<
                Action<EntityShapeRenderer, float, bool, ItemStack, AttachmentPointAndPose, ItemRenderInfo>>(ri, null, true, null);
            harmony = new Harmony(HarmonyId);
            harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ReadingHands), nameof(Prefix))));
            IlluminatedLogger.Info(api, "readinghands", "in-hands book hooked (EntityShapeRenderer.RenderHeldItem)");
        }
        catch (Exception e)
        {
            IlluminatedLogger.Warn(api, "readinghands", $"could not hook the in-hands book ({e.Message}); feature inactive");
            renderItem = null;
        }
    }

    public static void Dispose()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        bookSlot = null;
        renderItem = null;
        capi = null;
    }

    /// <summary>The dummy stack the reading hand renders. Resolved lazily: item
    /// registries are not ready at Init time.</summary>
    private static DummySlot? GetBookSlot()
    {
        if (bookSlot != null) return bookSlot;
        var item = capi?.World.GetItem(new AssetLocation("almanacilluminated", "almanac"));
        if (item == null)
        {
            if (!warnedNoItem && capi != null)
            {
                warnedNoItem = true;
                IlluminatedLogger.Warn(capi, "readinghands", "item almanacilluminated:almanac missing; the in-hands book cannot render");
            }
            return null;
        }
        bookSlot = new DummySlot(new ItemStack(item));
        return bookSlot;
    }

    /// <summary>Exception-guarded: a throw here would take the entity render loop down,
    /// so any failure falls back to vanilla rendering for that call.</summary>
    public static bool Prefix(EntityShapeRenderer __instance, float dt, bool isShadowPass, bool right)
    {
        try
        {
            if (capi == null || renderItem == null) return true;
            if (__instance.entity is not Vintagestory.API.Common.Entities.Entity e
                || e is not EntityPlayer plr) return true;
            bool reading = plr == capi.World.Player?.Entity
                ? LocalReading
                : plr.WatchedAttributes?.GetBool(Attr) == true;
            if (!reading) return true;

            if (!right) return false;   // left hand: the held item simply hides

            var apap = plr.AnimManager?.Animator?.GetAttachmentPointPose("RightHand");
            if (apap == null) return false;
            var slot = GetBookSlot();
            if (slot?.Itemstack == null) return false;

            var info = capi.Render.GetItemStackRenderInfo(slot, EnumItemRenderTarget.HandTp, dt);
            if (info?.ModelRef == null) return false;

            renderItem(__instance, dt, isShadowPass, slot.Itemstack, apap, info);
            return false;
        }
        catch
        {
            return true;
        }
    }
}
