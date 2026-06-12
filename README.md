# The Almanac: Illuminated

A living questbook for the modded world. Pull the Almanac from your back pocket (Alt+J): chapters of guides authored by mod creators and pack authors, appearing only for the mods loaded in your world — with bookmarks, search, page turns, and tracked objectives.

**Status: Phase 0 — renderer spike.**

Design of record lives in the agentic-os workspace: `projects/briefs/the-almanac-illuminated/` (brief v0.1 + prior-art teardown).

## Phase 0 question

Can native VS GUI composers handle a heavy guide chapter (rich text + inline itemstacks + checklists) with cheap recomposition per page turn? Prior art (Wanderer's Sketchbook, Frontier's Map) proves the physical-book *feel* in native GUI; the vanilla handbook proves dynamic richtext. This spike proves the combination at our content weight — pass, and vsimgui is dropped as a dependency entirely.

**Measure:** open the book (Alt+J), turn pages, grep `client-main.log` for `[almanac:illuminated:book]` — per-spread composition time is logged. Target < ~30 ms on iGPU-class hardware.

## Layout

Workshop-standard Maltiez template harness (see `vs-workshop/MOD-TEMPLATE.md`):

- `source/` — all C#. `AlmanacIlluminatedModSystem` (hotkey + lifecycle), `Gui/GuiDialogIlluminatedBook` (two-page spread, recompose-per-turn), `Gui/MockChapter` (deliberately heavy spike content — not the guide-pack format)
- `resources/assets/almanacilluminated/` — assets (empty in Phase 0)
- `modinfo.json` is generated from the csproj — edit metadata there

## Build

`dotnet build` (first run generates `Properties/localSettings.props`, run again). F5 Client profile launches the game with the mod via `--addModPath`.
