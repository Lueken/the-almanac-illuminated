# The Almanac: Illuminated. Guide-Pack Schema v0.1

This is the format you write to add a chapter to The Almanac. A guide pack is plain JSON. No code. A chapter appears in game only when the mod it documents is loaded.

Status: v0.1 draft, 2026-06-12. This document is canonical. The published JSON Schema follows it, never the reverse.

---

## 1. Two version numbers. Keep them apart.

| Number | Lives in | Declares |
|--------|----------|----------|
| `schemaVersion` | the guide pack | which format version the author wrote against |
| mod version | `modinfo.json` | the mod's own release version |

The two are unrelated. A guide does not break when its mod changes version. A guide breaks only when the engine drops support for its `schemaVersion`. Set `schemaVersion` once. Revisit it when you adopt a newer format feature, not before.

---

## 2. Discovery and gating

The engine scans every loaded mod for guide files at one path:

```
assets/<anydomain>/almanac/guides/*.json
```

Any mod can ship guides. A mod ships guides for itself. A pack author ships guides that document other people's mods. Each guide names a `gate`: the modid that must be present for the chapter to appear.

- `gate` set, mod loaded: the chapter shows.
- `gate` set, mod absent: the chapter stays hidden, with no error.
- `gate` omitted: the chapter always shows. Use this for front matter and for "how this world differs" chapters that belong to no single mod.

Server-side mods are covered too. At join, the server sends its guide manifests to the client (Phase 4, server bridge). Chapters then exist for content the client does not carry on its own.

---

## 3. The chapter manifest

The top level of every guide file.

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
  "order": 100,
  "sections": [ ... ]
}
```

| Field | Required | Rule |
|-------|----------|------|
| `schemaVersion` | yes | Integer. The format version this guide targets. |
| `id` | yes | Unique chapter id. Namespace it with your author domain to avoid collisions. Internal links resolve to it. |
| `gate` | no | The modid required for visibility. Omit to always show. |
| `title` | yes | The chapter title. A literal or a `#langkey`. |
| `subtitle` | no | One line under the title. |
| `byline` | no | Attribution line. |
| `indexKey` | no | The A to Z index letter. Defaults to the first letter of `title`. |
| `icon` | no | An itemstack code or texture path for the chapter's index icon. |
| `accentColor` | no | Hex. The chapter's identity color, used on drop-caps, rules, and callout tint. Defaults to a parchment neutral. |
| `order` | no | Sort position inside the letter group. Lower sorts first. Defaults to 1000. |
| `sections` | yes | The ordered content. See section 4. |

Identity is bounded. A chapter sets its `icon`, `accentColor`, `indexKey`, `order`, and its content. It does not set layout or fonts. A chapter carries its own identity. The book keeps its own.

---

## 4. Sections and the page model

An author writes a stream of sections. A section is a titled group of blocks. The engine flows sections across the two-page spread and fills pages on its own. An author does not place pages by hand.

Two hints return control where it matters.

```json
{
  "title": "Getting Started",
  "pageBreakBefore": false,
  "keepTogether": true,
  "blocks": [ ... ]
}
```

- `keepTogether: true`. The engine does not split the section across a page boundary. A section that does not fit moves whole to the next page. Use it for a recipe and its caption, a callout, or a short step list.
- `pageBreakBefore: true`. The section starts a new page, the left page of the next spread. Use it to open a major topic.

This is the hybrid model. Auto-flow handles the common case and never overflows. The two hints buy back the control an art-directed page needs. `title` is optional. Omit it for a continuation section.

---

## 5. Blocks

A block is `{ "type": "...", ...props }`. Any block may carry one common field:

- `requires`: `["modid", ...]`. Per-block gating. The block renders only when every listed mod is loaded. A chapter can show a butchering recipe and hide it when butchering is absent.

### Block types in v0.1

| `type` | Props | Renders as |
|--------|-------|------------|
| `heading` | `text`, `level` (1 to 3) | A section or sub heading. |
| `paragraph` | `text`, `dropcap?` | Body text. Inline VTML applies. See section 6. |
| `dropcap` | `letter`, `style?` | An illuminated initial. Or set `dropcap: true` on the first paragraph to apply it there. |
| `steps` | `items: [text, ...]`, `ordered?` | A step list. Numbered by default, bulleted when `ordered` is false. |
| `materials` | `items: [{ code, label?, count? }]` | A captioned itemstack grid of clickable slots. |
| `recipe` | `recipe` or `output` | A native recipe render, reusing the handbook's own component. |
| `callout` | `variant` (`author`, `tip`, `warning`, `lore`), `text`, `icon?` | A bordered, tinted aside. This is the author's-note box. |
| `quest` | `questId?`, `items: [{ text, done? }]` | A checklist. See section 7. |
| `figure` | `image`, `caption?`, `align?` (`left`, `right`, `full`) | An embedded illustration with an optional margin caption. |
| `ledger` | `entries: [text, ...]` | A dated journal block in italic. |
| `divider` | `ornament?` | A horizontal ornamental rule in the accent color. |
| `link` | `to`, `text` | A clickable cross-reference. See section 8. |

### Deferred to v0.2. Do not work around it.

`table`, type `"table"`, for stat grids: tool tiers, fuel burn times, temperatures. It is deferred on purpose. VTML does not lay out a real grid, and `steps` and `materials` must not be bent into fake tables. If you need a table in v0.1, wait for v0.2. The engine warns on a `table` block under v0.1 and skips it. It does not error, so an early guide degrades cleanly.

---

## 6. Inline formatting reuses VTML

Inside any `text` field, write Vintage Story's native VTML. The engine parses it with the game's own `VtmlUtil`. We add nothing.

- `<strong>bold</strong>`, `<i>italic</i>`
- `<a href="...">link</a>`. See section 8 for internal targets.
- `<icon name="..."/>`
- `<itemstack code="game:ingot-iron"/>`. An inline clickable item, proven in the Phase 0 spike.

Block structure is JSON. Inline runs are VTML. Anyone who has edited a handbook entry already knows the inline half.

---

## 7. Quests and Progression Framework

A `quest` block degrades by capability.

- With Progression Framework loaded and `questId` set: checkboxes track live state, the active objective shows the tracked pin, and completed items strike through. The chapter header may show progress pips.
- Without Progression Framework, or without `questId`: the block renders as a static checklist from `items` and honors the authored `done` flags. No tracking. No error.

Progression Framework is never required. A guide that uses quest blocks reads fine without it.

---

## 8. Links resolve internally first

`link`, and inline `<a>`, support internal navigation, not only URLs. The core move of a guide book is to say "see the Smithing chapter" and take the reader there. The handbook already resolves in-engine links, so the mechanism costs nothing. We reuse it.

`to` accepts four forms.

| Form | Jumps to |
|------|----------|
| `almanac://chapter/<id>` | another chapter by `id` |
| `almanac://chapter/<id>#<sectionTitle>` | a section inside a chapter |
| `handbook://<page>` | a vanilla handbook page, handed to the game |
| `https://...` | an external URL, behind a confirm-before-open dialog |

An internal jump pushes onto the book's history. Back returns the reader to where they were.

All four forms stay. The book is open, not a walled garden. The base game does the same in its handbook and its settings menu, which is where this link-handoff pattern is already proven.

---

## 9. Localization

Any string field accepts a literal or a lang key. This covers `title`, `subtitle`, `byline`, block `text`, captions, and step items.

- A literal string, for example `"Getting Started"`.
- A lang key prefixed with `#`, for example `"#almanac:fieldwright-getting-started"`, resolved through the game's lang system.

To translate, ship `assets/<domain>/lang/<locale>.json` as usual. A single guide may mix literals and keys.

---

## 10. A complete example

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
          "text": "Check your rod against the sea-level mark each spring. Frost heaves the ground. Trust the stone, not the soil." },
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

## Planned for v0.2 and later

- The `table` block, committed and deferred from v0.1.
- Multi-column layouts beyond the two-page spread.
- Author-supplied ornament art per chapter.
- A web editor that builds and previews a guide pack, generated from the published JSON Schema and hosted free.
