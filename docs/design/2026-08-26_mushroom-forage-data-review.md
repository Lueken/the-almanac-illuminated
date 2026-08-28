# Mushroom forage data: what exists, and what the card should say

Date: 2026-08-26
Scope: wild mushrooms and what the Crops tab can say about where they grow.
Out of scope and owned elsewhere: fruit trees, berry bushes, the Substrate mod, mushroom cultivation, soil sickness, the sown-crop family model.
Status: post-0.5. Nothing here ships from this document.

---

## 1. Verdict

There is a real feature here, and it is bigger than the empty cards suggest, but it does not build the way the question assumes. The data is rich: 81 mushroom species in the pack carry 76 distinct climate envelopes across temperature, rainfall, forest cover, host tree and placement, plus 28 species with real toxicity and 6 with psychedelic loads. It is not one shape group wearing 36 identical rows. The blocker is transport, not content. The climate envelopes live in `worldgen/blockpatches/*.json`, and `worldgen` is a server-only asset category, so a client-side book renderer cannot read a single one of them. Illuminated's catalogue runs client-side.

That blocker is cheap to clear, because Illuminated is already `side: universal` and already registers a server network channel it uses for the homebase handshake. The recommendation below splits the work in two: a free client-only half that fills the card today with facts that already reach the client, and a second half that adds one small server-to-client table and delivers the thing that actually matters, which is a habitat line and a verdict about the reader's own ground. Size it at one small build for Phase A and one medium build for Phase B.

---

## 2. The recommendation

### 2.1 What the card becomes

Keep the card shape the crop cards use. A tier removes lines from the bottom, it never rearranges them. Mushrooms get the same four slots a crop gets, with the sowing slot repurposed.

```
[icon]  Destroying angel    Mycodiversity
        Deadly.
        3° to 25° · under broadleaf · wants wet ground
        Found near you, Apr–Sep
```

```
[icon]  Turkey tail         Mycodiversity
        Sickening in quantity. Yields a blue dye.
        -2° to 28° · on broadleaf trunks · stands the winter
        Found near you, year-round
```

```
[icon]  Desert shaggy mane  Mycodiversity
        Sickening in quantity. Yields a red dye.
        9° to 35° · in the open · needs dry ground
        Too cold at your base
```

Line by line:

| Line | Content | Source | Side |
|---|---|---|---|
| Name line | display name, then the source mod | `CropEntry.DisplayName`, source mod (see the fix in 4.2) | client |
| Warning line | toxicity band, psychedelia, dye use | `block.NutritionProps.Health` / `.Psychedelic`; dye from the atlas | client (dye needs Phase B) |
| Habitat line | temperature band, placement + host tree, wetness or openness | the forage atlas | Phase B |
| Ground line | whether it is found at the reader's homebase, and in which months | the atlas envelope run against homebase climate | Phase B |

The habitat line replaces the literal string `"Foraged, not sown."` at `source/Crops/CropsRenderer.cs:289`. The ground line takes the `f.Sow` accent that `PlantingLine` uses at `CropsRenderer.cs:295`, because it is the same promise in the same colour: this is what your own ground will give you.

### 2.2 Exact fields and where each is read

**Client-side, available today, no packet (Phase A).**

| Field | Read from | Evidence |
|---|---|---|
| Toxicity | `block.NutritionProps.Health` | `vsapi/Common/Collectible/NutritionProperties.cs:96`; block attributes are on the client, see 4.1 |
| Psychedelia | `block.NutritionProps.Psychedelic` | `vsapi/Common/Collectible/NutritionProperties.cs:81` |
| Frost hardiness | `block.Attributes["mushroomProps"]["dieWhenTempBelow"]` | `vssurvivalmod/BlockEntity/BEMycelium.cs:105-112` proves it is the live kill threshold |
| Permanence | `block.Attributes["mushroomProps"]["dieAfterFruiting"]` | `BEMycelium.cs:115-119` |
| Side or ground growth | `block.Variant.ContainsKey("side")` | `BEMycelium.cs:218` |

`CropEntry.PlantBlock` is already populated for mushrooms at `source/Crops/CropCatalog.cs:415`, so every one of these is one dereference away inside `BuildCard`. Phase A needs no new plumbing at all.

Toxicity wording, banded off `Health`:

| Health | Word | Species |
|---|---|---|
| ≤ -20 | "Deadly." | death cap -50, destroying angel -50, funeral bell -40, fool's cone cap -20 |
| -20 < h ≤ -5 | "Poisonous." | 10 species, -6 to -10 |
| -5 < h < 0 | "Sickening in quantity." | 14 species, -1 to -2.5 |
| 0 | nothing | 53 species |

Psychedelia is a separate clause ("Brings visions."), because two of the six carry both.

**Server-read, needs one packet (Phase B).**

| Field | Read from | Side |
|---|---|---|
| minTemp / maxTemp | blockpatch entry | server only |
| minRain / maxRain | blockpatch entry | server only |
| minForest / maxForest | blockpatch entry | server only |
| placement (`OnTrees` / `UnderTrees` / absent) | blockpatch entry | server only |
| treeType (`Any` / `Conifer` / `Decidious` / `Tropical`) | blockpatch entry | server only |
| chance | blockpatch entry | server only |
| dye colour | barrel recipe registry, `recipes` is a server category | server only |

### 2.3 The transport

Illuminated's own server half, not TCM. Illuminated declares `side: universal` with `requiredOnServer: false` (`AlmanacIlluminated.csproj:16-18`), has a `StartServerSide` at `source/AlmanacIlluminatedModSystem.cs:201`, and registers a network channel there (`:205`) with a client twin at `:304`. The homebase handshake is already a request-response pair on that channel. The atlas is the same shape.

1. **Build it server-side.** On first request, walk `sapi.Assets.GetManyInCategory("worldgen", "blockpatches/")` (`vsapi/Common/Assets/IAssetManager.cs:74`), deserialize each entry, resolve `blockCodes[0]`, and keep only entries whose block is a `BlockMushroom`. Key each row by that block code. This is mod-agnostic by construction: it picks up vanilla, Mycodiversity, and any mushroom mod added later, with no per-mod knowledge and no shipped copy of anyone's data.
2. **Send it once.** `ForageAtlasRequest` / `ForageAtlasResponse` on the existing channel, requested alongside `HomebaseRequest`, cached for the session. 81 rows of a code plus eight numbers is roughly 5 KB.
3. **Send the homebase climate triple with it.** Rainfall, forest density and fertility at the bound spawn, read server-side with `EnumGetClimateMode.WorldGenValues`. The client can already sample its own temperature curve (`source/Crops/PlantingWindow.cs:49`) and its own rainfall (`source/Weather/HomeWeather.cs:72`), but I could not confirm that `ForestDensity` reads correctly on the client. Sending it removes the risk for the cost of three floats.
4. **Degrade honestly.** No atlas means an older server, or a server without Illuminated installed. The card falls back to the Phase A lines, which are still a large improvement on one flat sentence. This is the same graceful-degradation posture `ICropFamiliaritySource` already takes.

### 2.4 The habitat line, written out

`{cold}° to {heat}° · {where} · {ground}`

- **Where.** `OnTrees` gives "on {tree} trunks". `UnderTrees` gives "under {tree}". No placement plus a `maxForest` gives "in the open". Host tree words: `Decidious` (vanilla's spelling) reads "broadleaf", `Conifer` reads "conifer", `Tropical` reads "tropical", `Any` drops the qualifier and the line reads "under trees" or "on trunks".
- **Ground.** Print a wetness clause only where it says something. Vanilla's 45 species all sit at `minRain: 0.4` with no upper bound, so a rainfall clause on a vanilla mushroom is noise. Mycodiversity's 36 span 0.02 to 0.65 and four carry a `maxRain` ceiling. Rule: compare against the atlas's own median `minRain` and print "wants wet ground" at ≥ 0.6, "takes dry ground" at ≤ 0.25, and "needs dry ground" where a `maxRain` ceiling exists. Where a species carries `maxForest` instead, that clause becomes "not in deep wood".
- **Winter.** Where `dieWhenTempBelow` is -99, append "stands the winter". Five species qualify, and it is the one place the mycelium props say something a player can feel.

### 2.5 The ground line

Run the species envelope against the homebase's own climate, the same way `PlantingWindow.Compute` runs a crop's cold and heat limits against the twelve monthly temperatures. Mushrooms need two more axes, and they are not seasonal: rainfall and forest density are worldgen values, so they are a single pass-or-fail, while temperature varies month by month.

- Rainfall or forest cover out of band gives a flat refusal: "Too dry at your base", "Too wet at your base", "Your ground is too wooded", "Your ground is too open".
- Temperature in band for some months gives "Found near you, {months}", using `PlantingWindow.RangeString` unchanged.
- Temperature never in band gives "Too cold at your base" or "Too warm at your base".
- No homebase gives "Set a homebase to see where it grows", matching `CropsRenderer.cs:403`.

One honesty caveat belongs in the code comment, not on the page: a blockpatch envelope describes where worldgen was *willing* to place the species, not where it stands today. A patch can also be absent for want of a host tree, or for want of fertility ≥ 10 beneath it (`BEMycelium.cs:260`). "Found near you" is therefore a statement about suitable ground, and the wording should not promise a specific patch. The Forager's Memory is what promises specific patches, and it already does.

### 2.6 Ranking of the candidate facts

Asked for, and this is the order I would build in.

1. **Homebase verdict.** Highest. Actionable, falsifiable by walking outside, and the exact parallel to the planting line on a crop card.
2. **Toxicity.** High. Life and death, already client-side, free.
3. **Placement and host tree.** High. "Under conifers" tells a reader which direction to walk. 81 species split across three placements and four host types.
4. **Temperature band.** Medium-high. Feeds the verdict, and the bare figure is the crop card's own idiom so it belongs on the same line.
5. **Rainfall.** Medium, and only for the Mycodiversity half. Vanilla is flat.
6. **Forest cover.** Medium. Only 14 species carry a `maxForest`, but for those it is the whole story.
7. **Psychedelia.** Medium. Six species, cheap, client-side.
8. **Dye use.** Medium-low. Seven species, six colours, and a genuinely useful non-food reason to pick something.
9. **Permanence and frost hardiness.** Low-medium as a clause, and it earns its place because five conks never die back and a reader who knows that goes looking in winter.
10. **Rarity.** Low as printed text, and I recommend leaving it off the card. See 4.4.
11. **Satiety and edibility.** Zero. All 81 species are satiety 80 and food category Vegetable. There is no axis here. Never print it.

### 2.7 Tier gating: show it flat

Argued, not offered as a menu. **No familiarity gate. Print every line for every reader.** Three reasons.

The familiarity axis Illuminated has is a *farming* axis. `FarCropFamiliarity` counts what the reader has sown and harvested on farmland. Mushrooms are not sown. `CropsRenderer.cs:105` already routes every non-`SeedCrop` entry into the perennials bucket and past familiarity entirely, and that was the right call: warming a mushroom card by planting turnips would be incoherent.

There is a legitimate earned axis for mushrooms, and it is FOR rank, not FAR familiarity. TCM already counts mushroom gathers (`source/Domains/ForPatches.cs:325-339`) and already runs a four-tier fidelity ladder over them. But that ladder buys *location precision*, and it already exists and works. A second gate in the book over the same domain makes the reader earn the same knowledge twice, once as a map circle and once as a page.

Last, the credibility argument. A field guide is a book you read before you leave the house. Hiding the habitat line makes the book worse without making the world harder, because the world is not the book. The thing worth earning is finding the patch, and the Forager's Memory sells that. If a gate is ever wanted, hang it on FOR rank and gate only rarity, never habitat and never toxicity.

---

## 3. The data inventory

81 species. Counted from the authoritative asset files, not estimated. Temperatures are degrees C, rain and forest are 0 to 1. Blank means the field is absent, which means unbounded. "Frost" is `dieWhenTempBelow`; -99 means it never dies back. "Perm" marks `dieAfterFruiting: false`.

Sources: `Mycodiversity_1.0.8.zip!assets/game/worldgen/blockpatches/mycodiversity-mushrooms.json`, `Mycodiversity_1.0.8.zip!assets/game/blocktypes/plant/mycodiversity-mushroom.json` and `...-side.json`; `Vintagestory/assets/survival/worldgen/blockpatches/mushroom.json`, `.../blocktypes/plant/mushroom.json` and `.../mushroom-side.json`.

### Mycodiversity, 36 species

| Species | Placement | Tree | Temp | minRain | maxRain | maxForest | Chance | Health | Frost | Perm | Dye |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Artist's conk | OnTrees | Any | -2 to 28 | 0.5 | | | 8 | -2 | -99 | yes | |
| Beefsteak fungus | OnTrees | Decidious | 3 to 22 | 0.6 | | | 8 | 0 | 0 | | |
| Red belted conk | OnTrees | Conifer | -2 to 28 | 0.2 | | | 8 | -2 | -99 | yes | |
| Turkey tail | OnTrees | Decidious | -2 to 28 | 0.5 | | | 12 | -2 | -99 | yes | blue |
| Western varnished conk | OnTrees | Conifer | 0 to 18 | 0.25 | | | 8 | -2 | -99 | yes | |
| Desert shaggy mane | open | | 9 to 35 | 0.02 | 0.36 | 0.2 | 0.08 | -2 | 0 | | red |
| False earthstar | open | | -4 to 21 | 0.3 | | 0.2 | 0.08 | -2 | 0 | | |
| Fried chicken mushroom | open | | -2 to 18 | 0.4 | | 0.2 | 0.08 | 0 | 0 | | |
| Parasol | open | | 3 to 22 | 0.5 | | 0.2 | 0.08 | 0 | 0 | | |
| Shaggy mane | open | | -2 to 27 | 0.5 | | 0.3 | 0.08 | 0 | 0 | | |
| Stinkhorn | open | | 5 to 18 | 0.5 | | 0.2 | 0.08 | 0 | 0 | | |
| Yellow stainer | open | | -2 to 18 | 0.5 | | 0.2 | 0.08 | -6 | 0 | | |
| Aniseed funnel | UnderTrees | Any | -2 to 21 | 0.65 | | | 1.2 | 0 | 0 | | |
| Aspen bolete | UnderTrees | Decidious | -2 to 11 | 0.4 | | | 1.6 | 0 | 0 | | |
| Birch bolete | UnderTrees | Decidious | 0 to 22 | 0.4 | | | 1.6 | 0 | 0 | | |
| Blewit | UnderTrees | Any | -2 to 21 | 0.5 | | | 2 | 0 | 0 | | |
| Blue chanterelle | UnderTrees | Conifer | 3 to 10 | 0.5 | | | 0.64 | 0 | 0 | | gray |
| Candy cap | UnderTrees | Any | 3 to 18 | 0.5 | | | 1.6 | 0 | 0 | | |
| Coccora | UnderTrees | Decidious | 10 to 17 | 0.4 | 0.7 | | 1.6 | 0 | 0 | | |
| Destroying angel | UnderTrees | Decidious | 3 to 25 | 0.5 | | | 1.6 | **-50** | 0 | | |
| Dyer's polypore | UnderTrees | Conifer | -2 to 21 | 0.5 | | | 1.6 | -2 | 0 | | yellow |
| Flat-topped club coral | UnderTrees | Conifer | -2 to 21 | 0.4 | | | 1.2 | 0 | 0 | | |
| Green earth tongue | UnderTrees | Any | 4 to 18 | 0.5 | 0.85 | | 0.8 | -2 | 0 | | |
| Hawk wing | UnderTrees | Conifer | -2 to 20 | 0.4 | | | 1.2 | 0 | 0 | | green |
| Hedgehog | UnderTrees | Conifer | 0 to 22 | 0.4 | | | 2 | 0 | 0 | | |
| Matsutake | UnderTrees | Conifer | 5 to 21 | 0.35 | | | 1.2 | 0 | 0 | | |
| Bloodred webcap | UnderTrees | Conifer | 3 to 20 | 0.5 | | | 1.6 | -2 | 0 | | red |
| Purple fairy club | UnderTrees | Conifer | -2 to 11 | 0.5 | | | 0.8 | 0 | 0 | | |
| Queen bolete | UnderTrees | Decidious | 12 to 17 | 0.5 | | | 1.6 | 0 | 0 | | orange |
| Questionable stropharia | UnderTrees | Any | 3 to 19 | 0.5 | | | 1.6 | -2 | 0 | | |
| Red ramaria | UnderTrees | Any | 7 to 22 | 0.6 | | | 1.2 | 0 | 0 | | |
| Shrimp mushroom | UnderTrees | Conifer | -2 to 18 | 0.5 | | | 1.6 | 0 | 0 | | |
| Slippery jack | UnderTrees | Decidious | 4 to 18 | 0.5 | | | 2 | 0 | 0 | | |
| Sulfur tuft | UnderTrees | Any | 3 to 19 | 0.5 | | | 1.6 | -8 | 0 | | |
| Winter chanterelle | UnderTrees | Conifer | -2 to 25 | 0.4 | | | 2 | 0 | 0 | | |
| Yellow ramaria | UnderTrees | Decidious | -2 to 22 | 0.4 | | | 1.6 | -2 | 0 | | |

### Vanilla, 45 species

Every vanilla species sits at `minRain: 0.4` with no ceiling, so the rainfall column is omitted. "Psy" is the psychedelic load.

| Species | Placement | Tree | Temp | maxForest | Chance | Health | Psy | Frost | Perm |
|---|---|---|---|---|---|---|---|---|---|
| Bearded tooth | OnTrees | Decidious | 0 to 30 | | 8 | 0 | | 0 | |
| Chicken of the woods | OnTrees | Decidious | -15 to 20 | | 8 | 0 | | 0 | |
| Deer ear | OnTrees | Tropical | -5 to 50 | | 8 | 0 | | 0 | |
| Dryad saddle | OnTrees | Any | -15 to 20 | | 8 | 0 | | 0 | |
| Funeral bell | OnTrees | Conifer | -20 to 50 | | 8 | **-40** | | 0 | |
| Liver mushroom | OnTrees | Tropical | 10 to 50 | | 8 | 0 | | 0 | |
| Pink bonnet | OnTrees | Tropical | -5 to 50 | | 8 | -10 | | 0 | |
| Pink oyster | OnTrees | Any | 15 to 20 | | 4 | 0 | | 0 | |
| Reishi | OnTrees | Decidious | 10 to 50 | | 3.2 | 0 | | 0 | |
| Shiitake | OnTrees | Decidious | -6 to 23 | | 4 | 0 | | 0 | |
| Tinder hoof | OnTrees | Decidious | -15 to 10 | | 12 | 0 | | -99 | yes |
| White oyster | OnTrees | Conifer | 0 to 20 | | 6.4 | 0 | | 0 | |
| Blue meanie | open | Any | 15 to 20 | | 0.01 | 0 | 2 | 0 | |
| Field mushroom | open | | 0 to 10 | 0.2 | 0.08 | 0 | | 0 | |
| Fool's cone cap | open | Any | 0 to 20 | | 0.01 | **-20** | | 0 | |
| Liberty cap | open | Any | -10 to 15 | | 0.01 | 0 | 1.4 | 0 | |
| Puffball | open | | -3 to 21 | 0.2 | 0.08 | 0 | | 0 | |
| Red wine cap | open | | 0 to 30 | 0.2 | 0.08 | 0 | | 0 | |
| Wavy cap | open | Any | 3 to 25 | | 0.01 | 0 | 0.8 | 0 | |
| Almond mushroom | UnderTrees | Any | 1 to 21 | | 2.4 | 0 | | 0 | |
| Bitter bolete | UnderTrees | Any | -2 to 11 | | 1.2 | -1 | | 0 | |
| Black trumpet | UnderTrees | Decidious | -4 to 21 | | 0.4 | 0 | | 0 | |
| Chanterelle | UnderTrees | Any | -1 to 19 | | 2.4 | 0 | | 0 | |
| Common morel | UnderTrees | Any | -1 to 30 | | 0.8 | 0 | | 0 | |
| Death cap | UnderTrees | Any | -20 to 20 | | 1.6 | **-50** | | 0 | |
| Devil bolete | UnderTrees | Decidious | 18 to 30 | | 0.8 | -10 | | 0 | |
| Devil's tooth | UnderTrees | Conifer | -15 to 20 | | 0.2 | -2 | | 0 | |
| Earthball | UnderTrees | Conifer | -3 to 20 | | 1.6 | -8 | | 0 | |
| Elfin saddle | UnderTrees | Conifer | -15 to 20 | | 0.8 | -7 | | 0 | |
| Fly agaric | UnderTrees | Any | -20 to 23 | | 2.4 | -6.5 | 0.4 | 0 | |
| Gold cap | UnderTrees | Any | -1 to 30 | | 0.01 | 0 | 1 | 0 | |
| Gold drop milk cap | UnderTrees | Decidious | -2 to 22 | | 2 | -2.5 | | 0 | |
| Green cracked russula | UnderTrees | Any | -18 to 12 | | 0.8 | 0 | | 0 | |
| Honey mushroom | UnderTrees | Any | 0 to 30 | | 0.01 | 0 | | 0 | |
| Indigo milk cap | UnderTrees | Any | 3 to 30 | | 0.64 | 0 | | 0 | |
| Jack o' lantern | UnderTrees | Any | -3 to 25 | | 2 | -6 | | 0 | |
| King bolete | UnderTrees | Any | -5 to 24 | | 0.8 | 0 | | 0 | |
| Laughing jim | UnderTrees | Conifer | -10 to 10 | | 0.01 | -10 | 0.6 | 0 | |
| Lobster | UnderTrees | Any | -20 to 30 | | 2 | 0 | | 0 | |
| Orange oak bolete | UnderTrees | Decidious | 0 to 10 | | 1.6 | 0 | | 0 | |
| Paddy straw | UnderTrees | Any | 12 to 30 | | 1.6 | 0 | | 0 | |
| Saffron milk cap | UnderTrees | Conifer | -10 to 15 | | 0.4 | 0 | | 0 | |
| Sickener | UnderTrees | Conifer | -3 to 27 | | 0.01 | -7 | | 0 | |
| Violet webcap | UnderTrees | Decidious | -20 to 12 | | 1.2 | 0 | | 0 | |
| Witch hat | UnderTrees | Decidious | -15 to 20 | | 1.6 | 0 | | 0 | |

### Totals

| Measure | Value |
|---|---|
| Species in the pack | 81 (45 vanilla, 36 Mycodiversity) |
| Distinct climate envelopes | 76 of 81; only five pairs collide |
| Distinct temperature bands | 32 vanilla, 24 Mycodiversity |
| Distinct rainfall floors | 1 vanilla (0.4), 9 Mycodiversity (0.02 to 0.65) |
| Species with a rainfall ceiling | 4, all Mycodiversity |
| Species with a forest-cover ceiling | 14 |
| Toxic species | 28 (14 vanilla, 14 Mycodiversity) |
| Deadly species (health ≤ -20) | 4 |
| Psychedelic species | 6, all vanilla |
| Species that never die back | 5 |
| Dye sources | 7 species, 6 colours, all Mycodiversity |
| Distinct satiety values | 1 (every species is 80) |

Dye map, from `Mycodiversity_1.0.8.zip!assets/mycodiversity/recipes/barrel/dye/*.json`: turkey tail to blue, desert shaggy mane and bloodred webcap to red, dyer's polypore to yellow, hawk wing to green, blue chanterelle to gray, queen bolete to orange. Each is 4 caps plus 4 litres of water, 8 seal hours.

---

## 4. Findings

### 4.1 BLOCKER: worldgen blockpatch data is not readable from the client

**CONFIRMED.** `AssetCategory.worldgen = new AssetCategory("worldgen", true, EnumAppSide.Server)` at `vsapi/Common/Assets/AssetCategory.cs:28`. The side mask is enforced when assets are registered: `if ((origPath.Category.SideType & api.Side) == 0) continue;` at `stratum/VSEssentials/IntermodTools.cs:59`, under the comment "Some asset categories are not loaded on the client or server." The same mask drives the patch loader's not-found diagnostics at `stratum/VSEssentials/Loading/JsonPatchLoader.cs:504-509`.

Consequence: `capi.Assets` will never contain `worldgen/blockpatches/mushroom.json` or `mycodiversity-mushrooms.json`, on a dedicated server or otherwise. Every temperature, rainfall, forest and placement figure in section 3 is unreachable from `CropsRenderer`. This is the reason the feature cannot be built as a pure client-side read, and it is why section 2.3 exists.

The counterpart is that block *attributes* do reach the client, because blocktypes are sent as a resolved block registry rather than as raw assets. Proof by vanilla precedent: the client-side handbook reads `block.Attributes["handbook"]["groupBy"]` at `vssurvivalmod/Systems/Handbook/CollectibleBehaviorHandbookTextAndExtraInfo.cs:433`. Proof by Illuminated's own shipped code: `source/Crops/CropCatalog.cs:349-350` reads `block.Attributes?["stopBelowTemperature"]` for berry bushes on the client. So `mushroomProps` and `nutritionProps` are already in hand.

### 4.2 HIGH: all 36 Mycodiversity species are labelled "vanilla" on their cards

**CONFIRMED.** Mycodiversity registers its blocks in the `game` domain: `Mycodiversity_1.0.8.zip!assets/game/blocktypes/plant/mycodiversity-mushroom.json` has `"code": "mushroom"` with no domain prefix on its drops (`"code": "mushroom-{type}-normal"` at the `drops` block). `CropCatalog.cs:413` sets `SourceDomain = produce.Collectible.Code.Domain`, which is `game`, and `CropEntry.Vanilla` at `CropCatalog.cs:40` returns true for `game`. `CropsRenderer.cs:281` then prints the literal word "vanilla".

Every Mycodiversity mushroom currently claims to be part of the base game. This is a small, self-contained correctness bug and it is worth fixing whether or not the rest of this document is built, because the source line is the card's only provenance claim. The fix is to resolve provenance from the asset origin that defined the block rather than from the block's code domain. That resolution is server-side information, so it can ride the same atlas packet as one extra string per row.

### 4.3 HIGH: the climate envelope is genuinely per-species, not per-shape-group

**CONFIRMED.** 81 rows produce 76 distinct envelopes over the tuple (placement, treeType, minTemp, maxTemp, minRain, maxRain, minForest, maxForest). Only five pairs collide, and every collision is between two species with different names, different toxicity or different rarity, so no two cards would read identically once the warning line is present.

Within Mycodiversity alone: 36 rows, 35 distinct envelopes, 24 distinct temperature bands, 9 distinct rainfall floors, 3 placements, 4 host-tree types. The one collision is question mark stropharia and sulfur tuft, which share `UnderTrees / Any / 3 to 19 / minRain 0.5`, and they separate on toxicity (-2 against -8).

This directly answers the "36 identical rows" worry. It is not a risk here. The risk is the opposite one: printing the rainfall clause on vanilla species, where all 45 sit at exactly 0.4 and the clause would be pure filler. Section 2.4 handles that by comparing against the atlas median rather than printing unconditionally.

### 4.4 MEDIUM: rarity is real but not comparable across placement classes

**CONFIRMED.** `BlockPatch.Chance` is multiplied by a different constant in each of three generators before it becomes a probability:

- `UnderTrees`: `0.003f * forestNess * bPatch.Chance` per tree, `stratum/VSEssentials/Systems/WorldGen/Standard/ChunkGen/8.GenVegetationAndPatches/Treegen/ForestFloorSystem.cs:219`
- `OnTrees`: `3 * forestNess * blockPatch.Chance` per tree, `ForestFloorSystem.cs:285`
- Surface: `blockPatch.Chance` per chunk, `GenVegetationAndPatches.cs:268`

The `OnTrees` constant is a thousand times the `UnderTrees` constant, and the surface path counts chunks rather than trees. A single rarity column mixing them lies. The spread inside each class:

| Class | Species | Chance range | Spread | Distinct values |
|---|---|---|---|---|
| OnTrees | 17 | 3.2 to 12 | 4x | 5 |
| UnderTrees | 50 | 0.01 to 2.4 | 240x | 9 |
| Surface | 14 | 0.01 to 0.08 | 8x | 2 |

This corrects the "roughly 100x rarity spread" figure carried into this review. The honest numbers are 150x within Mycodiversity, 1200x across the whole pack, and neither is a single meaningful axis. Recommendation stands: leave rarity off the card in the first build. It is a claim a reader cannot check in five minutes, which is exactly the kind of line that costs a page its credibility. If it is wanted later, band it inside its own placement class and only for `UnderTrees`, where the spread is real.

### 4.5 MEDIUM: satiety is a dead axis and must not be printed

**CONFIRMED.** All 81 species carry satiety 80 raw and 120 in a meal, food category Vegetable. `Mycodiversity_1.0.8.zip!assets/game/blocktypes/plant/mycodiversity-mushroom.json` sets `"saturation": 80` in every `nutritionPropsByType` branch, the side file matches, and vanilla's `assets/survival/blocktypes/plant/mushroom.json` sets `satiety: 80` throughout. Health is the only nutritional axis that varies. An edibility or satiety line would print the same number 81 times.

### 4.6 MEDIUM: mushroomProps carries two live per-species facts the client already has

**CONFIRMED.** `BlockEntityMycelium` reads `block.Attributes["mushroomProps"]` into a `MushroomProps` at `vssurvivalmod/BlockEntity/BEMycelium.cs:192-194`, then acts on both fields every server tick: `DieWhenTempBelow` destroys the standing flush when local temperature drops below it (`:105-112`), and `DieAfterFruiting` destroys it after the fruiting window (`:115-119`). These are real behaviours, not decoration, and they sit on the block attributes so they are already on the client.

Five species set `dieWhenTempBelow: -99` and `dieAfterFruiting: false`: artist's conk, red belted conk, turkey tail, western varnished conk (`mycodiversity-mushroom-side.json`) and tinderhoof (`assets/survival/blocktypes/plant/mushroom-side.json`). They stand through winter and never die back. That is the single most useful client-only fact available today.

Two more facts from the same file, both universal rather than per-species, and both worth a line in the tab's own intro rather than on any card: mycelium only advances its growing clock above 5°C (`BEMycelium.cs:129`), and ground mushrooms only place on a block with fertility ≥ 10 (`BEMycelium.cs:260`).

### 4.7 MEDIUM: the Forager's Memory does not overlap this, and should not be extended to cover it

**CONFIRMED.** `AlmanacSpotsLayer` is a private per-player marker map of spots the reader has personally picked. It records the species names picked at each spot (`source/Overlay/AlmanacSpotsLayer.cs:182 AddName`), the patch state, standing cap counts and a regrow ETA, and it degrades location precision to the viewer's FOR rank server-side before sending (`:333-420`). Its hover text (`:805-874`) says things like "mushrooms, ready" plus a name list, and at Master and above one line per patch with counts and a clock.

It answers "where did I find this, and is it back yet". A mushroom card answers "where in the world does this grow, and does my ground suit it". Neither can substitute for the other, and the map is the wrong surface for a habitat envelope because the envelope is not a place. Build the card. Do not extend the layer.

There is one clean seam worth noting: the layer already holds the reader's own species-name list. A later version of the card could mark the species the reader has actually picked, which is a memory fact rather than an earned gate, and it would come from TCM rather than from the atlas.

### 4.8 MEDIUM: Mycodiversity ships a biome table that nothing in this pack reads

**CONFIRMED.** `Mycodiversity_1.0.8.zip!assets/mycodiversity/config/biomes.json` holds `NorthernRealms`, `SouthernRealms` and a `BlockPatchBiomes` map with all 36 species against named real-world realms ("pacific nearctic", "australasian" and so on). The mod's own changelog names the consumer: "Fixed the hedgehog mushroom being in Australia breaking Biomes" at 1.0.1. That capitalised "Biomes" is a separate mod.

No mod named Biomes appears in the 2026-08-19 Quire mod list (203 entries), and none of Mycodiversity's own blockpatch entries carry a `biome` or `biomes` field, both of which `BlockPatchConfig.IsPatchSuitableAt` would honour (`stratum/VSEssentials/.../BlockPatchConfig.cs:49-61`). So this file is inert in this pack.

It is worth knowing anyway, for two reasons. First, `config` is a Universal asset category (`vsapi/Common/Assets/AssetCategory.cs:16`), so unlike the blockpatches this file *is* client-readable. Second, if the Biomes mod is ever added, the realm names would make a far more evocative habitat line than a temperature band. That is a future option, not a current one, and the card must not print realm names while nothing enforces them.

### 4.9 LOW: Mycodiversity 1.0.8 is current, and the mushroom data is byte-identical to 1.0.7

**CONFIRMED.** The live server runs 1.0.8 (`projects/briefs/vs-quire-server/2026-08-19_quire-mod-list.csv`, and `vs-workshop/.server-sync/mods-mirror/Mycodiversity_1.0.8.zip`). The ModDB API reports 1.0.8 as the latest release, dated 2026-07-22. The copies under `%APPDATA%/VintagestoryData/ModsByServer/` are all 1.0.7 and all byte-identical to each other (md5 `b7c6d24de539c18ca463bb263c1fc663`), dated 2026-06-30 to 2026-07-10, so that cache is between 47 and 57 days stale.

Diffing 1.0.7 against 1.0.8: the only changes are the removal of the Expanded Foods compatibility tree (34 files), four lang files, `changelog.txt` and `modinfo.json`. `mycodiversity-mushroom.json`, `mycodiversity-mushroom-side.json`, `mycodiversity-mushrooms.json` (both the world properties and the blockpatches), `config/biomes.json`, the dye recipes and the spore patches are all unchanged. Every figure in section 3 is therefore correct for the version actually running.

`modinfo.json` in 1.0.8: `"Type": "content"`, `"ModID": "mycodiversity"`, `"Version": "1.0.8"`, `"Authors": ["sheepon"]`, `"Side": "Universal"`, `"RequiredOnServer": true`, empty `"Website"`, no dependencies. There is no DLL in the zip. This is a pure content mod.

### 4.10 LOW but decisive: no licence, so read it at runtime and never ship a copy

**CONFIRMED that no licence exists.** No `LICENSE`, `LICENCE`, `COPYING` or similar file anywhere in `Mycodiversity_1.0.8.zip` (559 entries in 1.0.7, fewer in 1.0.8, checked exhaustively). No licence field in `modinfo.json`. The ModDB API record for `mycodiversity` carries no licence field. The linked source repository, `https://github.com/sheep0n/mycodiversity`, reports `"license": null` on the GitHub API, and `GET /repos/sheep0n/mycodiversity/license` returns 404.

Absent a licence, the default is that the author retains all rights. That is a statement about the situation, not legal advice, and the practical rule that follows is simple and happens to match the better engineering answer:

- **Reading at runtime is fine and is what the recommendation does.** The atlas is built by walking the assets already loaded into the user's own server from the user's own copy of the mod. Nothing is copied and nothing is redistributed. This is what every Vintage Story mod does with every other mod's blocks.
- **Do not hardcode the table into Illuminated, and do not ship a transcribed copy in Illuminated's assets.** Individual temperature figures are facts and are weakly protected at best, but a 36-row selected and arranged table is a compilation, and the modding community's own norm against redistributing another author's assets is clearer than any copyright analysis. It would also go stale the first time sheepon releases 1.0.9.
- **If a copy is ever genuinely wanted**, there is a public repository and an issue tracker, so ask.

The runtime read has a second benefit that makes this an easy call: it works for vanilla, Mycodiversity, and any mushroom mod added to the pack later, with no per-mod knowledge and no maintenance.

### 4.11 LOW: the card count should be 81, and I could not reconcile "roughly 24"

`BuildMushrooms` (`CropCatalog.cs:396-421`) admits any `BlockMushroom` whose `state` variant is not `harvested`, and `Build` dedupes on produce code (`:62-64`). Every mushroom block in the pack drops itself: `mushroom-{mushroom}-normal` for ground species and `mushroom-{mushroom}-normal-north` for side species, in both vanilla (`assets/survival/blocktypes/plant/mushroom.json:158-160` and `mushroom-side.json:166-168`) and Mycodiversity. So the four side rotations collapse to one card and each species yields exactly one card. 81 cards.

Nothing else in the pack adds mushroom species. I scanned every zip named in the 2026-08-19 mod list against the local mirror for `BlockMushroom`, for mushroom world properties, and for patches targeting the vanilla mushroom blocktypes. Only Mycodiversity defines species. Substrate patches one attribute onto the vanilla files (`substrate_2.0.0.zip!assets/substrate/patches/mushroom.json` adds `sporeharvestable: true`), Expanded Foods adds baking and pie properties, and Food Shelves defines a basket. None adds a variant.

The most likely explanation for the observed count is that 24 was what fit on the visible pages rather than the whole section, since `PackCards` (`CropsRenderer.cs:464-491`) flows cards into page-height columns and the mushroom group runs across several page turns. That is LIKELY, not confirmed. See section 5.

---

## 5. What I could not verify

1. **The "roughly 24 cards" observation.** I could not reproduce it. The assets say 81 and the code path says one card per species. No client log containing the `CropCatalog.LogSummary` line ("Catalog: N growable(s)") exists on this machine; the newest client log is from 2026-08-09 and holds nothing relevant. The page-turn explanation above is reasoning, not evidence. The cheap check is to open the book, run the catalogue log, and read the count off `LogSummary`.

2. **Whether `ForestDensity` reads correctly on the client.** `EnumGetClimateMode.WorldGenValues` populates `ClimateCondition.ForestDensity` (`vsapi/Common/API/IBlockAccessor.cs:101`), and every `WorldGenValues` call I found in `vssurvivalmod` is on a server path. Illuminated's own shipped client code only ever asks for temperature (`PlantingWindow.cs:49`) or temperature and rainfall (`HomeWeather.cs:72`). Section 2.3 sidesteps this by sending the homebase climate from the server, which costs three floats. If a client-side read is wanted instead, log `GetClimateAt(homebase, WorldGenValues).ForestDensity` at the homebase and confirm it is not zero.

3. **The exact packet cost and whether one send is enough.** I sized the atlas at roughly 5 KB from 81 rows, but I did not measure the serialised form and did not check it against the survival-mode 1 MB packet cap. It is far below any plausible limit, so this is a note rather than a concern.

4. **Whether the client can see the barrel recipe registry.** `recipes` is a server-only asset category (`AssetCategory.cs:27`), and I did not trace whether resolved barrel recipes are separately synced to the client the way blocktypes are. The recommendation routes the dye fact through the atlas packet, which makes the question moot, but if a client-side read does work then Phase A could carry the dye line too.

5. **Live server state.** Everything about the pack composition comes from `projects/briefs/vs-quire-server/2026-08-19_quire-mod-list.csv` and the local `.server-sync/mods-mirror/`, not from a live SFTP read. The CSV is seven days old at the time of writing. Mycodiversity's version and content are independently confirmed against ModDB, so the mushroom figures do not depend on the CSV; the "no other mod adds mushrooms" claim does.

6. **The five envelope collisions in practice.** I confirmed them as tuple equality across the eight worldgen fields. I did not check whether the pairs also share a `randomMapCodePool` entry, which would make them genuinely co-located rather than merely climate-equivalent. It does not affect the card design.
