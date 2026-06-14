# The Almanac: Illuminated. Guide-Pack Schema v0.1

This is the format you write to add a chapter to The Almanac. A guide pack is plain JSON. No code. A chapter appears in game only when the mod it documents is loaded.

Status: v0.1 draft, 2026-06-12. This document is canonical. The published JSON Schema follows it, never the reverse.

---

## 1. Two version numbers. Keep them apart.

| Field | Lives in | Declares |
|-------|----------|----------|
| `schemaVersion` | the guide pack JSON | which format version the author wrote against |
| `version` | the mod's `modinfo.json` | the mod's own release version |

The two are unrelated. A guide does not break when the mod's `version` changes. A guide breaks only when the engine drops support for its `schemaVersion`. Set `schemaVersion` once. Revisit it when you adopt a newer format feature, not before.

`schemaVersion` bumps only for a change an older engine cannot read. New optional blocks and fields that an older engine can skip do not force a bump. They degrade gracefully.

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

Two guides may gate to the same modid. A mod author documents their own mod, and a pack author documents it too. Guides are keyed by `id`, not by `gate`, so these are two separate chapters. Both show, sorted by title under that mod's name. A real clash is two guides that share one `id`. The engine keeps the one loaded last and logs a warning. Replacing a mod's own guide wholesale is an override feature, planned for v0.2.

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
  "icon": "fieldwright:item/surveyors-rod",
  "accentColor": "#7a5a2e",
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
| `icon` | no | An itemstack code or texture path for the chapter's index icon. |
| `accentColor` | no | Hex. The chapter's identity color, used on drop-caps and rules. Defaults to a parchment neutral. |
| `order` | no | Gate-less chapters only. Integer sort position among front matter, lower first. Ties break by `id`. Ignored on gated chapters. |
| `overview` | no | Gate-less chapters only. Pins this chapter first in the front matter and makes it the book's landing page. For the server overview. See section 11. |
| `sections` | yes | The ordered content. See section 4. |

Identity is bounded. A chapter sets its `icon`, its `accentColor`, and its content. It does not set its placement, its layout, or its fonts. A chapter carries its own identity. The book keeps its own.

### Placement is the engine's call, with one exception

A gated chapter does not choose where it sits. The book orders gated chapters alphabetically by the display name of the mod each one documents. A mod that ships more than one chapter sorts its own chapters by title beneath that name. The index letter comes from the same name. No gated chapter can set its placement, so none can jump ahead of another.

Gate-less chapters are different. They are front matter, and they belong to the pack rather than to a competing mod, so the pack owner may order them with the `order` field. Front matter sorts by `order`, then by `id`, and sits ahead of the alphabetical run. `order` plus `id` is a total ordering, so front matter from two installed packs interleaves predictably instead of colliding. One gate-less chapter may set `overview: true` to pin ahead of all other front matter and become the book's landing page. See section 11.

This split is deliberate. The lever exists only where one owner controls the content. Where chapters from different authors compete under a mod name, placement stays the platform's call.

---

## 4. Sections and the page model

An author writes a stream of sections. A section is a titled group of blocks. The engine flows sections across the two-page spread and fills pages on its own. An author does not place pages by hand.

Two hints return control where it matters.

```json
{
  "id": "getting-started",
  "title": "Getting Started",
  "pageBreakBefore": false,
  "keepTogether": true,
  "blocks": [ ... ]
}
```

- `id` is optional. It is a stable anchor for internal links and is never shown to the reader. Set it when you want links to point at this section. Unlike `title`, it must not change once links rely on it. See section 8.
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
| `dropcap` | `letter` | An illuminated initial. Or set `dropcap: true` on the first paragraph to apply it there. Decorative styles arrive in v0.2. |
| `steps` | `items: [text, ...]`, `ordered?` | A step list. Numbered by default, bulleted when `ordered` is false. |
| `materials` | `items: [{ code, label?, count? }]` | A captioned itemstack grid of clickable slots. |
| `recipe` | `recipe` or `output` | A native recipe render, reusing the handbook's own component. |
| `callout` | `variant` (`author`, `tip`, `warning`, `lore`), `text`, `icon?` | A bordered, tinted aside. This is the author's-note box. |
| `quest` | `questId?`, `items: [{ text, done? }]` | A checklist. See section 7. |
| `figure` | `image`, `caption?`, `align?` (`left`, `right`, `full`) | An embedded illustration with an optional margin caption. |
| `ledger` | `entries: [text, ...]` | A dated journal block in italic. |
| `divider` | `ornament?` | A horizontal ornamental rule in the accent color. |
| `link` | `to`, `text` | A clickable cross-reference. See section 8. |
| `contents` | `include?` (`added`, `all`) | An auto table of contents of loaded mods. See section 11. |

**The `recipe` block.** Supply `recipe` or `output`, never both. `recipe` takes a recipe code and renders that one recipe. `output` takes an item code and renders every recipe that produces it, the way the handbook does. Use `recipe` for one specific recipe, `output` for all of them.

**The `figure` block.** `image` is a texture path, `domain:textures/<path>.png`, in PNG. The book scales the image to fit the text column and preserves its aspect ratio. `align` `full` spans the column. `left` and `right` float it smaller with text beside it.

**The `callout` block.** Its color comes from its `variant`, not from `accentColor`, so a `warning` stays warning-colored in every chapter, whatever the chapter's accent. `icon` takes the same form as the chapter icon: an itemstack code or a texture path.

### Deferred to v0.2. Do not work around it.

`table`, type `"table"`, for stat grids: tool tiers, fuel burn times, temperatures. It is deferred on purpose. Nothing in the game lays out a real grid for us. VTML has no table tag, and the richtext engine offers only inline and left or right floats, which cannot hold columns aligned from one row to the next. A real table needs a custom column-layout component, and that is v0.2 work. Until then, do not bend `steps` or `materials` into fake tables. The engine warns on a `table` block under v0.1 and skips it. It does not error, so an early guide degrades cleanly.

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

Quest tracking is a planned feature. The static checklist comes first. Live tracking against Progression Framework comes with the later integration work. The concept is already proven: Fieldwright's build guide tracks checklist completion in a shipped mod.

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
| `almanac://chapter/<id>#<sectionId>` | a section inside a chapter, by its `id` |
| `handbook://<page>` | a vanilla handbook page, handed to the game |
| `https://...` | an external URL, behind a confirm-before-open dialog |

Inline `<a href>` accepts these same four forms. An internal jump pushes onto the book's history. Back returns the reader to where they were.

Anchor to a section's `id`, never its `title`. A title is display text, optional and localizable, so it makes a fragile target. An `id` is stable.

A link can point at a chapter that is hidden because its `gate` mod is not loaded. The engine cannot tell that case apart from a link to an `id` that was never defined, because a gated-off chapter never loads and its `id` is simply unknown. Both render the same way: plain, inert text, so the sentence around the link still reads. Any click or hover feedback says `unavailable`, never `not installed`, because the engine cannot know which case it is.

All four forms stay. The book is open, not a walled garden. The base game does the same in its handbook and its settings menu, which is where this link-handoff pattern is already proven.

---

## 9. Localization

Any string field accepts a literal or a lang key. This covers `title`, `subtitle`, `byline`, block `text`, captions, and step items.

- A literal string, for example `"Getting Started"`.
- A lang key prefixed with `#`, for example `"#fieldwright:getting-started"`, resolved through the game's lang system.

The key's domain is the domain you ship the translation under, not the chapter `id` namespace. To translate, ship `assets/<domain>/lang/<locale>.json` as usual. A single guide may mix literals and keys.

To write a literal that begins with `#`, double it. The value `##1 rule of the trade` renders as `#1 rule of the trade`. The escape applies only to a leading `#`.

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
  "icon": "fieldwright:item/surveyors-rod",
  "accentColor": "#7a5a2e",
  "sections": [
    {
      "id": "getting-started",
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
        { "type": "link", "to": "almanac://chapter/venah:fieldwright#getting-started",
          "text": "Review Getting Started before you set out." },
        { "type": "link", "to": "almanac://chapter/venah:smithing",
          "text": "See the Smithing chapter for working the flint." }
      ]
    }
  ]
}
```

---

## 11. The server overview and the contents block

A server admin wants a front page: what this server is, its rules, the spirit of the place, and a way into the rest of the book. The format already carries it, and two small features make it standardized.

**The overview chapter.** Ship a gate-less guide and set `overview: true`. It pins ahead of all other front matter and becomes the page the book opens to. It is opt-in. A server that does not ship one simply opens to its first front matter, or to the first chapter. Only one chapter holds the overview slot; if two claim it, the first by `order` then `id` wins and the engine logs the rest as ordinary front matter.

```json
{
  "schemaVersion": 1,
  "id": "myserver:overview",
  "overview": true,
  "title": "Welcome to Wilderlands",
  "sections": [ ... ]
}
```

**The contents block.** Drop `{ "type": "contents" }` into any chapter, usually the overview, and the book fills it at render with a row per loaded mod. A mod that ships a chapter renders as a link to it. A mod with no chapter renders as plain, muted text, so a player sees the whole modlist and what still lacks a guide. The list rebuilds from the live modlist every time the book opens, so it never goes stale and needs no upkeep.

- `include: "added"` is the default. It lists the mods the server added and hides the base game.
- `include: "all"` lists every loaded mod, the base game included.

The rows sort by display name. Because the block is authored, the admin wraps it in their own prose: a heading, a sentence of intent, then the table.

A ready-to-edit overview template ships in the repository at `docs/examples/server-overview.example.json`. Copy it into your own server mod under `assets/<yourserver>/almanac/guides/`, fill in the prose, and it appears for everyone who joins.

---

## Validation

A JSON Schema ships beside this document at `docs/guide-pack.schema.json`. It mirrors the rules above and gives you autocomplete and live error checking in any editor that reads JSON Schema.

Point your guide file at it with a `$schema` line at the top:

```json
{
  "$schema": "https://raw.githubusercontent.com/Lueken/the-almanac-illuminated/main/docs/guide-pack.schema.json",
  "schemaVersion": 1,
  "id": "you:your-chapter",
  ...
}
```

The game ignores that line. Your editor uses it. In VS Code it works as soon as the file is saved. The engine still does the final say at load time, but the schema catches the everyday mistakes first: an unknown block type, a missing required field, a `recipe` with both `recipe` and `output`, a misspelled property, a bad `variant` or `accentColor`. This document stays canonical. The schema follows it.

---

## Planned for v0.2 and later

- The `table` block, committed and deferred from v0.1.
- A live in-game preview that reloads a guide while you edit it.
- A web editor that builds and previews a guide pack, generated from the published JSON Schema and hosted free.
