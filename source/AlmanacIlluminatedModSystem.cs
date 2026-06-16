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

        api.ChatCommands.Create("almweather")
            .WithDescription("Sample the home weather outlook for the year (logs detail to the client log)")
            .HandleWith(OnWeatherCommand);

        api.ChatCommands.Create("almweathersweep")
            .WithDescription("Sample the weather outlook across latitudes (equator to pole) to validate the prose")
            .HandleWith(OnWeatherSweepCommand);

        IlluminatedLogger.Info(api, "startup", "Illuminated Phase 0 spike loaded — Alt+J opens the book");
    }

    private TextCommandResult OnWeatherCommand(TextCommandCallingArgs args)
    {
        if (capi == null) return TextCommandResult.Error("Client API unavailable");

        EnsureHomebase(() =>
        {
            var w = HomeWeather.Sample(capi, homebasePos!);
            string[] mn = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            IlluminatedLogger.Info(capi, "weather",
                $"Home {homebaseLabel} at {homebasePos} — lat {w.Latitude:0.00} ({(w.North ? "N" : "S")}), " +
                $"year {w.YearMinTemp:0}°..{w.YearMaxTemp:0}°, swing {w.SeasonalSwing:0}°. " +
                $"Warmest {mn[w.WarmestMonth]}, coldest {mn[w.ColdestMonth]}, wettest {mn[w.WettestMonth]}, driest {mn[w.DriestMonth]}. " +
                (w.FrostFreeYear ? "Frost-free all year." : w.FrostBoundYear ? "Frozen all year." :
                    $"Last frost ~{mn[w.LastFrostMonth]}, first frost ~{mn[w.FirstFrostMonth]}."));
            for (int m = 0; m < 12; m++)
                IlluminatedLogger.Info(capi, "weather",
                    $"  {mn[m]}: mean {w.MonthMeanTemp[m]:0.#}° ({w.MonthMinTemp[m]:0}..{w.MonthMaxTemp[m]:0}°), wet {w.MonthWetness[m]:0.00}, snow {w.MonthSnowShare[m] * 100:0}%");
            capi.ShowChatMessage($"Almanac weather sampled for {homebaseLabel} (lat {w.Latitude:0.00}). Details in the client log.");
        });
        return TextCommandResult.Success("Sampling home weather for the year; outlook will log shortly.");
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

    private TextCommandResult OnWeatherSweepCommand(TextCommandCallingArgs args)
    {
        if (capi == null) return TextCommandResult.Error("Client API unavailable");

        int playerX = (int)capi.World.Player.Entity.Pos.X;
        int mapZ = capi.World.BlockAccessor.MapSizeZ;
        // Latitude is a sawtooth in Z (equator -> +1 pole -> back), not monotonic, so step
        // out from an equator by latitude x polarEquatorDistance to stay on one hemisphere.
        int ped = int.TryParse(capi.World.Config?.GetString("polarEquatorDistance"), out var pv) ? pv : 50000;
        int equatorZ = FindZForLatitude(0.0, mapZ);
        IlluminatedLogger.Info(capi, "weather",
            $"Latitude sweep at x={playerX} (mapSizeZ={mapZ}, polarEquatorDistance={ped}, equator z≈{equatorZ}):");

        double[] targets = { 0.0, 0.25, 0.5, 0.75, 0.95 };
        foreach (double target in targets)
        {
            int z = GameMath.Clamp(equatorZ + (int)(target * ped), 0, mapZ - 1);
            var w = HomeWeather.Sample(capi, new BlockPos(playerX, 120, z));
            string frost = w.FrostFreeYear ? "frost-free" : w.FrostBoundYear ? "frost-bound" : "seasonal frost";
            IlluminatedLogger.Info(capi, "weather",
                $"--- target lat {target:0.00} -> z={z}, actual lat {w.Latitude:0.00} ({(w.North ? "N" : "S")}), " +
                $"year {w.YearMinTemp:0}°..{w.YearMaxTemp:0}°, swing {w.SeasonalSwing:0}°, {frost} ---");
            foreach (var (title, text) in WeatherRenderer.BuildSections(w))
                IlluminatedLogger.Info(capi, "weather", $"  [{title}] {text}");
        }

        capi.ShowChatMessage("Weather latitude sweep (equator → pole) logged to the client log.");
        return TextCommandResult.Success("Sweeping latitudes; prose for each logged.");
    }

    /// <summary>First Z whose latitude reaches the target; OnGetLatitude is monotonic in Z, so binary search the map.</summary>
    private int FindZForLatitude(double targetLat, int mapZ)
    {
        int lo = 0, hi = System.Math.Max(1, mapZ);
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (capi!.World.Calendar.OnGetLatitude(mid) >= targetLat) hi = mid;
            else lo = mid + 1;
        }
        return lo;
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
