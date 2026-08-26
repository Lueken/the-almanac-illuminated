using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AlmanacIlluminated;

/// <summary>
/// The Almanac: Illuminated — a living questbook for the modded world.
/// Native GUI book dialog, presented as a book held in the reader's hands.
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

    /// <summary>The homebase currently held is a local guess (the player's feet), made
    /// because the server could not be asked. Cleared the moment a real answer lands, which
    /// is also when any page built from the guess gets rebuilt.</summary>
    private bool homebaseProvisional;
    private Action? onHomebaseReceived;

    /// <summary>The live client-side instance, for renderers that need session state the
    /// page-building call chain does not carry. Client only and cleared on dispose, matching
    /// the ClientInstance pattern the sibling mod already uses.</summary>
    public static AlmanacIlluminatedModSystem? Instance;

    // The forage atlas (Crops tab, mushrooms): worldgen habitat envelopes, which are a
    // server-only asset category. Requested once per session alongside the homebase and
    // cached; null simply means the card falls back to what the block registry already
    // gives it, which is the same graceful-degradation posture the familiarity supplier
    // takes when no supplier is registered.
    private Dictionary<string, ForageAtlasRow>? forageAtlas;
    private Dictionary<string, string>? forageDyes;
    private float homeRain = -1f, homeForest = -1f, atlasMedianRain = 0.4f;
    private bool forageAtlasRequested;

    /// <summary>Habitat row for a species key, or null when no atlas has arrived.</summary>
    public ForageAtlasRow? ForageRowFor(string produceCode)
    {
        if (forageAtlas == null) return null;
        return forageAtlas.TryGetValue(ForageAtlas.Key(produceCode), out var row) ? row : null;
    }

    /// <summary>Dye colour for a species key, or null when no atlas has arrived or the species
    /// yields none. Most yield none, so absence is the ordinary case and never an error.</summary>
    public string? DyeColourFor(string produceCode)
    {
        if (forageDyes == null) return null;
        return forageDyes.TryGetValue(ForageAtlas.Key(produceCode), out var colour) ? colour : null;
    }

    public float HomeRain => homeRain;
    public float HomeForest => homeForest;
    public float AtlasMedianRain => atlasMedianRain;

    // Book tabs registered by other mods (TCM's Callings first). Initialized
    // inline so registration is safe regardless of ModSystem start order.
    private readonly List<IAlmanacBookTab> bookTabs = new();

    // The book's presentation. The dialog still composes every page — it is the
    // layout engine — and the view captures it to a framebuffer and draws it on a
    // modeled, tilted book. There is no second presentation: the flat page was
    // retired in 0.2.0, so a machine that cannot run the capture cannot open the
    // book, and says so plainly rather than failing quietly.
    private PhysicalBookView? physicalView;
    /// <summary>Pause the singleplayer world while the book is open. Default on; the book
    /// exists to be read, and reading should not cost daylight.</summary>
    private bool pauseOnOpen = true;

    // Local-illumination shading: the book renders by the light actually reaching the
    // reader (on by default; `.almbook light off` opts out). The floor is the book's
    // brightness in total darkness. Technique adapted from the Scribe mod (MIT) — see
    // AmbientLightSampler.cs for the full attribution.
    private bool respectWorldLight = true;
    private float illuminationFloor = IlluminationCurve.DefaultFloor;
    private AmbientLightSampler? lightSampler;
    private static string SettingsPath => System.IO.Path.Combine(
        Vintagestory.API.Config.GamePaths.DataPath, "ModData", "almanacilluminated", "settings.json");


    // Author preview (dev-only): read guides live from a folder or packed zip, hot-reload on edit.
    private PreviewSource.Location? previewLoc;
    private System.IO.FileSystemWatcher? previewWatcher;
    private long previewReloadToken;

    // Universal: the GUI is client-only, but the homebase sync needs a server handler.
    public override bool ShouldLoad(EnumAppSide side) => true;

    /// <summary>
    /// Registers an external book tab (client side). Call any time before the
    /// book first opens — typically from your mod's StartClientSide via
    /// api.ModLoader.GetModSystem&lt;AlmanacIlluminatedModSystem&gt;().
    /// </summary>
    public void RegisterBookTab(IAlmanacBookTab tab)
    {
        if (tab == null || bookTabs.Contains(tab)) return;
        bookTabs.Add(tab);
        if (capi != null) IlluminatedLogger.Info(capi, "tabs", $"External book tab registered: {tab.Label}");
    }

    /// <summary>
    /// The registered crop-familiarity supplier, or null when nothing supplies one. The
    /// Crops tab reads this on every build; null (or a supplier reporting itself disabled)
    /// is the plain-catalogue path, which is what Illuminated on its own always shows.
    /// </summary>
    public ICropFamiliaritySource? CropFamiliarity { get; private set; }

    /// <summary>
    /// How the Crops tab records what the reader has grown: a tally struck in the margin,
    /// a rule inked as far as they have come, or plain counts. A reading preference, not a
    /// gameplay one, so it lives in the client's own book settings. Inert with no
    /// familiarity supplier registered, because then there is nothing to record.
    /// </summary>
    public GrowthMarksStyle CropMarks { get; private set; } = GrowthMarksStyle.Tally;

    /// <summary>
    /// Registers the crop-familiarity supplier for the Crops tab (client side). Call from
    /// your mod's StartClientSide, any time before the book first opens. Last registration
    /// wins; there is one tab and one taxonomy behind it.
    /// </summary>
    public void RegisterCropFamiliarity(ICropFamiliaritySource source)
    {
        if (source == null) return;
        CropFamiliarity = source;
        if (capi != null) IlluminatedLogger.Info(capi, "crops",
            $"Crop familiarity supplier registered: {source.GetType().Name}");
    }

    // Reveal providers, keyed by the namespace before the ':' in a section's
    // revealedBy key. TCM registers "almanactcm" backed by its Knowledge store.
    private readonly Dictionary<string, System.Func<string, bool>> revealProviders =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a reveal provider for one key namespace (client side). Guide sections
    /// with revealedBy "<paramref name="keyNamespace"/>:..." render only when
    /// <paramref name="isRevealed"/> returns true for the full key. Call from your mod's
    /// StartClientSide, any time before the book first opens.
    /// </summary>
    public void RegisterRevealProvider(string keyNamespace, System.Func<string, bool> isRevealed)
    {
        if (string.IsNullOrEmpty(keyNamespace) || isRevealed == null) return;
        revealProviders[keyNamespace] = isRevealed;
        if (capi != null) IlluminatedLogger.Info(capi, "reveal", $"Reveal provider registered: {keyNamespace}");
    }

    /// <summary>Point a freshly built library at the same reveal providers the sections use, so
    /// pack-level `revealedBy` hides a whole chapter and its tab. Called at every construction
    /// site; a library built before any provider registers simply shows everything, which is the
    /// degrade-open behaviour and also the right answer during asset load.</summary>
    private void AttachRevealProbe()
    {
        if (library != null) library.RevealProbe = IsRevealed;
    }

    /// <summary>Quest-step lookup for sibling mods' completion toasts (API stable as of
    /// 0.1.4): returns the text of every quest item across loaded guide packs whose
    /// `doneWhen` equals <paramref name="revealKey"/>. Empty list when none match, never
    /// null. TCM calls this on a live knowledge earn to raise its animated-check toast.</summary>
    public System.Collections.Generic.IReadOnlyList<string> GetQuestStepsFor(string revealKey)
    {
        return QuestStepIndex.StepsFor(guidePacks, revealKey);
    }

    /// <summary>
    /// Whether a revealedBy key is earned. The degradation rule is deliberate: a key
    /// whose namespace has NO registered provider is treated as revealed, so a guide
    /// authored against an absent mod never produces a permanently dead page. A
    /// provider that throws is treated the same way, for the same reason.
    /// </summary>
    public bool IsRevealed(string key) => Probe(key, whenUnknown: true);

    /// <summary>
    /// Whether a key has actually been EARNED. Same providers as <see cref="IsRevealed"/>,
    /// opposite degradation: a key whose namespace has no registered provider counts as not
    /// earned, and so does a provider that throws.
    ///
    /// The split is deliberate. A reveal degrades open because a guide written against an
    /// absent mod must never leave a page permanently dead. Completion is the other way
    /// round: nothing can have been done in a mod that is not loaded, and ticking every box
    /// on a checklist nobody can complete would be a lie the reader cannot correct. Used by
    /// a quest item's `doneWhen` and by a section's `whenChapterComplete`.
    /// </summary>
    public bool IsEarned(string key) => Probe(key, whenUnknown: false);

    private bool Probe(string key, bool whenUnknown)
    {
        if (string.IsNullOrEmpty(key)) return whenUnknown;
        int i = key.IndexOf(':');
        string ns = i > 0 ? key.Substring(0, i) : key;
        if (!revealProviders.TryGetValue(ns, out var provider)) return whenUnknown;
        try { return provider(key); }
        catch (System.Exception e)
        {
            if (capi != null) IlluminatedLogger.Warn(capi, "reveal",
                $"provider '{ns}' threw on '{key}' ({e.Message}); treating as {(whenUnknown ? "revealed" : "unearned")}");
            return whenUnknown;
        }
    }

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
            .RegisterMessageType<ReadingState>()
            .RegisterMessageType<ForageAtlasRequest>()
            .RegisterMessageType<ForageAtlasResponse>()
            .SetMessageHandler<HomebaseRequest>(OnHomebaseRequest)
            .SetMessageHandler<ForageAtlasRequest>(OnForageAtlasRequest)
            .SetMessageHandler<ReadingState>(OnReadingState);

        // A crash or disconnect mid-read must not strand the flag: reading state is
        // session-scoped, so every join starts with it clear.
        api.Event.PlayerNowPlaying += p =>
            p.Entity?.WatchedAttributes?.SetBool(ReadingHands.Attr, false);
    }

    /// <summary>Stamp the reading flag on the player entity. Watched attributes
    /// broadcast to every client, which is the whole point: ReadingHands renders the
    /// open book in this player's hands on every screen that can see them.</summary>
    private void OnReadingState(IServerPlayer fromPlayer, ReadingState msg)
    {
        var wa = fromPlayer.Entity?.WatchedAttributes;
        if (wa == null) return;
        wa.SetBool(ReadingHands.Attr, msg.Reading);
        wa.MarkPathDirty(ReadingHands.Attr);
    }

    /// <summary>
    /// Build the atlas and answer with it, plus the reader's own ground.
    ///
    /// Rebuilt per request rather than cached server-side. It is one walk of an asset
    /// category that is already in memory, it happens once per player per session, and a
    /// cache would need an invalidation story for a thing that cannot change without a
    /// restart. Cheap beats clever here.
    /// </summary>
    private void OnForageAtlasRequest(IServerPlayer fromPlayer, ForageAtlasRequest req)
    {
        if (sapi == null) return;
        try
        {
            var rows = ForageAtlas.Build(sapi);
            var dyes = ForageAtlas.BuildDyes(sapi);
            var reply = new ForageAtlasResponse { Rows = rows, Dyes = dyes, MedianRain = ForageAtlas.MedianRain(rows) };
            serverChannel?.SendPacket(reply, fromPlayer);
            IlluminatedLogger.Info(sapi, "crops",
                $"forage atlas sent to {fromPlayer.PlayerName}: {rows.Count} mushroom row(s), "
                + $"{dyes.Count} dye species, median minRain {reply.MedianRain:0.##}");
        }
        catch (Exception e)
        {
            IlluminatedLogger.Warn(sapi, "crops", $"forage atlas build failed ({e.Message}); client keeps its plain cards");
        }
    }

    private void OnHomebaseRequest(IServerPlayer fromPlayer, HomebaseRequest req)
    {
        var pos = fromPlayer.GetSpawnPosition(false);   // resolved: bed spawn, else world default
        // No public getter for the raw bed spawn; infer it by divergence from world default.
        var def = sapi?.World.DefaultSpawnPosition;
        bool bed = def == null || Math.Abs(pos.X - def.X) > 1 || Math.Abs(pos.Z - def.Z) > 1;
        // The ground at that spawn, read on the side that is known to resolve it:
        // WorldGenValues is what populates ForestDensity, and every call to it in the
        // survival source sits on a server path.
        var climate = sapi?.World.BlockAccessor.GetClimateAt(
            new BlockPos((int)pos.X, (int)pos.Y, (int)pos.Z), EnumGetClimateMode.WorldGenValues);

        serverChannel?.SendPacket(new HomebaseResponse
        {
            X = (int)pos.X,
            Y = (int)pos.Y,
            Z = (int)pos.Z,
            BedSpawn = bed,
            Rain = climate?.Rainfall ?? -1f,
            Forest = climate?.ForestDensity ?? -1f,
        }, fromPlayer);
    }

    private void OnForageAtlasResponse(ForageAtlasResponse msg)
    {
        var map = new Dictionary<string, ForageAtlasRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in msg.Rows) map[row.Key] = row;
        forageAtlas = map;
        atlasMedianRain = msg.MedianRain;

        var dyeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dye in msg.Dyes) dyeMap[dye.Key] = dye.Colour;
        forageDyes = dyeMap;

        if (capi != null)
            IlluminatedLogger.Info(capi, "crops",
                $"forage atlas received: {map.Count} mushroom row(s), {dyeMap.Count} dye species, "
                + $"median minRain {atlasMedianRain:0.##}");

        // Cards already drawn were built without habitat lines. Redraw so the reader is not
        // left with a thinner page than the data now supports.
        bookDialog?.RefreshSpread();
    }

    private void OnHomebaseResponse(HomebaseResponse msg)
    {
        bool wasProvisional = homebaseProvisional;
        homebasePos = new BlockPos(msg.X, msg.Y, msg.Z);
        homebaseLabel = msg.BedSpawn ? "your bed" : "world spawn";
        homebaseProvisional = false;
        // Refreshed with the position, because they describe the same place and a bed can move.
        homeRain = msg.Rain;
        homeForest = msg.Forest;

        var cb = onHomebaseReceived;
        onHomebaseReceived = null;
        if (cb != null) { cb(); return; }

        // Nobody was waiting, which means a page was already built from the player's
        // feet because the server could not answer at the time. Now that the real bound
        // spawn has landed, rebuild it rather than leaving the reader a climate for the
        // wrong place until they reopen the tab.
        if (wasProvisional) bookDialog?.RefreshSpread();
    }

    /// <summary>
    /// Resolve the homebase position, then run onReady. Uses the server's bound spawn
    /// when the channel is connected; otherwise (no server-side mod, or not yet joined)
    /// falls back to where the player is standing, so the Crops tab still works.
    /// </summary>
    private void EnsureHomebase(Action onReady)
    {
        // The reply can only come back if the server is TICKING. The book pauses
        // singleplayer while it is open (GuiDialogIlluminatedBook.PauseEligible), and a
        // paused integrated server never processes the request, so asking would hand the
        // caller a continuation that can never run. Crops and Weather put their whole
        // page open inside that continuation, so the tab simply stopped responding: the
        // freeze reported on 2026-08-08, opening Crops from the Journal in a paused world.
        //
        // Nothing here may ever wait. If the server cannot answer, answer locally now and
        // let the real position arrive later if it ever does.
        bool serverCanAnswer = clientChannel?.Connected == true && capi?.IsGamePaused != true;

        if (serverCanAnswer)
        {
            onHomebaseReceived = onReady;
            clientChannel!.SendPacket(new HomebaseRequest());
            // Same trip, same conditions: the atlas cannot change without a restart, so one
            // ask per session is enough and nothing waits on the answer.
            if (!forageAtlasRequested)
            {
                forageAtlasRequested = true;
                clientChannel!.SendPacket(new ForageAtlasRequest());
            }
            return;
        }

        // Best answer available without the server: whatever it told us last time, else
        // where the player is standing. Keeping the earlier answer matters because it is
        // the real bound spawn, and a page built from it should not fall back to the
        // player's feet just because the world happens to be paused this time.
        if (homebasePos == null)
        {
            homebasePos = capi!.World.Player.Entity.Pos.AsBlockPos;
            homebaseLabel = "where you stand";
            homebaseProvisional = true;
        }
        onReady();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        Instance = this;

        clientChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<HomebaseRequest>()
            .RegisterMessageType<HomebaseResponse>()
            .RegisterMessageType<ReadingState>()
            .RegisterMessageType<ForageAtlasRequest>()
            .RegisterMessageType<ForageAtlasResponse>()
            .SetMessageHandler<HomebaseResponse>(OnHomebaseResponse)
            .SetMessageHandler<ForageAtlasResponse>(OnForageAtlasResponse);

        // The in-hands book (renderer patch). Init only wires the hook; the dummy
        // item resolves lazily on first render, after registries are ready.
        ReadingHands.Init(api);

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
        AttachRevealProbe();

        api.Input.RegisterHotKey(GuiDialogIlluminatedBook.HotkeyCode, "Open The Almanac",
            GlKeys.J, HotkeyType.GUIOrOtherControls, altPressed: true);
        api.Input.SetHotKeyHandler(GuiDialogIlluminatedBook.HotkeyCode, OnToggleBook);

        // The physical view is created before the commands below: a throw during
        // command registration must never take the book's presentation with it
        // (that exact failure shipped once during development).
        LoadSettings();
        physicalView = new PhysicalBookView(api);
        ApplyLoadedKnobs();   // LoadSettings ran before this existed; hand it the saved tuning now
        IlluminatedLogger.Info(api, "light",
            $"Local-illumination shading {(respectWorldLight ? "on" : "off")}, floor {illuminationFloor:0.00}");

        // Warm pagination (Jeffrey's ask, 2026-08-22): the first Alt+J used to pay the
        // whole library's Cairo pagination as one freeze. Instead, once the world has
        // finalized (when every mod is loading things anyway), paginate one chapter per
        // chained callback until the cache is warm. Opening the book early just stops
        // the chain and takes the remaining cost the old way.
        api.Event.LevelFinalize += () => api.Event.RegisterCallback(_ => WarmPaginateStep(api), 1500);
        physicalView.OnCaptureUnavailable = OnCaptureUnavailable;
        api.Event.RegisterRenderer(physicalView, EnumRenderStage.Ortho, "almanacilluminated-bookview");

        api.ChatCommands.Create("almbook")
            .WithDescription("Almanac presentation. No args: report. Args: pause [on|off] (singleplayer reading pause), light [on|off|floor <n>] (read by the world's light), or a physical-book knob: framing (tilt|yaw|dist|scale|fov|height <n>), page bend (bend|stiff|lead|corner <n>), leafing-through (flipslow|flipfast|flipcurl|flipscale|flipmax <n>, flippaper <0|1>), model <0|1>, reset. Knobs save as you set them.")
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("arg"))
            .HandleWith(OnBookCommand);

        // Deep links into the book, clickable from anywhere richtext links render
        // (chat, a server-info panel, another mod's guide). A link like
        // <a href="almanac://chapter/almanactcm:trades">the Trades</a> opens the
        // book straight to that chapter. TextLinkComponent routes any almanac://
        // click here through the global link-protocol registry.
        api.RegisterLinkProtocol("almanac", OnAlmanacLink);

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

        api.ChatCommands.Create("almpreview")
            .WithDescription("Author preview: load guides live from a disk folder, hot-reload on edit. Args: <folder> | reload | off")
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("arg"))
            .HandleWith(OnPreviewCommand);

        IlluminatedLogger.Info(api, "startup",
            "Illuminated loaded. Alt+J opens the book");
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

    // --- The book, and how it is presented --------------------------------

    /// <summary>
    /// The vanilla seraph pose the "Opened large book" clutter item plays when
    /// held (ClutterTypeProps.HeldIdleAnim path) — the character visibly holds
    /// an open book. Triggering it directly while the Almanac is open gives a
    /// third-person reading pose for free (Lamp's find, 2026-07-28).
    /// </summary>
    private const string ReadingAnimCode = "holdbothhands";

    /// <summary>
    /// Creates the book dialog if needed. Every construction site comes through
    /// here — a dialog built anywhere else would miss the physical mirror and have
    /// nothing to draw itself with.
    /// </summary>
    /// <summary>One step of the post-LevelFinalize warm pagination: build the dialog if
    /// it does not exist yet, paginate one chapter, and reschedule while work remains.
    /// Stands down the moment the book is actually open (the user got there first, and
    /// ComposeSpread's own path owns the cache from then on).</summary>
    private void WarmPaginateStep(ICoreClientAPI api)
    {
        try
        {
            EnsureDialog();
            if (bookDialog == null || bookDialog.IsOpened()) return;
            int left = bookDialog.WarmPaginate();
            if (left > 0) api.Event.RegisterCallback(_ => WarmPaginateStep(api), 40);
            else IlluminatedLogger.Info(api, "book", "Warm pagination complete; first open is prepaid");
        }
        catch (System.Exception e)
        {
            IlluminatedLogger.Warn(api, "book", $"Warm pagination stopped: {e.Message}");
        }
    }

    private void EnsureDialog()
    {
        if (bookDialog != null || capi == null) return;
        library ??= new GuideLibrary(capi, guidePacks);
        AttachRevealProbe();
        bookDialog = new GuiDialogIlluminatedBook(capi, library, () => homebasePos, EnsureHomebase, bookTabs);
        bookDialog.PauseOnOpenPref = () => pauseOnOpen;

        // Say so on the page when the climate was read from the player's feet rather than
        // their bound spawn. The reader is owed the difference: a Crops page reckoned from
        // where they happen to be standing can name a different planting season than their
        // homestead's, and nothing else on the page would give that away.
        bookDialog.HomebaseNote = () => homebaseProvisional
            ? "Reckoned from where you stand. The world is held still, so your bound spawn could not be asked after."
            : null;
        bookDialog.OnReadingChanged = reading =>
        {
            // Local truth first: the reader's own render never waits on the server
            // (in paused singleplayer that round-trip is frozen until close).
            ReadingHands.LocalReading = reading;
            clientChannel?.SendPacket(new ReadingState { Reading = reading });
        };
        bookDialog.PhysicalMirror = physicalView;
        lightSampler ??= new AmbientLightSampler(capi, () => illuminationFloor);
        bookDialog.LightSampler = lightSampler;
        bookDialog.LightEnabled = () => respectWorldLight;
        bookDialog.OnOpened += () => capi?.World.Player.Entity.AnimManager.StartAnimation(ReadingAnimCode);
        bookDialog.OnClosed += () =>
        {
            physicalView?.HideLive();
            capi?.World.Player.Entity.AnimManager.StopAnimation(ReadingAnimCode);
        };
    }

    /// <summary>
    /// Opens the book. The view is armed before TryOpen so not even the first
    /// frame draws uncaptured; a machine whose capture has already failed is
    /// told so instead, because there is nothing else to show.
    /// </summary>
    private void OpenBook()
    {
        if (physicalView == null || physicalView.CaptureUnavailable)
        {
            capi?.ShowChatMessage(CaptureLostLine);
            return;
        }
        EnsureDialog();
        if (bookDialog == null) return;
        physicalView.ShowLive(bookDialog);
        bookDialog.TryOpen();
    }

    /// <summary>What the player is told when the book cannot be drawn at all.</summary>
    private const string CaptureLostLine =
        "The Almanac cannot be drawn on this machine — its page needs an offscreen "
        + "render pass this graphics driver refused. See the client log for the reason.";

    /// <summary>
    /// Closes the book: the view stows it with the reverse draw-out and closes the
    /// dialog when the animation lands. It goes through the dialog's TryClose
    /// guard, which makes this idempotent: one Alt+J can reach both the game's own
    /// close and this handler, and asking twice must not read as "cancel the stow".
    /// </summary>
    private void CloseBook() => bookDialog?.TryClose();

    /// <summary>
    /// The capture pipeline failed (no framebuffer, no shader, a driver that
    /// refuses the offscreen pass). Since 0.2.0 there is no second presentation to
    /// fall back to, so this closes the book and says why, plainly, once: a book
    /// that silently refused to open would read as a broken mod, and the reason
    /// lives in a log the player has no cause to look at.
    /// </summary>
    private void OnCaptureUnavailable(string reason)
    {
        if (capi == null) return;
        IlluminatedLogger.Warn(capi, "physical",
            $"The Almanac's page could not be drawn ({reason}); the book is unavailable for this session.");

        bool wasOpen = bookDialog?.IsOpened() == true;
        physicalView?.HideLive();

        // This arrives mid-render, with the gui loop iterating the dialog list.
        // Closing the book from inside that loop is exactly what the stow path
        // avoids, so hand it to the next tick.
        capi.Event.RegisterCallback(_ =>
        {
            if (wasOpen) bookDialog?.TryClose();
            capi.ShowChatMessage(CaptureLostLine);
        }, 0);
    }

    private void LoadSettings()
    {
        try
        {
            if (!System.IO.File.Exists(SettingsPath)) return;
            var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<BookSettings>(System.IO.File.ReadAllText(SettingsPath));
            if (cfg != null)
            {
                pauseOnOpen = cfg.PauseOnOpen; loadedKnobs = cfg;
                respectWorldLight = cfg.RespectLight ?? true;
                illuminationFloor = IlluminationCurve.ClampFloor(cfg.LightFloor ?? IlluminationCurve.DefaultFloor);
                if (System.Enum.TryParse<GrowthMarksStyle>(cfg.CropMarks, true, out var st)) CropMarks = st;
            }
        }
        catch (Exception e)
        {
            IlluminatedLogger.Warn(capi!, "settings", $"Could not read book settings: {e.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            var cfg = new BookSettings
            {
                PauseOnOpen = pauseOnOpen,
                RespectLight = respectWorldLight,
                LightFloor = illuminationFloor,
                CropMarks = CropMarks.ToString(),
            };
            if (physicalView != null)
            {
                var p = physicalView;
                cfg.Tilt = p.TiltDeg; cfg.Yaw = p.YawDeg; cfg.Dist = p.Dist;
                cfg.Scale = p.Scale; cfg.Fov = p.FovDeg; cfg.Height = p.HeightOff;
                cfg.Bend = p.BendMax; cfg.Stiff = p.BendStiff; cfg.Lead = p.BendLead; cfg.Corner = p.BendCorner;
                cfg.FlipSlow = p.FlipSlow; cfg.FlipFast = p.FlipFast; cfg.FlipCurl = p.FlipCurl;
                cfg.FlipScale = p.FlipScale; cfg.FlipMax = p.FlipMax; cfg.FlipPaper = p.FlipPaper;
            }
            System.IO.File.WriteAllText(SettingsPath, Newtonsoft.Json.JsonConvert.SerializeObject(cfg));
        }
        catch (Exception e)
        {
            IlluminatedLogger.Warn(capi!, "settings", $"Could not save book settings: {e.Message}");
        }
    }

    /// <summary>
    /// The settings file as last read. Held because <see cref="LoadSettings"/> runs before
    /// the physical view exists (it is deliberately constructed after, so a throw in command
    /// registration cannot take the presentation with it), so the knobs cannot be applied at
    /// read time and are pushed into the view the moment it is built.
    /// </summary>
    private BookSettings? loadedKnobs;

    /// <summary>
    /// Pushes saved knob values into a freshly built view. Every knob is nullable and a null
    /// is left alone, so a settings file written by an earlier version keeps this version's
    /// shipped defaults for anything it never heard of, instead of zeroing it.
    /// </summary>
    private void ApplyLoadedKnobs()
    {
        if (loadedKnobs == null || physicalView == null) return;
        var c = loadedKnobs;
        var p = physicalView;
        if (c.Tilt.HasValue) p.TiltDeg = c.Tilt.Value;
        if (c.Yaw.HasValue) p.YawDeg = c.Yaw.Value;
        if (c.Dist.HasValue) p.Dist = c.Dist.Value;
        if (c.Scale.HasValue) p.Scale = c.Scale.Value;
        if (c.Fov.HasValue) p.FovDeg = c.Fov.Value;
        if (c.Height.HasValue) p.HeightOff = c.Height.Value;
        if (c.Bend.HasValue) p.BendMax = c.Bend.Value;
        if (c.Stiff.HasValue) p.BendStiff = c.Stiff.Value;
        if (c.Lead.HasValue) p.BendLead = c.Lead.Value;
        if (c.Corner.HasValue) p.BendCorner = c.Corner.Value;
        if (c.FlipSlow.HasValue) p.FlipSlow = c.FlipSlow.Value;
        if (c.FlipFast.HasValue) p.FlipFast = c.FlipFast.Value;
        if (c.FlipCurl.HasValue) p.FlipCurl = c.FlipCurl.Value;
        if (c.FlipScale.HasValue) p.FlipScale = c.FlipScale.Value;
        if (c.FlipMax.HasValue) p.FlipMax = c.FlipMax.Value;
        if (c.FlipPaper.HasValue) p.FlipPaper = c.FlipPaper.Value;
    }

    private class BookSettings
    {
        // Physical/flat was retired in 0.2.0. A settings file written by an older
        // version still carries it; Json.NET simply ignores the unknown member.
        /// <summary>Singleplayer reading pause (on by default; `.almbook pause off` to
        /// keep the days moving). Saved but inert in multiplayer.</summary>
        public bool PauseOnOpen = true;

        /// <summary>Shade the book by the light reaching the reader (`.almbook light`).
        /// Nullable like the knobs below: absent = the shipped default (on).</summary>
        public bool? RespectLight;
        /// <summary>Book brightness in total darkness (`.almbook light floor`).</summary>
        public float? LightFloor;

        /// <summary>How the Crops tab records growing (`.almbook marks`). Absent = tally.</summary>
        public string? CropMarks;

        // Framing, bend and flip knobs. Nullable on purpose: absent means "use the shipped
        // default", which is what lets a new knob ship without rewriting everyone's file.
        public float? Tilt, Yaw, Dist, Scale, Fov, Height;
        public float? Bend, Stiff, Lead, Corner;
        public float? FlipSlow, FlipFast, FlipCurl, FlipScale;
        public int? FlipMax;
        public bool? FlipPaper;
    }

    /// <summary>
    /// .almbook light — report, toggle, or set the darkness floor of the
    /// local-illumination shading (the book lit by the light reaching the reader).
    /// The no-arg report includes a live sampler snapshot, so the curve can be
    /// judged against real numbers instead of guessed.
    /// </summary>
    private TextCommandResult OnLightCommand(string[] parts)
    {
        if (capi == null) return TextCommandResult.Error("Client API unavailable");

        if (parts.Length < 2)
        {
            string state = respectWorldLight
                ? $"The book reads by the world's light (floor {illuminationFloor:0.00}). '.almbook light off' keeps it always bright."
                : "The book is always fully lit. '.almbook light on' reads by the world's light.";
            return TextCommandResult.Success($"{state}\n{AmbientLightSampler.Describe(capi, illuminationFloor)}");
        }

        string sub = parts[1].ToLowerInvariant();
        switch (sub)
        {
            case "on":
            case "off":
                respectWorldLight = sub == "on";
                lightSampler?.ResetSmoothing();
                SaveSettings();
                IlluminatedLogger.Info(capi, "light",
                    $"Local-illumination shading {(respectWorldLight ? "on" : "off")}");
                return TextCommandResult.Success(respectWorldLight
                    ? "The book now reads by the light that reaches you."
                    : "The book now ignores the world's light.");
            case "floor":
                if (parts.Length < 3 || !float.TryParse(parts[2],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float f))
                {
                    return TextCommandResult.Error(
                        $"Usage: .almbook light floor <{IlluminationCurve.MinFloor:0.00}..{IlluminationCurve.MaxFloor:0.0}> (currently {illuminationFloor:0.00})");
                }
                illuminationFloor = IlluminationCurve.ClampFloor(f);
                SaveSettings();
                IlluminatedLogger.Info(capi, "light", $"Illumination floor set to {illuminationFloor:0.00}");
                return TextCommandResult.Success(
                    $"In total darkness the book now renders at {illuminationFloor * 100f:0}% brightness.");
            default:
                return TextCommandResult.Error(
                    "Usage: .almbook light | .almbook light on|off | .almbook light floor <n>");
        }
    }

    /// <summary>
    /// .almbook marks — how the Crops tab keeps its record of what has been grown.
    /// Three hands for the same fact, because a tally, a rule and a count are three
    /// different amounts of bookkeeping to have staring back at you.
    /// </summary>
    private TextCommandResult OnMarksCommand(string[] parts)
    {
        if (capi == null) return TextCommandResult.Error("Client API unavailable");

        if (CropFamiliarity == null)
            return TextCommandResult.Success(
                "Nothing tracks what you have grown in this world, so the Crops tab keeps no record to draw.");

        if (parts.Length < 2)
            return TextCommandResult.Success(
                $"The Crops tab records growing as {Describe(CropMarks)}. '.almbook marks tally|fullness|counts' to change it.");

        string sub = parts[1].ToLowerInvariant();
        GrowthMarksStyle chosen = sub switch
        {
            "tally" or "tallies" => GrowthMarksStyle.Tally,
            "fullness" or "full" or "rule" => GrowthMarksStyle.Fullness,
            "counts" or "count" or "numbers" => GrowthMarksStyle.Counts,
            _ => (GrowthMarksStyle)(-1),
        };
        if ((int)chosen < 0)
            return TextCommandResult.Error("Usage: .almbook marks tally|fullness|counts");

        CropMarks = chosen;
        SaveSettings();
        // The tab caches its composed columns; drop them so the change shows on this open.
        bookDialog?.InvalidateCrops();
        IlluminatedLogger.Info(capi, "crops", $"Crop record style set to {chosen}");
        return TextCommandResult.Success($"The Crops tab now records growing as {Describe(chosen)}.");
    }

    private static string Describe(GrowthMarksStyle s) => s switch
    {
        GrowthMarksStyle.Tally => "a tally struck in the margin",
        GrowthMarksStyle.Fullness => "a rule inked as far as you have come",
        _ => "plain counts",
    };

    /// <summary>
    /// .almbook — report or set the presentation, and tune the physical book's
    /// framing live. Every knob applies on the next frame; finding the
    /// readability envelope should never cost a restart.
    /// </summary>
    private TextCommandResult OnBookCommand(TextCommandCallingArgs args)
    {
        if (capi == null || physicalView == null) return TextCommandResult.Error("Client API unavailable");

        string[] parts = ((args[0] as string)?.Trim() ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            if (physicalView.CaptureUnavailable)
                return TextCommandResult.Success(CaptureLostLine);
            return TextCommandResult.Success($"The Almanac reads as a book in your hands. {physicalView.Describe()}");
        }

        string verb = parts[0].ToLowerInvariant();
        switch (verb)
        {
            case "status":
                return TextCommandResult.Success(physicalView.Describe());
            case "reset":
                physicalView.Reset();
                SaveSettings();
                return TextCommandResult.Success($"Reset. {physicalView.Describe()}");
            case "hand":
                return OnHandKnob(parts);
            case "light":
                return OnLightCommand(parts);
            case "marks":
                return OnMarksCommand(parts);
            case "pause":
            {
                if (parts.Length < 2)
                    return TextCommandResult.Success(pauseOnOpen
                        ? "The world holds still while the book is open (singleplayer). '.almbook pause off' lets it run."
                        : "The world runs on while you read. '.almbook pause on' holds it still (singleplayer).");
                string setTo = parts[1].ToLowerInvariant();
                if (setTo != "on" && setTo != "off")
                    return TextCommandResult.Error("Usage: .almbook pause [on|off]");
                pauseOnOpen = setTo == "on";
                SaveSettings();
                // Answer immediately if the book is open right now, not on the next open.
                bookDialog?.ApplyPauseNow(pauseOnOpen);
                string note = capi.IsSinglePlayer && !capi.OpenedToLan ? ""
                    : " (applies in singleplayer; this session is not)";
                return TextCommandResult.Success((pauseOnOpen
                    ? "The world will hold still while the book is open."
                    : "The world will run on while you read.") + note);
            }
        }

        if (parts.Length < 2 || !float.TryParse(parts[1],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
        {
            return TextCommandResult.Error(
                "Usage: .almbook | .almbook pause [on|off] | .almbook light [on|off|floor <n>] | .almbook tilt|yaw|dist|scale|fov|height <n> | .almbook bend|stiff|lead|corner <n> | .almbook flipslow|flipfast|flipcurl|flipscale|flipmax <n> | .almbook flippaper <0|1> | .almbook model <0|1> | .almbook reset");
        }

        switch (verb)
        {
            case "tilt": physicalView.TiltDeg = v; break;
            case "yaw": physicalView.YawDeg = v; break;
            case "dist": physicalView.Dist = Math.Max(0.1f, v); break;
            case "scale": physicalView.Scale = Math.Max(0.05f, v); break;
            case "fov": physicalView.FovDeg = GameMath.Clamp(v, 10f, 120f); break;
            case "height": physicalView.HeightOff = v; break;
            case "model": physicalView.ModelEnabled = v != 0; break;
            case "quad": physicalView.QuadEnabled = v != 0; break;
            // The bend of a turning page. 'bend 0' restores the old rigid sheet.
            case "bend": physicalView.BendMax = GameMath.Clamp(v, 0f, 2.5f); break;
            case "stiff": physicalView.BendStiff = GameMath.Clamp(v, 0.2f, 6f); break;
            case "lead": physicalView.BendLead = GameMath.Clamp(v, 0.1f, 4f); break;
            case "corner": physicalView.BendCorner = GameMath.Clamp(v, 0f, 1.5f); break;
            // The leafing-through series played when a jump crosses more than one spread.
            case "flipslow": physicalView.FlipSlow = GameMath.Clamp(v, 0.02f, 0.6f); break;
            case "flipfast": physicalView.FlipFast = GameMath.Clamp(v, 0.02f, 0.6f); break;
            case "flipcurl": physicalView.FlipCurl = GameMath.Clamp(v, 0f, 3f); break;
            case "flipscale": physicalView.FlipScale = GameMath.Clamp(v, 0f, 4f); break;
            case "flipmax": physicalView.FlipMax = (int)GameMath.Clamp(v, 1f, 24f); break;
            case "flippaper": physicalView.FlipPaper = v != 0; break;
            default:
                return TextCommandResult.Error(
                    $"Unknown knob '{verb}'. pause, tilt, yaw, dist, scale, fov, height, "
                    + "bend, stiff, lead, corner, flipslow, flipfast, flipcurl, flipscale, flipmax, "
                    + "flippaper, model, quad, reset, status.");
        }

        // Tuning that does not survive a relaunch is tuning done twice.
        SaveSettings();
        IlluminatedLogger.Info(capi, "physical", $"almbook: {physicalView.Describe()}");
        return TextCommandResult.Success(physicalView.Describe());
    }

    /// <summary>
    /// `.almbook hand ...` — live-tune where the reading book sits in the character's
    /// hand (the in-world ReadingHands render). Mutates the dummy item's own
    /// tpHandTransform, so the next frame shows it; when the numbers look right they
    /// get baked into itemtypes/almanac.json as the shipped default. Session-only by
    /// design, the same posture as the physical-view knobs.
    /// </summary>
    private Vintagestory.API.Common.ModelTransform? handTransformOriginal;

    private TextCommandResult OnHandKnob(string[] parts)
    {
        var item = capi?.World.GetItem(new AssetLocation("almanacilluminated", "almanac"));
        if (item?.TpHandTransform == null) return TextCommandResult.Error("The reading book item is not available.");
        var tf = item.TpHandTransform;
        handTransformOriginal ??= tf.Clone();

        // No '=(' sequences: emoji chat mods (emojime is in the pack) read that as a
        // sad-face emoticon and eat the paren, mangling the whole readout.
        string Show() =>
            $"hand | t {tf.Translation.X:0.###} {tf.Translation.Y:0.###} {tf.Translation.Z:0.###}" +
            $" | r {tf.Rotation.X:0.#} {tf.Rotation.Y:0.#} {tf.Rotation.Z:0.#}" +
            $" | o {tf.Origin.X:0.###} {tf.Origin.Y:0.###} {tf.Origin.Z:0.###}" +
            $" | scale {tf.ScaleXYZ.X:0.###}";

        if (parts.Length < 2) return TextCommandResult.Success(Show());
        string knob = parts[1].ToLowerInvariant();
        if (knob == "reset")
        {
            var o = handTransformOriginal;
            tf.Translation.Set(o.Translation); tf.Rotation.Set(o.Rotation);
            tf.Origin.Set(o.Origin); tf.ScaleXYZ.Set(o.ScaleXYZ);
            return TextCommandResult.Success("Reset. " + Show());
        }
        if (parts.Length < 3 || !float.TryParse(parts[2],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
            return TextCommandResult.Error("Usage: .almbook hand | .almbook hand reset | .almbook hand tx|ty|tz|rx|ry|rz|scale <n>");

        switch (knob)
        {
            case "tx": tf.Translation.X = v; break;
            case "ty": tf.Translation.Y = v; break;
            case "tz": tf.Translation.Z = v; break;
            case "rx": tf.Rotation.X = v; break;
            case "ry": tf.Rotation.Y = v; break;
            case "rz": tf.Rotation.Z = v; break;
            case "scale": tf.ScaleXYZ.Set(v, v, v); break;
            default:
                return TextCommandResult.Error($"Unknown hand knob '{knob}'. tx, ty, tz, rx, ry, rz, scale, reset.");
        }
        return TextCommandResult.Success(Show());
    }

    // --- Author preview ---------------------------------------------------

    private TextCommandResult OnPreviewCommand(TextCommandCallingArgs args)
    {
        if (capi == null) return TextCommandResult.Error("Client API unavailable");

        string arg = (args[0] as string)?.Trim() ?? "";

        if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase))
        {
            if (previewLoc == null) return TextCommandResult.Success("Preview is not active.");
            StopPreviewWatcher();
            previewLoc = null;
            // Rebuild the library from the packaged guides and swap it back in.
            library = new GuideLibrary(capi, guidePacks);
        AttachRevealProbe();
            bookDialog?.ReloadLibrary(library);
            IlluminatedLogger.Info(capi, "preview", "Preview off — restored packaged guides");
            return TextCommandResult.Success("Preview off. Showing the packaged guides again.");
        }

        if (string.Equals(arg, "reload", StringComparison.OrdinalIgnoreCase))
        {
            if (previewLoc == null)
                return TextCommandResult.Error("Preview is not active. Run `.almpreview <folder-or-zip>` first.");
            ReloadPreview(announce: true);
            return TextCommandResult.Success($"Reloaded preview from {previewLoc.Describe()}.");
        }

        // No keyword: enable preview. Use the given target, else find the dev assets folder.
        PreviewSource.Location? loc;
        if (arg.Length > 0)
        {
            loc = PreviewSource.Resolve(arg);
        }
        else
        {
            string? def = PreviewSource.ResolveDefaultFolder(capi);
            if (def == null)
                return TextCommandResult.Error(
                    "Could not find the mod's guides folder automatically. Pass one: " +
                    "`.almpreview C:\\path\\to\\almanac\\guides` or `.almpreview C:\\path\\to\\mod.zip`.");
            loc = new PreviewSource.Location(false, def, null);
        }

        if (loc == null) return TextCommandResult.Error("Could not read that path.");

        // Validate the target exists before committing to it.
        if (loc.IsZip && !System.IO.File.Exists(loc.DiskPath))
            return TextCommandResult.Error($"No such zip: {loc.DiskPath}");
        if (!loc.IsZip && !System.IO.Directory.Exists(loc.DiskPath))
            return TextCommandResult.Error($"No such folder: {loc.DiskPath}");

        previewLoc = loc;
        StartPreviewWatcher(loc);
        ReloadPreview(announce: false);

        // Open the book on the preview so the author sees it immediately.
        EnsureDialog();
        if (bookDialog?.IsOpened() == false) OpenBook();

        IlluminatedLogger.Info(capi, "preview", $"Preview on — watching {loc.Describe()}");
        return TextCommandResult.Success(
            $"Preview on. Reading guides live from {loc.Describe()}. " +
            (loc.IsZip ? "Rebuild the zip to refresh. " : "Edit and save; the book reloads itself. ") +
            "`.almpreview off` to stop.");
    }

    /// <summary>Re-read the preview target, rebuild the library, and swap it into the open book.</summary>
    private void ReloadPreview(bool announce)
    {
        if (capi == null || previewLoc == null) return;

        var result = PreviewSource.Load(capi, previewLoc);
        var packs = new List<GuidePack>(result.Packs);
        if (result.Errors.Count > 0)
            packs.Insert(0, PreviewSource.BuildErrorChapter(result.Errors));

        library = new GuideLibrary(capi, packs);
        AttachRevealProbe();
        bookDialog?.ReloadLibrary(library);

        if (announce || result.Errors.Count > 0)
        {
            string msg = result.Errors.Count > 0
                ? $"[Almanac] Preview reloaded: {result.Packs.Count} ok, {result.Errors.Count} with errors — see the book."
                : $"[Almanac] Preview reloaded: {result.Packs.Count} guide(s).";
            capi.ShowChatMessage(msg);
        }
    }

    private void StartPreviewWatcher(PreviewSource.Location loc)
    {
        StopPreviewWatcher();
        // A folder: watch its *.json. A zip: watch the zip file itself in its
        // directory (you cannot watch inside an archive), re-reading on rebuild.
        string dir = loc.IsZip ? (System.IO.Path.GetDirectoryName(loc.DiskPath) ?? loc.DiskPath) : loc.DiskPath;
        string filter = loc.IsZip ? System.IO.Path.GetFileName(loc.DiskPath) : "*.json";
        try
        {
            var w = new System.IO.FileSystemWatcher(dir, filter)
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            w.Changed += OnPreviewFileEvent;
            w.Created += OnPreviewFileEvent;
            w.Deleted += OnPreviewFileEvent;
            w.Renamed += OnPreviewFileEvent;
            previewWatcher = w;
        }
        catch (Exception e)
        {
            IlluminatedLogger.Warn(capi!, "preview",
                $"Could not watch {dir} ({e.Message}); use `.almpreview reload` to refresh by hand.");
        }
    }

    private void StopPreviewWatcher()
    {
        if (previewWatcher == null) return;
        try { previewWatcher.EnableRaisingEvents = false; previewWatcher.Dispose(); }
        catch { /* disposing a dead watcher is fine */ }
        previewWatcher = null;
    }

    /// <summary>
    /// Watcher events fire on a thread-pool thread and editors often emit several
    /// per save. Debounce, and marshal the actual reload onto the main thread —
    /// touching the GUI off-thread crashes the game.
    /// </summary>
    private void OnPreviewFileEvent(object sender, System.IO.FileSystemEventArgs e)
    {
        long token = System.Threading.Interlocked.Increment(ref previewReloadToken);
        capi?.Event.RegisterCallback(_ =>
        {
            if (token != System.Threading.Interlocked.Read(ref previewReloadToken)) return;   // superseded by a later event
            if (previewLoc != null) ReloadPreview(announce: false);
        }, 250);
    }

    /// <summary>
    /// Handles an almanac:// link clicked anywhere in the UI. Opens the book (if
    /// shut) and navigates to the referenced chapter. Only chapter links are wired;
    /// unknown forms log and no-op so a stray link never throws.
    /// </summary>
    private void OnAlmanacLink(LinkTextComponent link)
    {
        if (capi == null) return;
        string href = link?.Href ?? "";
        const string chapterPrefix = "almanac://chapter/";
        if (!href.StartsWith(chapterPrefix))
        {
            IlluminatedLogger.Info(capi, "link", $"Unrecognized almanac link: {href}");
            return;
        }

        string rest = href.Substring(chapterPrefix.Length);
        int hash = rest.IndexOf('#');
        string id = hash >= 0 ? rest.Substring(0, hash) : rest;   // section anchors land on page 1 for now

        EnsureDialog();
        if (bookDialog == null) return;
        if (!bookDialog.IsOpened()) OpenBook();
        bookDialog.OpenToChapter(id);
    }

    private bool OnToggleBook(KeyCombination comb)
    {
        if (capi == null) return false;

        // The whole visible library, ordered: the book opens to the overview (or
        // first front matter) and navigates between chapters via internal links.
        EnsureDialog();
        if (bookDialog == null) return false;

        if (bookDialog.IsOpened()) CloseBook();
        else OpenBook();

        return true;
    }

    public override void Dispose()
    {
        // Only drop the handle if it still points at this instance: a client and a server
        // ModSystem both run in singleplayer, and a blind null would strand the survivor.
        if (ReferenceEquals(Instance, this)) Instance = null;
        ReadingHands.Dispose();
        StopPreviewWatcher();
        physicalView?.Dispose();
        physicalView = null;
        bookDialog?.Dispose();
        bookDialog = null;
        base.Dispose();
    }
}
