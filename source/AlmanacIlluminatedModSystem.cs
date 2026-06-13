using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// The Almanac: Illuminated — a living questbook for the modded world.
/// Phase 0 renderer spike: native GUI book dialog with a heavy mock chapter.
/// Design of record: agentic-os projects/briefs/the-almanac-illuminated/.
/// </summary>
public class AlmanacIlluminatedModSystem : ModSystem
{
    private ICoreClientAPI? capi;
    private GuiDialogIlluminatedBook? bookDialog;
    private List<GuidePack> guidePacks = new();

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void AssetsLoaded(ICoreAPI api)
    {
        // Assets are available here, before StartClientSide. Discover guide packs.
        if (api is ICoreClientAPI clientApi)
        {
            guidePacks = GuidePackLoader.Load(clientApi);
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        // Register bundled fonts before any GUI can request them.
        FontRegistry.RegisterAll(api);

        // Alt+J is the Almanac's keybind. The legacy almanaccodexilluminated
        // dialog also binds it — they should not be loaded together; warn if so.
        if (api.ModLoader.IsModEnabled("almanaccodexilluminated"))
        {
            IlluminatedLogger.Warn(api, "startup",
                "Legacy 'almanaccodexilluminated' is loaded alongside Illuminated — both bind Alt+J. Disable the legacy mod.");
        }

        api.Input.RegisterHotKey(GuiDialogIlluminatedBook.HotkeyCode, "Open The Almanac",
            GlKeys.J, HotkeyType.GUIOrOtherControls, altPressed: true);
        api.Input.SetHotKeyHandler(GuiDialogIlluminatedBook.HotkeyCode, OnToggleBook);

        IlluminatedLogger.Info(api, "startup", "Illuminated Phase 0 spike loaded — Alt+J opens the book");
    }

    private bool OnToggleBook(KeyCombination comb)
    {
        if (capi == null) return false;

        bookDialog ??= new GuiDialogIlluminatedBook(capi);

        if (bookDialog.IsOpened()) bookDialog.TryClose();
        else bookDialog.TryOpen();

        return true;
    }

    public override void Dispose()
    {
        bookDialog?.Dispose();
        bookDialog = null;
        base.Dispose();
    }
}
