# The Almanac: Illuminated. Roadmap

Status of the build. The format spec is at `docs/SCHEMA.md` and is open for comment.

## Done

- **Guide-pack format spec (v0.1)** — `docs/SCHEMA.md`. Twice reviewed, every claim checked against the engine. Public for feedback.
- **Renderer spike** — native VS GUI composes a heavy chapter in 16 to 24 ms per spread on iGPU. vsimgui dropped.
- **Loader and gating** — discovers `almanac/guides/*.json` across all loaded mods (registers a custom `almanac` asset category), parses to GuidePack, gates chapters by their `gate` modid and blocks by `requires`.
- **Book at ~84% of screen, on parchment** — dark leather board, two cream pages, dark ink serif text.
- **Callouts** — author's-hand callout (brighter vellum, rounded double red border, wax seal with the author's initial, red heading) and sibling variants (tip, warning, lore) sharing the frame in their own hues.

## Next

- **Renderer completion**
  - Native recipe block: render the grid recipe via the handbook's own component (`recipe` = one recipe, `output` = every recipe for the item). Currently a placeholder.
  - Figure block: load and draw the texture, scale to fit the column, honor align. Currently a placeholder.
  - Hybrid paginator: flow sections across pages honoring `keepTogether` and `pageBreakBefore` so a section that does not fit shifts to the next page instead of clipping. Currently one section per page.
- **JSON Schema** — publish a JSON Schema for the format so authors get autocomplete and validation. Foundation for the web editor.
- **In-game author preview** — watch a guide folder and hot-reload the chapter on change. Real renderer, zero drift.
- **Navigation and IA** — A to Z index tabs, Contents and Journal tabs, top-edge bookmark ribbons, flip-to-search. Internal links jump here.
- **The magic (phase 3)** — Alt+J materialize animation, sprite page-turn with randomized sounds, speed-ramped flip-to-search, diegetic quest-complete notification.
- **Integrations (phase 4)** — Codex discovery renders as the Journal chapter, Progression Framework binds quest blocks to live state, server bridge sends guide manifests for server-side mods.

## Deferred

- **`table` block** — committed to v0.2. Needs a custom column-layout component (nothing native lays out a grid).
- **Web editor** — build and preview a guide pack in the browser, generated from the JSON Schema, hosted free.
- **Eyesome Script font** — held out of the repo until its license is confirmed. Re-add the file and the FontRegistry entry once cleared.
- **Legacy `almanaccodexilluminated`** — retire its Alt+J binding when this supersedes it.
- **Non-Windows font registration** — current custom-font path is Windows only (GDI). Add a fontconfig branch if needed.
