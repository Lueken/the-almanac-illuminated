using System;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A clickable link that keeps its ink. The base LinkTextComponent recolors every
/// link to the UI's active-button colour, which is near-invisible on parchment;
/// this restores the caller's font so the text reads as page ink. The base still
/// draws its underline (now in ink) and brightens on hover — the "this is
/// clickable" cue, without the jarring button colour. Clicks call onClick
/// directly (no href needed), so a tab captures its target in the closure.
/// </summary>
public class BookLinkComponent : LinkTextComponent
{
    public BookLinkComponent(ICoreClientAPI api, string displayText, CairoFont font, Action<LinkTextComponent> onClick)
        : base(api, displayText, font, onClick)
    {
        Font = font.Clone();
    }
}
