# Examples

## server-overview.example.json

A worked, ready-to-edit server overview chapter. It is a full example, not a stub:
a welcome, the design idea, and a "why X is different here" section for each major
system. The prose is there to be replaced with your own. The Almanac engine ships
no live overview of its own, so this file is the starting point for yours.

To use it:

1. Copy it into your own server (or pack) mod at `assets/<yourserver>/almanac/guides/00-overview.json`.
2. Change `id` to your domain, set the `title` and `byline`, and rewrite the sections for your pack. Fill in the `[Your voice goes here]` author callouts.
3. Keep `overview: true` so it pins first and becomes the book's landing page. Only one chapter across all mods may set it.
4. You do not need a mod list here. The book has its own Contents tab that fills itself from the live modlist.

The `$schema` line gives you autocomplete and validation in any editor that reads JSON Schema. The game ignores it. See `docs/SCHEMA.md` section 11 for the full explanation.
