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
    private GuideLibrary? library;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartPre(ICoreAPI api)
    {
        // `almanac` is a custom asset category. The disk scanner only indexes
        // known categories, so register it before assets load. Without this,
        // guide files at almanac/guides/*.json are never read. Universal side,
        // does not affect gameplay sync.
        if (AssetCategory.FromCode("almanac") == null)
        {
            new AssetCategory("almanac", false, EnumAppSide.Universal);
        }
    }

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

        // Build the library now (guide packs loaded in AssetsLoaded) so any
        // overview-conflict warning is logged at startup, not only on first open.
        library = new GuideLibrary(api, guidePacks);

        api.Input.RegisterHotKey(GuiDialogIlluminatedBook.HotkeyCode, "Open The Almanac",
            GlKeys.J, HotkeyType.GUIOrOtherControls, altPressed: true);
        api.Input.SetHotKeyHandler(GuiDialogIlluminatedBook.HotkeyCode, OnToggleBook);

        IlluminatedLogger.Info(api, "startup", "Illuminated Phase 0 spike loaded — Alt+J opens the book");
    }

    private bool OnToggleBook(KeyCombination comb)
    {
        if (capi == null) return false;

        // The whole visible library, ordered: the book opens to the overview (or
        // first front matter) and navigates between chapters via internal links.
        library ??= new GuideLibrary(capi, guidePacks);
        bookDialog ??= new GuiDialogIlluminatedBook(capi, library);

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
