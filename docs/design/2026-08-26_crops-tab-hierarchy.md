# Crops tab: hierarchy pass

Design pass 2026-08-26, against `source/Crops/CropsRenderer.cs` and siblings.
Written down so the remaining work does not need the session that produced it.

**Author's complaint, verbatim:** "I do want to keep the tally marks. Its more clarity
of the data provided. its pretty hard to know what is important."

**Tally marks stay. Fixed constraint, not a preference.**

---

## The diagnosis

The page had two visual registers doing all the work: loud bold serif for names and
headers, and one narrow quiet band (`Body`, `Muted`, `Window`, `Voice`, all within a
tight tonal range) for everything else. Weight was allocated by position in the card
rather than by what a reader came for.

Roughly half the page repeats one boilerplate sentence at full card cost: every
all-stranger family, plus the entire mushroom section, gets the same icon size, name
weight and footprint as a fully Versed entry.

## Ranked hierarchy, what the eye should hit

1. **The plant-now answer** (`PlantingLine` / `PlantingWindow.Summary`)
2. **Progress toward the next tier** (tally plus a numeric target)
3. **Family and rotation state**, because the family is the unit of strategy
4. **Agronomy specifics**, reference detail once 1 has answered the practical question
5. **Crop name and icon**, the anchor rather than the payload
6. **Source label**, an attribution footnote that should recede furthest

---

## DONE (commit e202765)

**The third register exists.** `PlantingLine` moved out of `f.Window`, which is italic
and therefore this book's aside voice, into a new `f.Sow`: upright, bold, field green
`#4f6338`, the same accent The Turned Earth declares. The agronomy figures stepped back
from `Body` to `Muted`. Both tier formats are kept, numeric for Versed and prose for
Acquainted, because that split is the tier system's whole point.

## DONE (commit 05420df) — was item 2, and it grew

Item 2 below was scoped as a mushroom dedup. Reading the code widened it: `CropsRenderer.cs:105`
sends **every** non-`SeedCrop` down the same path, so bushes and fruit trees arrive with
`standing: null` exactly as mushrooms do, and at `CropsRenderer.cs:291` the `st == null` arm
hands them the full Versed readout. A fruit tree the reader has never touched prints exact
figures while a sown crop they have never grown prints "A stranger."

The fix is one mechanism for all three groups. `GroupNote(CropKind)` prints one line under each
perennial heading saying why that group's pages stand open, and the per-card
"Foraged, not sown." is gone from all 24 mushroom cards.

**Ruled: explain, do not hide.** The obvious alternative was to gate perennial figures the way
sown crops are gated. Rejected. There is currently no way to earn a fruit tree's page back, so
gating would be a straight loss to the player dressed as a reward. Revisit only once fruit
familiarity exists; a review of that is running as of 2026-08-26.

Mushroom cards now carry name, icon and source and nothing else, which is correct rather than
lazy: `BuildMushrooms` (`CropCatalog.cs:396-421`) sets both temperature bounds to NaN with no
growth days, so `GrowingLine` returns empty and `PlantingWindow.Compute` would fall back to a
bare grow floor with no ceiling (`PlantingWindow.cs:53-54`) and report a fruiting season derived
from nothing about the mushroom. Filling that space needs real data, which is the separate
mushroom forage-data review.

---

## REMAINING, in value order

### 1. Collapse untouched families. The big space win.

Three states already exist in the data (`CropFamilyStanding.Sum`, `MemoryAvailable`,
`OfOne` in `ICropFamiliaritySource.cs:47-76`).

- **Untouched** (`Sum <= 0`, every member Stranger): render ONE line, not a header plus
  N cards. Small-caps family name, the family note if any, then a comma list of member
  names with small inline icons (reuse `ItemstackTextComponent` smaller, same machinery
  as `CropsRenderer.cs:266`). Use the existing `" · "` separator convention from
  `GrowingLine` (line 413). Shape:
  `GRASS FAMILY · the bread grains and the pasture grasses are one family · Rice grain, Rye grain, Spelt grain`
- **Partly worked** and **rotation-unlocked**: keep the full card treatment, it is
  earning its space.

**Collapse means shrink-and-group, never hide.** Every name still appears, in family
order. What is lost is the full-size icon framing for never-grown crops, mitigated by
keeping small inline icons, since icons rather than prose are what make a list scannable.

### 2. ~~Mushrooms are never family-gated and never will be.~~ SHIPPED, see DONE above.

### 3. Family header becomes a badge.

Replace the four full-sentence `FamilyVoice` variants (`CropsRenderer.cs:222-230`) with
a one-to-three word small-caps status badge beside the name: UNWORKED / LEARNING /
ROTATION MEMORY UNLOCKED / SINGLE-CROP FAMILY. Explain "rotation memory" **once** on the
page, or link to The Turned Earth which already explains it, rather than repeating the
same paragraph under every unlocked family.

Keep the family note in italic (a correct use of the aside voice) but separate it
visually from the badge, so status and trivia stop reading as one thing.

### 4. Tally marks: show the target.

Print the target after the marks, small and muted: `‖‖ of 25`. Size the marks up a touch
from the default 13 **only while a card is actively being learned** (`own+kin > 0` and
tier is not Versed). Once capped, replace the whole row with a small **VERSED** stamp:
the code's own comment says a complete row means nothing further to learn, so past that
point the marks are decorative rather than informative.

### 5. Source label.

Drop `"vanilla"` entirely. Unmarked-means-vanilla is a legible convention and most cards
carry that dead label today. For modded produce, move the mod name to its own
caption-size line under the crop name, a full size step down, rather than sitting on the
name's baseline competing with it at merely a muted colour.

### 6. The orphaned note.

"Reckoned from where you stand. The world is held still, so your bound spawn could not be
asked after" floats unstyled at top-left of spread 1 with no header. It explains the
homebase-climate basis for every planting window on the page, and planting windows are
now the loudest fact on every card, so it matters more than it looks. Give it the book's
callout treatment (`lore` or `tip`).

### 7. Page balance, recheck rather than fix.

Page 6 renders entirely blank, header only. `PackCards` (`CropsRenderer.cs:451-478`)
flows cards linearly with no attempt to balance facing pages. The family collapse will
likely absorb this by freeing earlier space. **Re-check after that lands rather than
hand-tuning for a page count.**

---

## Resolved, do not re-investigate

**The running headers are correct and need no fix.** The pass flagged
"THE ALMANAC" / "WHAT YOU HAVE GROWN" as claiming a content split they do not deliver.
They are not a split: left is the book's name, right is the tab's name, set at
`Gui/GuiDialogIlluminatedBook.cs:433`. Ordinary book running-head convention.

**There is no Crops-tab accent colour registered anywhere.** That is why the new `Sow`
register borrows The Turned Earth's field green rather than inventing one.
