# The Almanac: Illuminated

A living questbook for the modded world. Pull the Almanac from your back pocket with Alt+J. It holds chapters of guides written by mod authors and pack authors. A chapter appears only when the mod it documents is loaded. The book has bookmarks, search, page turns, and tracked objectives.

Status: the book reads as a physical object by default. The format is specified and under review.

## How it reads

Alt+J opens the Almanac. By default it draws as a bound book held in front of you: a modeled cover and spine, tilted pages, paper slips for the chapter tabs, scraps hanging from the top edge for your bookmarks, and a sheet that turns when you page.

The flat page is still there. It is the same book, the same chapters, the same layout code, drawn straight to the screen instead of onto a tilted page. It stays available two ways:

- **By choice.** Alt+P switches presentation, or `.almbook flat` / `.almbook physical`. The choice is remembered per install in `ModData/almanacilluminated/settings.json`.
- **By fallback.** The physical book captures the composed spread into an offscreen framebuffer. If a machine will not give us one, the Almanac says so once and reads flat for the rest of the session. Nothing is lost but the tilt.

`.almbook` on its own reports the current presentation. `.almbook tilt|yaw|dist|scale|fov|height <n>` tunes the framing live, and `.almbook reset` puts it back. Every knob, what its number means, and its limits: [`docs/book-framing.md`](docs/book-framing.md).

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

- `source/`. All C#. `AlmanacIlluminatedModSystem` handles the hotkeys and lifecycle. `Gui/GuiDialogIlluminatedBook` is the two-page spread that recomposes per turn, and it is the layout engine for both presentations. `Gui/MockChapter` is deliberately heavy spike content, not the guide-pack format.
- `source/Physical/PhysicalBookView.cs`. The physical presentation: it captures the dialog above into a framebuffer, draws it on the modeled book, and remaps mouse events from the tilted page back into the flat space the composers laid out. Because it captures the composed spread rather than reimplementing it, every tab (chapters, Crops, Weather, Journal) inherits the physical book for free.
- `resources/assets/almanacilluminated/`. Assets, including the bundled fonts.
- `docs/SCHEMA.md`. The guide-pack format.
- `modinfo.json` is generated from the csproj. Edit the metadata there.

## Build

Run `dotnet build`. The first run generates `Properties/localSettings.props`, so run it again. The F5 Client profile launches the game with the mod through `--addModPath`.

## Fonts

The book reads in Lora and Almendra, the serifs the game already ships. Josefin Sans and Odibee Sans are bundled under the SIL Open Font License (see `resources/assets/almanacilluminated/fonts/licenses/`) and held in reserve for accents.

## Credits

The page-turn sounds (`sounds/pageturn*.ogg`) are from **Wanderer's Sketchbook** by **JeanPierre**, used with the author's blanket permission for modders ("Feel free and most welcome to modify or to use any of the assets from the mod!", stated on the mod's page). The same mod's open-book frame art and page-flip frames dressed the flat page until 0.2.0 retired it; the modeled book that replaced it draws its own boards and turns its own sheets, so neither ships any more.
