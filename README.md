# The Almanac: Illuminated

A living questbook for the modded world. Pull the Almanac from your back pocket with Alt+J. It holds chapters of guides written by mod authors and pack authors. A chapter appears only when the mod it documents is loaded. The book has bookmarks, search, page turns, and tracked objectives.

Status: Phase 0, the renderer spike. The format is specified and under review. The book itself is early.

## The guide-pack format

If you want to write a chapter, read the spec: [`docs/SCHEMA.md`](docs/SCHEMA.md). A guide pack is plain JSON. No code. Chapters render only for the mods a player has loaded.

The spec is open for comment. The format is not frozen, and feedback now is worth more than feedback later.

## Feedback

Comments and concerns are welcome two ways:

- Open an issue on this repo.
- DM **@jefficus1776** on Discord.

## Phase 0 question

Can native Vintage Story GUI compose a heavy guide chapter, rich text with inline itemstacks and checklists, and recompose cheaply on every page turn? Prior art (Wanderer's Sketchbook, Frontier's Map) proves the physical-book feel in native GUI. The vanilla handbook proves dynamic richtext. This spike proves the two together at our content weight. It passed: 16 to 24 ms per spread on iGPU-class hardware against a 30 ms target, so vsimgui is dropped entirely.

Open the book with Alt+J, turn pages, and grep `client-main.log` for `[almanac:illuminated:book]` to see per-spread composition time.

## Layout

Workshop-standard Maltiez template harness.

- `source/`. All C#. `AlmanacIlluminatedModSystem` handles the hotkey and lifecycle. `Gui/GuiDialogIlluminatedBook` is the two-page spread that recomposes per turn. `Gui/MockChapter` is deliberately heavy spike content, not the guide-pack format.
- `resources/assets/almanacilluminated/`. Assets, including the bundled fonts.
- `docs/SCHEMA.md`. The guide-pack format.
- `modinfo.json` is generated from the csproj. Edit the metadata there.

## Build

Run `dotnet build`. The first run generates `Properties/localSettings.props`, so run it again. The F5 Client profile launches the game with the mod through `--addModPath`.

## Fonts

The book reads in Lora and Almendra, the serifs the game already ships. Josefin Sans and Odibee Sans are bundled under the SIL Open Font License (see `resources/assets/almanacilluminated/fonts/licenses/`) and held in reserve for accents.
