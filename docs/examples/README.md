# Examples

## server-overview.example.json

A ready-to-edit server overview chapter. To use it:

1. Copy it into your own server mod at `assets/<yourserver>/almanac/guides/00-overview.json`.
2. Change `id` to your domain, set the `title`, and write your welcome, rules, and intent.
3. Keep `overview: true` so it pins first and becomes the book's landing page.
4. Leave the `contents` block in place. The book fills it with the live modlist on its own.

The `$schema` line gives you autocomplete and validation in any editor that reads JSON Schema. The game ignores it. See `docs/SCHEMA.md` section 11 for the full explanation.
