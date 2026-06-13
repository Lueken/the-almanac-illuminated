# The Almanac: Illuminated — Guide-Pack Schema v0.1

The format mod authors and pack authors write to add a chapter to The Almanac. A guide pack is plain JSON — no code. Chapters appear in-game only when the mod they document is loaded.

**Status: v0.1 draft (2026-06-12).** This is the canonical spec. The published JSON Schema (for editor autocomplete/validation) tracks this document.

---

## 1. Two version numbers — do not conflate them

| Number | Lives in | Means |
|--------|----------|-------|
| `schemaVersion` | the guide pack JSON | which **format version** the author wrote against. The engine declares which schema versions it supports; a guide stays valid as long as its `schemaVersion` is supported. |
| mod version | the mod's `modinfo.json` | the mod's own release version. **Unrelated** to guide compatibility. Moves on its own schedule. |

A guide does not break when its mod bumps version. A guide breaks only if the engine drops support for its `schemaVersion`. Authors set `schemaVersion` once and forget it until they adopt new format features.

---

## 2. Discovery & gating

Illuminated scans **every loaded mod** for guide files at:

```
assets/<anydomain>/almanac/guides/*.json
```

Any mod can ship guides — for itself, or (pack authors) documenting third-party mods. Each guide declares a `gate`: the modid that must be loaded for the chapter to appear.

- `gate` present + mod loaded → chapter shows.
- `gate` present + mod absent → chapter silently hidden.
- `gate` omitted → always shows (use for the pack's own front-matter / "world differences" chapters).

Server-loaded mods: the server pushes guide manifests for its mods to the client at join (Phase 4 server bridge), so chapters exist for server-only content.

---

## 3. Chapter manifest

Top level of each guide JSON.

```json
{
  "schemaVersion": 1,
  "id": "fieldwright",
  "gate": "fieldwright",
  "title": "Fieldwright",
  "subtitle": "A practical guide to the surveyor's art",
  "byline": "set down by Venah",
  "indexKey": "F",
  "icon": "fieldwright:item/surveyors-rod",
  "accentColor": "#7a5a2e",
  "order": 100,
  "sections": [ ... ]
}
```

| Field | Req | Notes |
|-------|-----|-------|
| `schemaVersion` | yes | integer. Format version this guide targets. |
| `id` | yes | unique chapter id (namespace by author to avoid clashes, e.g. `venah:fieldwright`). Target of internal `link`s. |
| `gate` | no | modid required for visibility. Omit = always visible. |
| `title` | yes | chapter title. Literal or `#langkey`. |
| `subtitle` | no | one-line descriptor. |
| `byline` | no | attribution line. |
| `indexKey` | no | the A–Z index letter/glyph. Defaults to first letter of `title`. |
| `icon` | no | itemstack code or texture path for the chapter's index icon. |
| `accentColor` | no | hex. Chapter identity color (drop-caps, rules, callout tint). Defaults to a parchment-neutral. |
| `order` | no | sort within the letter group. Lower = earlier. Default 1000. |
| `sections` | yes | ordered list of sections (see §4). |

**Bounded identity:** a chapter customizes `icon`, `accentColor`, `indexKey`, ordering, and its content — never raw layout or fonts. Your chapter looks like *yours*; the book still looks like the Almanac.

---

## 4. Sections & the hybrid page model

Authors write a stream of **sections**, each a titled group of blocks. The engine **flows** sections across the two-page spread to fill pages — authors do not hand-place pages. Two hints keep control where it matters:

```json
{
  "sections": [
    {
      "title": "Getting Started",
      "pageBreakBefore": false,
      "keepTogether": true,
      "blocks": [ ... ]
    }
  ]
}
```

- `keepTogether: true` — the engine will not split this section across a page boundary (move it whole to the next page if it doesn't fit). Use for a recipe + its caption, a callout, a tight step list.
- `pageBreakBefore: true` — start this section on a fresh page (left page of the next spread). Use to open a major topic cleanly.

This is the **hybrid** model: robust auto-flow (never overflows or breaks) with opt-in art-direction (`pageBreakBefore` + `keepTogether` approach the mockup's balance). `title` is optional — omit for a continuation section.

---

## 5. Blocks (the content vocabulary)

Each block is `{ "type": "...", ...props }`. Every block may carry:

- `requires`: `["modid", ...]` — **per-block gating**. Block renders only if all listed mods are loaded. (A chapter can show a butchering recipe only when butchering is present.)

### v0.1 block types

| `type` | Props | Renders |
|--------|-------|---------|
| `heading` | `text`, `level` (1–3) | section/sub heading (Almendra) |
| `paragraph` | `text` | body text; **inline VTML supported** (§6) |
| `dropcap` | `letter`, `style?` | illuminated initial; or set `dropcap: true` on the first `paragraph` to auto-apply |
| `steps` | `items: [text, ...]`, `ordered?` (default true) | numbered/bulleted step list |
| `materials` | `items: [{ code, label?, count? }]` | captioned itemstack grid (clickable 3D slots) |
| `recipe` | `recipe` (grid recipe code) **or** `output` (itemstack code) | native VS recipe render, reusing the handbook component |
| `callout` | `variant` (`author`\|`tip`\|`warning`\|`lore`), `text`, `icon?` | bordered/tinted aside — the "From the Author's Hand" box |
| `quest` | `questId?`, `items: [{ text, done? }]` | checklist; with PF + `questId`, checkboxes reflect live state + show the tracked pin; without PF, static (§7) |
| `figure` | `image`, `caption?`, `align?` (`left`\|`right`\|`full`) | embedded illustration with optional margin caption |
| `ledger` | `entries: [text, ...]` | dated italic journal block (Lora italic) |
| `divider` | `ornament?` | horizontal ornamental rule (accent-colored) |
| `link` | `to`, `text` | clickable cross-reference (§8) |

### Deferred to v0.2 — do not hack around it

- **`table`** — `type: "table"` for stat grids (tool tiers, fuel burn times, temperatures). **Explicitly deferred.** VTML inline formatting cannot lay out a real grid, and `steps`/`materials` must **not** be abused to fake tables. Authors needing tables in v0.1 should wait for v0.2; the engine will warn (not error) if it encounters a `table` block under v0.1 so early adopters degrade gracefully.

---

## 6. Inline formatting — reuse VS VTML (free)

Inside any `text` field, authors use Vintage Story's native VTML, parsed by the engine's existing `VtmlUtil` — we build nothing:

- `<strong>bold</strong>`, `<i>italic</i>`
- `<a href="...">link</a>` (see §8 for internal targets)
- `<icon name="..."/>`
- `<itemstack code="game:ingot-iron"/>` — inline clickable 3D item (proven in the Phase 0 spike)

Block-level structure (headings, lists, recipes, callouts) is JSON; rich *inline* runs are VTML. This keeps authoring familiar to anyone who has edited a VS handbook entry.

---

## 7. Quests & Progression Framework (soft)

`quest` blocks degrade by capability:

- **PF loaded + `questId` set** → checkboxes reflect live quest state; the active objective shows the **tracked** pin; completed items strike through; chapter header can show progress pips.
- **PF absent, or no `questId`** → renders as a static checklist from the `items` array (with `done` flags as authored). No live tracking, no error.

Never a hard dependency. A guide with quest blocks is fully readable without PF installed.

---

## 8. Links — internal cross-references first

`link` (and inline `<a>`) must support **internal navigation**, not just URLs. A guide book's core move is "see the Smithing chapter" and jump there. The VS handbook already does clickable internal links (`handbook://...`), so the mechanism is effectively free — we mirror it.

`to` accepts:

| Form | Jumps to |
|------|----------|
| `almanac://chapter/<id>` | another chapter by `id` |
| `almanac://chapter/<id>#<sectionTitle>` | a section within a chapter |
| `handbook://<page>` | a vanilla handbook page (hand off to the game's handbook) |
| `https://...` | external URL (confirm-before-open dialog) |

Internal jumps push onto the book's nav history so the back action returns the reader.

All four forms are kept — the book is open, not a walled garden. This mirrors vanilla precedent: the handbook and settings menu already use the same in-engine link-handoff pattern, so the renderer support is free and proven.

---

## 9. Localization

Any string field (`title`, `subtitle`, `byline`, block `text`, captions, step items) accepts **either**:

- a literal string (`"Getting Started"`), **or**
- a lang key prefixed `#` (`"#almanac:fieldwright-getting-started"`), resolved through VS's standard lang system.

Free translations: ship `assets/<domain>/lang/<locale>.json` as normal. Mixed literal + key within one guide is allowed.

---

## 10. Minimal complete example

```json
{
  "schemaVersion": 1,
  "id": "venah:fieldwright",
  "gate": "fieldwright",
  "title": "Fieldwright",
  "subtitle": "A practical guide to the surveyor's art",
  "byline": "set down by Venah",
  "indexKey": "F",
  "icon": "fieldwright:item/surveyors-rod",
  "accentColor": "#7a5a2e",
  "sections": [
    {
      "title": "Getting Started",
      "keepTogether": true,
      "blocks": [
        { "type": "paragraph", "dropcap": true,
          "text": "Every land remembers its measure. Fashion a <itemstack code=\"fieldwright:item/surveyors-rod\"/> and walk your bounds at dawn." },
        { "type": "steps", "items": [
          "Fashion a Surveyor's Rod from sticks and knapped flint.",
          "Drive a datum stake where two landmarks align.",
          "Sight along the rod and set each bearing in your ledger."
        ]},
        { "type": "recipe", "recipe": "fieldwright:surveyors-rod" }
      ]
    },
    {
      "title": "The First Survey",
      "pageBreakBefore": true,
      "blocks": [
        { "type": "callout", "variant": "author",
          "text": "Check your rod against the sea-level mark each spring. Frost heaves the ground; trust the stone, not the soil." },
        { "type": "quest", "questId": "fieldwright:first-survey", "items": [
          { "text": "Assemble a Surveyor's Rod", "done": true },
          { "text": "Drive the datum stake at the river bend", "done": true },
          { "text": "Sight the canyon rim from your stake" },
          { "text": "Set down three bearings in the ledger" }
        ]},
        { "type": "link", "to": "almanac://chapter/venah:smithing",
          "text": "See the Smithing chapter for working the flint." }
      ]
    }
  ]
}
```

---

## Open for v0.2+

- `table` block (committed, deferred from v0.1).
- Multi-column layouts beyond the two-page spread.
- Author-supplied custom ornament art per chapter.
- Web WYSIWYG generator auto-derived from the published JSON Schema (Netlify-hosted).
