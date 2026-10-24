# Patch notes

The Almanac: Illuminated.

Notes for 0.1.3 and 0.1.4 were written as ModDB changelogs rather than kept here. This file
starts at 0.3.0 and carries forward from now on.

---

## 0.3.4

**The page-turn corner was not where the page was.**

Reported on The Quire on 2026-10-24 by four players inside ten minutes. Silas clicked around the
bottom of the page and on the folio number and got nothing, and said the live spot sat "crazy
high." Chanka and Pun found it, but only by accident. GamerGoblin and Vinni_Pukh asked for an
icon. My first answer in the thread was that the book is a book and a page turn is a page turn,
and that there was already a turn icon. Both halves of that were wrong, and the code says so.

`cornerBounds` was built from the book *plate* rect — `(bookX, 0, bookW, bookH)`. The parchment
the reader sees is painted across the whole *dialog* rect: `DrawPhysicalPages` fills `bgBounds`,
and `PhysicalBookView` textures that capture onto the page quad edge to edge, un-inset and
un-letterboxed. `dialogH` is `bookH + btnRow`, so a 38-unit strip of fully visible page along the
bottom was dead to the mouse. The folio number is drawn at `dialogH - 12`, which is **26 units
below the bottom of the clickable region** — the one mark that says "this is the bottom of the
page" sat outside the only control on it. At a small window or a high GUI scale the dead strip is
proportionally larger, and the camera tilt magnifies exactly that part of the page.

The corner region is now the painted paper. Clicking the folio number turns the page, which is
the test the report deserved.

It also takes an absolute floor of 48 units on each corner band. The 22% fraction shrinks with
the plate, and the plate is smallest precisely where a player is least able to aim.

**The live corner lifts under the cursor.**

There is no page-turn icon in this mod and there never was; the class comment claimed Prev/Next
buttons that do not exist either. The corner click is the only page-turn input in the book — no
buttons, no hotkey, no wheel — and nothing announced it. "It is a physical book" is a fair
argument for not bolting an arrow onto the page, and no argument at all for an invisible control:
a real book's loose corner is something you can see.

So the corner you can actually turn now curls off the page while the cursor is on it, and settles
when it leaves. It reuses the sheet the turn already builds, at zero swing and a small curl
biased to the foot of the page, drawn over the flat spread rather than instead of it. No icon, no
text, no permanent chrome. `BuildBentSheet` takes a `lowBias` argument that defaults to zero, so
the turn animation is byte-for-byte the curve it always was.

Feel is tunable in place: `.almbook hover <n>` for the lift, `.almbook hoverease <n>` for the
settle, `hover 0` to switch the cue off entirely. Both save like every other knob.

---

## 0.3.3

**The Spotted Page is withdrawn. It should never have shipped.**

0.3.2 was cut to carry one new UI component for TCM's Great Work panel, and it took the whole
working tree with it, including five commits of unreleased work on `TextLens`, the seam an external
mod uses to lay a corruption filter over the book's text.

That seam is not inert when a wirer is present. The Marginalia: Conjunction already ships
`SpottedPageBridge`, which looks for `AlmanacIlluminated.TextLens` by reflection. Against 0.3.1 the
type did not exist, so the bridge logged "book text stays clean" and did nothing. Against 0.3.2 it
wired, and the book began re-inking its whole spread whenever the corruption state moved, which
readers saw as a flash on page turns. A player reported it within hours.

`TextLens` is gone, so the bridge is back to finding nothing and the book is clean again. The
renderer keeps three passthrough helpers that every text run calls; emptying those call sites to
delete three no-ops would be a large diff across the whole renderer for no behavioural gain. The
mark-capable serif faces stay too, because they render identically with no marks asked for.

Restoring the feature means restoring `TextLens.cs` and the two bodies it drove. Nothing else moved.

**AlmanacTcm 0.6.0 is unchanged and needs no rebuild.** Its load floor is 0.3.2 and 0.3.3 clears
it, and it references nothing this release removed: `DecisionPanelComponent`, `BookLinkComponent`
and the serif families are all still here. Re-cutting a TCM that was already published would have
meant two different builds both called 0.6.0, which is the exact fault that made the 0.5.16 crash
invisible. Drop 0.3.3 in beside it.

---

## 0.3.2

One new extension seam, for the Almanac suite's first page that asks the reader to decide
something rather than read something.

**`DecisionPanelComponent`** — a rubricated panel with the author's-hand frame and wax seal, a
heading, the body of a consequence, and the choices drawn inside the same box.

It is one component rather than a frame placed around several, and that is forced by the rich
text model rather than chosen: a component may only narrow its OWN flow path, which is exactly how
`CalloutComponent` works. There is no way for a frame to indent separate `BookLinkComponent`s that
follow it, so a box drawn around loose links either clips at the column edge or has its own border
run through its text. The actions therefore belong to the panel; their rectangles are computed
where they are drawn and hit-tested in the space `GuiElementRichtext` hands to `OnMouseDown`.

Carries the same margin contract as `CalloutComponent`: the caller reserves `OuterMarginTop` above
and the instance's `ReserveBelow` underneath, because the box is drawn past the text bounds. The
bottom reservation is per-instance because a panel with no choices has no choice row to leave room
for, and reserving one anyway leaves a visible empty band inside the box.

Built for TCM's Great Work declaration, which is the one irreversible choice in the Almanac and
first shipped reading as one more underlined link on a page full of them.

**`AlmanacTcm` 0.6.0 and this release are a pair.** `DecisionPanelComponent` does not exist in
0.3.1, and TCM 0.6.0 raises its runtime floor to 0.3.2 so a mismatched install is refused at boot
with a readable message rather than dying when the Callings tab is first opened.

---

## 0.3.1

A crash fix, reported from the field on the day it is fixed.

In singleplayer an open book pauses the game, and the game's scheduler refuses new
timers while paused for anyone running with developer mode and extended debug info
switched on. Four places in the mod set timers from inside that pause:

- **Opening the Journal tab crashed those players outright.** Composing the journal
  page re-set the text box, the text box reported a change as though someone had
  typed, and the auto-save timer that followed went to the paused scheduler. A
  recompose no longer counts as an edit, and the auto-save timer is now permitted
  while paused, which is when all journal writing happens anyway. For everyone
  else the old behaviour was quieter but still wrong: a journal edit made in
  singleplayer waited for the unpause to reach disk.

- Closing the book, losing the page capture, and the dev preview watcher each
  deferred work through the same scheduler and could be caught the same way. All
  three are now permitted while paused.

## 0.3.0

The Crops chapter, which was the one part of the book still showing a reader less than the
game already knew. Ships alongside TCM 0.5.0; TCM gates on `MinIlluminatedVersion`, so the
two go up together.

### The book itself

- **The page is the game's own parchment now.** The paper tone used to be a colour picked by
  hand over generated noise, sampled from cover art that no longer ships. It now reads its
  tones out of the game's own page texture at load, finding the writing surface by brightness
  so the ruled lines and the dark edge band drop out on their own. A retexture pack carries
  through without anything here changing. The tones are still dealt out rather than tiled,
  because a repeat across a spread is worse than no texture at all, and the old generated
  palette stays as the fallback if the texture cannot be read.

- **The page holds its ground in half light.** A page read at dusk used to lose about half its
  tone, and half light is where most reading actually happens. Only the middle of the curve
  moved. Full dark still costs you the page and a lamp still changes the reading.

- **You choose the binding, and it is yours alone.** Covers are the reader's choice and never
  the pack's, so two people reading the same book can bind it differently. Leather in eleven
  colours, dyed cloth in eleven, and wool, each drawn as the real item the way the handbook
  draws it, and each taking the texture the item itself declares, so a cover and the hide it
  was cut from can never drift apart. Nothing new ships with the mod; the materials are read
  from the game at runtime, the same as the parchment. Swapping re-skins only the parts that
  wear the cover, so the pages are untouched and you stay on the page you were reading. The
  tab hangs below Journal, Crops, Weather and Index rather than among them.

### The perennials

- Fruit trees and berry bushes have pages that can fill. They were listed and then bypassed:
  every one rendered with no standing and no family, so no amount of picking ever moved them.
  Both now carry a standing like any crop, and each group says its own rule once at the top
  rather than eighty times over. A tree is known by planting it, a bush by raising one.

- The hedge lists the bushes the game actually has, and names them correctly. The catalogue
  was reading a legacy block shape that five species no longer use, so beautyberry,
  cloudberry, blackberry, raspberry and strawberry were missing outright and the rest were
  read through the wrong growth keys. Worse, a bush keeps its berry nowhere near the drops a
  broken one leaves, so the ones that were found were named after the litter: a card reading
  "Plant debris" stood in the hedge where a berry should have been, and because the catalogue
  files one card per produce, every bush that landed on it disappeared into that one entry.
  All ten now read from what a bush gives when you pick it, which is where the fruit was the
  whole time.

- Every fruit tree roster is read, not the first one found. With more than one orchard mod
  installed the catalogue took whichever roster the registry happened to hand back first and
  discarded the others, so trees could vanish depending on load order. All rosters are read
  and grouped by tree identity, and the count is logged so a missing orchard is visible
  rather than silent.

- Modded produce stops being captioned "vanilla". Anything the base game did not supply now
  names the mod that did, read from the block registry rather than guessed from the code's
  domain.

### The mushrooms

- A mushroom card says what is worth knowing before you eat one. Eighty-one species were
  listed with a name and a drawing and nothing else. A card now says plainly whether the
  thing is deadly, poisonous, or only unpleasant in quantity, whether it brings visions, and
  whether it stands through winter or is gone with the first cold. Visions are said
  separately from poison, because two of the six that bring them are poisonous as well.

- A mushroom card says where it grows, and whether your own ground will do. The habitat bands
  the world generator works from are server-side data the book could not previously reach.
  The server now sends them, and each card reads its own temperature and wetness band, what
  it grows on or under, and how that compares with the ground at your bed. The wording is
  careful on purpose: the page says the ground suits a species, never that one will be found
  there. A patch can be absent for want of a host tree, and any world that has been lived in
  has had forest felled since.

- Two rules that hold for every mushroom are said once, at the top of the group, instead of
  on every card. Fungus keeps a slow clock that turns only on days above five degrees, so a
  cold season postpones a flush rather than killing what lies under the ground. And nothing
  comes up on stone, gravel, sand, clay or a beaten path: any true soil will serve, and a
  rich one is worth no more than a poor one.

- The dye mushrooms say so. Seven species in the pack give up a colour in a sealed barrel and
  nothing in the game says which, so a reader had no reason to pick them and no way to learn
  of one. The card now names the colour. It is read from the barrel recipes the server
  actually has, so a mushroom mod added later is picked up with no change here, and a pack
  with no mushroom mod at all sees nothing, because the base game dyes from flowers and
  berries and never from a mushroom.

### Under the hood

- The reader's home ground rides its own reply rather than the session's forage atlas. The
  two were cached together, and they have different lifetimes: the atlas cannot change
  without a restart, but a bed can be moved mid-session. Cached as one, the card judged
  rainfall at the old base while judging temperature at the new one.
