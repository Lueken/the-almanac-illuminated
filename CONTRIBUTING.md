# Contributing to The Almanac: Illuminated

Thanks for looking. This is a Vintage Story mod, MIT licensed, built by Venah.

Illuminated is the Almanac's reading surface and quest host: guide packs, per-domain chapters, reveal
gating, and the quest engine that TCM's Grandmaster ascension and the tutorial chains run on. Other
mods depend on it, which raises the bar on anything that changes a public contract.

## Read these two first

1. **`CONVENTIONS.md`** (workshop root), the nine engineering conventions every mod here follows.
2. **`docs/SCHEMA.md`**: the guide-pack schema. It is a published contract that third-party mods
   author against, so schema changes are versioned, not edited.

## Before you open a PR

- **A changed number brings its baseline**, signed and dated. See `CONVENTIONS.md` § 1.
- **Guard content sweeps per item and count the skips.** Illuminated loads guide packs from every
  mod in the pack via `capi.Assets.GetMany`. One malformed third-party pack must not take the book
  down, and the log line must name the file that did it. See § 5.
- **New abstractions need a second shipping consumer.** See § 4.
- **Third-party code carries its grant.** Illuminated needs a `THIRD-PARTY-LICENSES/` folder; the
  Progression Framework quest core ported into it is MIT and its grant belongs there, with a header
  on the ported files. This is currently an open gap. See § 8.
- **Reveal keys fail loudly, not open.** A key whose namespace has no registered provider renders as
  revealed, which is correct for a missing optional mod and wrong for a typo. Validate at load and
  report the file path.
- **Schema changes are additive and versioned.** Guide packs in the wild are authored by people who
  are not in this repository.

## What is likely to be accepted

Bug fixes with a reproduction. Rendering and layout fixes. Localisation. Documentation and worked
examples in `docs/examples/`. New guide-pack capabilities that are backward compatible.

## What to discuss before building

Anything that changes `docs/SCHEMA.md`, the quest engine's public behaviour, or the reveal semantics.
Downstream mods and hand-authored guide packs depend on all three.

## Reporting a bug

Say what you did, what happened, what you expected, and the mod list you were running. Include the
guide pack involved if it is a rendering or reveal issue.
