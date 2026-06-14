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

## Next

- **In-game author preview.** Watch a guide folder and hot-reload the chapter on change. Real renderer, zero drift.
- **Navigation and IA.** A to Z index tabs, Contents and Journal tabs, top-edge bookmark ribbons, flip-to-search. Internal links jump here.
- **The magic (phase 3).** Alt+J materialize animation, sprite page-turn with randomized sounds, speed-ramped flip-to-search, diegetic quest-complete notification.
- **Integrations (phase 4).** Codex discovery renders as the Journal chapter, Progression Framework binds quest blocks to live state, server bridge sends guide manifests for server-side mods.

## Deferred

- **`table` block.** Committed to v0.2. Needs a custom column-layout component, since nothing native lays out a grid.
- **Web editor.** Build and preview a guide pack in the browser, generated from the JSON Schema, hosted free.
- **Eyesome Script font.** Held out of the repo until its license is confirmed. Re-add the file and the FontRegistry entry once cleared.
- **Legacy `almanaccodexilluminated`.** Retire its Alt+J binding when this supersedes it.
- **Non-Windows font registration.** Current custom-font path is Windows only (GDI). Add a fontconfig branch if needed.
