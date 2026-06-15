using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AlmanacIlluminated;

/// <summary>
/// The Almanac: Illuminated — a living questbook for the modded world.
/// Phase 0 renderer spike: native GUI book dialog with a heavy mock chapter.
/// Design of record: agentic-os projects/briefs/the-almanac-illuminated/.
/// </summary>
public class AlmanacIlluminatedModSystem : ModSystem
{
    private const string ChannelName = "almanacilluminated";

    private ICoreClientAPI? capi;
    private GuiDialogIlluminatedBook? bookDialog;
    private List<GuidePack> guidePacks = new();
    private GuideLibrary? library;

    // Homebase climate (Crops tab): the bound spawn lives server-side, synced on request.
    private ICoreServerAPI? sapi;
    private IClientNetworkChannel? clientChannel;
    private IServerNetworkChannel? serverChannel;
    private BlockPos? homebasePos;
    private string homebaseLabel = "";
    private Action? onHomebaseReceived;

    // Universal: the GUI is client-only, but the homebase sync needs a server handler.
    public override bool ShouldLoad(EnumAppSide side) => true;

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

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        // The bound spawn is server-only; answer the client's homebase request with it.
        serverChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<HomebaseRequest>()
            .RegisterMessageType<HomebaseResponse>()
            .SetMessageHandler<HomebaseRequest>(OnHomebaseRequest);
    }

    private void OnHomebaseRequest(IServerPlayer fromPlayer, HomebaseRequest req)
    {
        var pos = fromPlayer.GetSpawnPosition(false);   // resolved: bed spawn, else world default
        // No public getter for the raw bed spawn; infer it by divergence from world default.
        var def = sapi?.World.DefaultSpawnPosition;
        bool bed = def == null || Math.Abs(pos.X - def.X) > 1 || Math.Abs(pos.Z - def.Z) > 1;
        serverChannel?.SendPacket(new HomebaseResponse
        {
            X = (int)pos.X,
            Y = (int)pos.Y,
            Z = (int)pos.Z,
            BedSpawn = bed,
        }, fromPlayer);
    }

    private void OnHomebaseResponse(HomebaseResponse msg)
    {
        homebasePos = new BlockPos(msg.X, msg.Y, msg.Z);
        homebaseLabel = msg.BedSpawn ? "your bed" : "world spawn";
        var cb = onHomebaseReceived;
        onHomebaseReceived = null;
        cb?.Invoke();
    }

    /// <summary>
    /// Resolve the homebase position, then run onReady. Uses the server's bound spawn
    /// when the channel is connected; otherwise (no server-side mod, or not yet joined)
    /// falls back to where the player is standing, so the Crops tab still works.
    /// </summary>
    private void EnsureHomebase(Action onReady)
    {
        if (clientChannel?.Connected == true)
        {
            onHomebaseReceived = onReady;
            clientChannel.SendPacket(new HomebaseRequest());
        }
        else
        {
            homebasePos = capi!.World.Player.Entity.Pos.AsBlockPos;
            homebaseLabel = "where you stand";
            onReady();
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        clientChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<HomebaseRequest>()
            .RegisterMessageType<HomebaseResponse>()
            .SetMessageHandler<HomebaseResponse>(OnHomebaseResponse);

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

        // Crops-tab phase 1: a dev command to validate crop discovery against a pack.
        api.ChatCommands.Create("almcrops")
            .WithDescription("List the growable crops the Almanac discovered (logs detail to the client log)")
            .HandleWith(OnCropsCommand);

        IlluminatedLogger.Info(api, "startup", "Illuminated Phase 0 spike loaded — Alt+J opens the book");
    }

    private TextCommandResult OnCropsCommand(TextCommandCallingArgs args)
    {
        if (capi == null) return TextCommandResult.Error("Client API unavailable");

        EnsureHomebase(() =>
        {
            var entries = CropCatalog.Build(capi);
            CropCatalog.LogSummary(capi, entries, homebasePos);
            int vanilla = entries.Count(e => e.Vanilla);
            capi.ShowChatMessage(
                $"Almanac: {entries.Count} growable(s), {vanilla} vanilla / {entries.Count - vanilla} modded. " +
                $"Homebase: {homebaseLabel} at {homebasePos}. Details in the client log.");
        });
        return TextCommandResult.Success("Querying homebase climate; crop catalog with planting windows will log shortly.");
    }

    private bool OnToggleBook(KeyCombination comb)
    {
        if (capi == null) return false;

        // The whole visible library, ordered: the book opens to the overview (or
        // first front matter) and navigates between chapters via internal links.
        library ??= new GuideLibrary(capi, guidePacks);
        bookDialog ??= new GuiDialogIlluminatedBook(capi, library, () => homebasePos, EnsureHomebase);

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
