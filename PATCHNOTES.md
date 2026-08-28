# Patch notes

The Almanac: Illuminated.

Notes for 0.1.3 and 0.1.4 were written as ModDB changelogs rather than kept here. This file
starts at 0.3.0 and carries forward from now on.

---

## 0.3.0 (in development)

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
