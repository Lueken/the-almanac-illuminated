# Framing the physical book — the `.almbook` command

The Almanac opens with **Alt+J**. **Alt+P** switches it between the physical book and the flat
page. Everything on this page tunes the *physical* book: the flat page has no 3D framing to adjust,
so the knobs simply have nothing to act on while it is showing.

`.almbook` is a client command. Nobody else sees your settings and nothing is sent to the server.

Every knob applies **on the next frame** and **saves as you set it**, so the way to use this is to
open the book and type while looking at it. Tuning that does not survive a relaunch is tuning done
twice. Settings live in `ModData/almanacilluminated/settings.json`.

---

## Finding your way around

| Command | What it does |
|---|---|
| `.almbook` | Reports the current presentation plus every knob value. |
| `.almbook status` | Just the knob values. |
| `.almbook reset` | Every framing, bend and flip knob back to the shipped defaults. |
| `.almbook physical` / `.almbook flat` | Same as Alt+P. |
| `.almbook pause on` / `off` | Whether singleplayer time holds still while you read. On by default; saved but inert in multiplayer. |

Unknown knob names are answered with the full list, so you never have to remember it.

---

## The six framing numbers

These are the ones that place the book. Set them with `.almbook <knob> <number>`, for example
`.almbook tilt 15`.

| Knob | Default | What the number means | Limits |
|---|---|---|---|
| `tilt` | `12` | Degrees of pitch. Higher lays the book flatter, as though resting on a desk; lower stands it upright to face you square on. | none |
| `yaw` | `0` | Degrees of turn around the book's vertical axis. Negative and positive swing the spine to either side. | none |
| `dist` | `1.0` | How far the book sits from the camera. Larger pushes it away, smaller pulls it closer. | minimum `0.1` |
| `scale` | `0.37` | The size of the book itself, applied evenly on all three axes. | minimum `0.05` |
| `fov` | `50` | Degrees of lens angle for the book's own render. Lower is more telephoto and flattens perspective; higher is wider and more dramatic. | `10`–`120` |
| `height` | `0.06` | Vertical offset. Raises or lowers the book on screen. | none |

**`dist` and `scale` are not the same lever.** Both change how large the book appears, but `dist`
moves it through perspective while `scale` resizes it where it stands. If the near edge of a page
looks stretched or looming, the fix is usually `fov` down and `scale` up, rather than reaching for
`dist`.

The order the transform is built in, if you ever need to reason about an odd result: the book is
moved up by `height` and back by `dist`, then pitched by `tilt`, then turned by `yaw`, then scaled
by `scale`, and finally viewed through a `fov` lens.

**The opening animation borrows from two of these.** A book being opened starts a full unit further
away and 18° more tilted than your settings, easing in over about six tenths of a second. That is
the "pulled from a pocket" rise, and it means the first moments after Alt+J deliberately do not
match your numbers. Judge your framing on the settled book.

---

## Page bend

How the open pages curl away from the spine. All four were set by tuning in game on 2026-08-09
rather than derived, so treat them as taste, not physics.

| Knob | Default | What the number means | Limits |
|---|---|---|---|
| `bend` | `1.5` | Peak extra rotation from spine to free edge, in radians. `0` restores a perfectly rigid sheet. | `0`–`2.5` |
| `stiff` | `0.7` | How the curl distributes along the page. Above `1` holds the root stiff and throws the bend at the free edge. Below `1` does the opposite, bending hardest just off the spine, which reads as a page lifting from its binding rather than a tube unrolling. | `0.2`–`6` |
| `lead` | `0.6` | Where in the turn the curl peaks. Below `1` peaks early, so the page curls as it lifts and has relaxed by the time it lands. | `0.1`–`4` |
| `corner` | `0.05` | Extra curl at the top and bottom edges relative to mid-height. `0` gives a pure cylinder. | `0`–`1.5` |

---

## Leafing through

When a jump crosses more than one spread, the book leafs through instead of turning a single page.

**Check `.almbook status` before spending time here.** If leafing-through is parked, the status line
says so outright, and these knobs will set and save while doing nothing at all.

| Knob | Default | What the number means | Limits |
|---|---|---|---|
| `flipslow` | `0.13` | Seconds for the slowest sheet, which is the first and last of the run. | `0.02`–`0.6` |
| `flipfast` | `0.06` | Seconds for the quickest sheet, at the middle of the run. | `0.02`–`0.6` |
| `flipcurl` | `1.35` | Curl multiplier on a mid-run sheet, because fast paper curls harder. | `0`–`3` |
| `flipscale` | `0.6` | Sheets added per spread of distance. The count is `2 + spreads × flipscale`, then capped. | `0`–`4` |
| `flipmax` | `7` | Ceiling on sheets in one run, landing turn included. | `1`–`24` |
| `flippaper` | `0` | `1` draws mid-run sheets as blank parchment instead of real page pixels. The fallback if real content strobes rather than reading as leafing. | `0` or `1` |

---

## The book in your hands

`.almbook hand` tunes the third-person held-item transform for the Almanac item, which is what
other players see when you are carrying it. This is separate from the reading view and is **not**
saved to the settings file.

- `.almbook hand` reports translation, rotation, origin and scale.
- `.almbook hand tx|ty|tz|rx|ry|rz|scale <n>` sets one value.
- `.almbook hand reset` restores what the item shipped with.

---

## Rendering switches

`.almbook model <0|1>` and `.almbook quad <0|1>` turn the two halves of the physical draw on and
off. These are diagnostic rather than decorative; leave them at `1` unless chasing a rendering
fault.

---

## If the physical book never appears

The physical view needs an offscreen framebuffer. When a driver refuses it, the failure is latched
for the whole session rather than retried every frame, and the book falls back to the flat page.
`.almbook` will say so in place of the usual presentation line. Nothing is lost but the tilt: the
flat page is the entire book minus its presentation.

---

## Defaults, in one line

```
tilt 12, yaw 0, dist 1.0, scale 0.37, fov 50, height 0.06,
bend 1.5, stiff 0.7, lead 0.6, corner 0.05,
flipslow 0.13, flipfast 0.06, flipcurl 1.35, flipscale 0.6, flipmax 7, flippaper 0
```

`.almbook reset` returns every one of them.
