using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// THE SPOTTED PAGE's seam: an external mod may lay a "lens" over the book's text.
/// Illuminated knows nothing about who wires this or why — a null Filter is the
/// permanent default and costs nothing. The wirer (today: The Marginalia's
/// corruption layer, by reflection, no compile-time reference either way) supplies
/// a text transform; the book applies it to every text run at compose time and
/// owns everything spatial: hit-testing the reader's lens against laid-out lines,
/// steadying the runs beneath it, and recomposing when the state moves.
///
/// CONTRACT for the Filter: it receives the raw VTML source of one text run plus a
/// stable per-run seed, and must return markup-safe output (tags and entities
/// intact). It must be deterministic in (text, seed) — the book recomposes freely
/// and the page must not shimmer. When the transform's ambient state changes
/// (whatever it derives its strength from), the wirer bumps Generation; the open
/// book notices and re-inks every cached page.
///
/// Zero-width combining marks are the intended vocabulary: they stack over glyphs
/// without changing text metrics, so a foxed page and a clean page lay out
/// IDENTICALLY, which is what lets a steadied region swap in place with no reflow.
/// </summary>
public static class TextLens
{
    /// <summary>The transform: (vtmlText, seed) -> vtmlText. Null = no lens, zero cost.</summary>
    public static Func<string, long, string>? Filter;

    /// <summary>The plain-text sibling of <see cref="Filter"/>, for runs whose
    /// renderer takes plain text and never parses VTML (the callout boxes draw
    /// their own prose). CONTRACT: plain text in, plain text out — any markup in
    /// the output would print literally on the page.</summary>
    public static Func<string, long, string>? PlainFilter;

    /// <summary>Answers "is the reader's steadying lens in hand right now" — the
    /// wirer's notion of a lens (an item on the cursor, a worn trinket, whatever).
    /// Null or false = nothing steadies; the page reads as the Filter wrote it.</summary>
    public static Func<bool>? LensHeld;

    /// <summary>Bumped by the wirer whenever Filter's output would change for the
    /// same input run. The open book watches this and drops its pagination cache.</summary>
    public static int Generation;

    /// <summary>Run ordinals composing CLEAN this pass (the section under the held
    /// lens). Written by the dialog's hit-test, read by the renderer. Ordinals are
    /// assigned in compose order and are stable per chapter because compose is
    /// deterministic.</summary>
    public static readonly HashSet<int> SteadyRuns = new();

    /// <summary>Which run each composed component belongs to, stamped at compose,
    /// weak so cached chapters never pin entries past their own lifetime.</summary>
    public static readonly ConditionalWeakTable<RichTextComponentBase, StrongBox<int>> RunOf = new();

    /// <summary>Radius of the steadied window around the cursor, unscaled px. [TUNE]</summary>
    public static double LensRadius = 70;

    /// <summary>True once the book's fonts can render combining marks (the bundled
    /// derivative serifs registered — see FontRegistry). A wirer whose transform
    /// leans on combining marks should check this and fall back to a markless
    /// vocabulary when false: the game's own Lora/Almendra draw every mark as a
    /// tofu box. Set before any compose, at client font registration.</summary>
    public static bool MarksCapable;
}
