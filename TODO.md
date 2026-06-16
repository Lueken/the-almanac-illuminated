# The Almanac: Illuminated. Roadmap

Status of the build. The format spec is at `docs/SCHEMA.md` and is open for comment.

## Done

- **Guide-pack format spec (v0.1).** `docs/SCHEMA.md`. Twice reviewed, every claim checked against the engine. Public for feedback.
- **Renderer spike.** Native VS GUI composes a heavy chapter in 16 to 24 ms per spread on iGPU. vsimgui dropped.
- **Loader and gating.** Discovers `almanac/guides/*.json` across all loaded mods (registers a custom `almanac` asset category), parses to GuidePack, gates chapters by their `gate` modid and blocks by `requires`.
- **Book at ~84% of screen, on parchment.** Dark leather board, two cream pages, dark ink serif text.
- **Callouts.** Author's-hand callout (brighter vellum, rounded double red border, wax seal with the author's initial, red heading) and sibling variants (tip, warning, lore) sharing the frame in their own hues.
- **Native recipe block.** Reuses the handbook's own grid component. `recipe` renders one recipe by name, `output` renders every recipe that produces an item. Degrades to a quiet notice when nothing resolves.
- **Figure block.** Bakes a PNG onto the parchment via a Cairo surface pattern, scaled to the column with aspect preserved. `full` spans the column, `left` and `right` float with text beside them.
- **Hybrid paginator.** Measures each block at the page width and packs blocks into page-height columns. `keepTogether` holds a section whole, `pageBreakBefore` forces a fresh page, and a title never orphans from its first block. Replaces one-section-per-page.
- **JSON Schema.** `docs/guide-pack.schema.json`, mirroring the spec with per-block validation. Authors get autocomplete and live error checking via a `$schema` line. Foundation for the web editor.
- **Server overview and the contents block.** A gate-less chapter can set `overview: true` to pin first and become the landing page. The `contents` block fills itself from the live modlist (documented mods link, the rest show muted), `include` `added` or `all`. A copy-me template ships at `docs/examples/`. See SCHEMA.md section 11.
- **Multi-chapter navigation.** The book holds the whole library. Internal `almanac://chapter/<id>` links jump between chapters, Prev/Next read straight through the book across chapter boundaries, and Back and Contents buttons return. Two page readouts: this chapter, and the whole book. All chapters paginate once and cache.
- **Contents as its own tab.** A chapter may set `contents: true` to be the index page, kept out of the reading flow and reached by the Contents tab. The engine ships no live overview; it lives in the pack mod, with a tweakable template in `docs/examples/`.
- **Tab ribbons hanging off both edges.** The book sits in a wider dialog; the leather paints only the book region so ribbons read as leading off it. Left: Contents, Journal, then passed letters. Right: current letter and those ahead. Custom `GuiElementChapterTabs` draws and hit-tests on either edge (the native vertical tabs hit-test the wrong side and cannot be clicked when right-facing).
- **A-Z grouped index with a split.** Chapters group by first letter (ignoring a leading "The"); selecting a letter jumps to its first chapter and re-centers the split, the way a thumb-index moves as you read deeper. Only-present letters for now; range-folding (A-E, F-J) when packs grow.
- **Writable journal.** A file-saved personal notebook: one writing sheet per spread, `Save`, `+ Page` (adds a spread), Prev/Next within it, 3-second debounced auto-save plus save-on-close, persisted to `ModData/almanacilluminated/journal.json`.
- **Real book-frame art.** The open-book plate (leather board, brackets, clasp, aged pages) from Wanderer's Sketchbook by JeanPierre, used under his modder permission (credited in README), replaces the procedural rectangle. Pages, title, tabs, and footer map onto the art as fractions. Title is a centered running head; Home replaces Back; Esc/Alt+J close.
- **Page turn.** Click the bottom-outer corner (or it is a no-op at the book ends) to play the five-frame corner flip with a randomized page-turn sound, then swap. Faint `‹ ›` cues mark turnable corners; codex page numbers sit on each footer.
- **Reader polish.** Symmetric callout padding, air above section headings, breathing room below callouts and before links, gutter tightened.
- **Crops tab.** A computed `Crops` tab listing every growable food from the modlist — seed crops, **mushrooms** (foraged), berry bushes, fruit trees — grouped under those headers and source-flagged vanilla vs mod. Each card shows the produce icon, growing facts, and a planting line computed from the player's bound-spawn climate (the mod is now universal with a server sync channel; surface-sampled). Two columns per page. **Foragers Gamble gating:** reads the player's synced `WatchedAttributes["foragersGamble"]` knowledge and reveals each card in steps — silhouette while unidentified, facts-without-nutrition while learning, full card once known; full reveal when FG is absent. Views rebuild on book open so they match the food tooltips.
- **Weather tab.** A computed `Weather` tab: an almanac outlook for the home column's year. `HomeWeather` samples temperature + forecast precipitation across the current year into monthly aggregates, latitude, and a hemisphere-agnostic frost-free season; `WeatherRenderer` writes folksy prose (the year's temper, rain and drought, frost and season, snow). Character is taken from the sampled winter temperature, so it reads right at altitude and never contradicts the frost line. `.almweathersweep` validates the prose across latitudes.

## Next

- **Crops polish.** `kind · source` wraps in the narrow column — tighten. Optional: read FG's per-category config so unmasked categories are not silhouetted; a header/count on the Crops pages.
- **Weather polish.** Absolute temps are only real on loaded chunks (the sweep's far samples aren't trustworthy); consider a "Home" combined page pairing weather with planting; optional moon/calendar almanac.
- **A-Z range-folding.** Collapse sparse letter runs into ranges (A-E, F-J) and expand dense letters to singles, so a large pack's index fits the strip without overflow. Replaces only-present once chapter count grows.
- **In-game author preview.** Watch a guide folder and hot-reload the chapter on change. Real renderer, zero drift.
- **Navigation and IA, the rest.** Top-edge bookmark ribbons (pin a page), flip-to-search. `handbook://` and external URL links still need their handlers.
- **More magic.** Alt+J materialize animation, speed-ramped flip-to-search, diegetic quest-complete notification.
- **Integrations.** Codex discovery renders as the Journal chapter, Progression Framework binds quest blocks to live state, server bridge sends guide manifests for server-side mods.

## Deferred

- **`table` block.** Committed to v0.2. Needs a custom column-layout component, since nothing native lays out a grid.
- **Web editor.** Build and preview a guide pack in the browser, generated from the JSON Schema, hosted free.
- **Eyesome Script font.** Held out of the repo until its license is confirmed. Re-add the file and the FontRegistry entry once cleared.
- **Legacy `almanaccodexilluminated`.** Retire its Alt+J binding when this supersedes it.
- **Non-Windows font registration.** Current custom-font path is Windows only (GDI). Add a fontconfig branch if needed.
