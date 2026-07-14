using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// A book tab supplied by another mod. Register through
/// AlmanacIlluminatedModSystem.RegisterBookTab (client side, any time before the
/// book is opened); the label joins the left ribbon after the built-in tabs.
/// This is the extension seam the Almanac suite builds on — TCM's Callings page
/// first, ascension/badge/questbook pages later.
/// </summary>
public interface IAlmanacBookTab
{
    /// <summary>Ribbon label. Keep it one short word; the ribbons are narrow.</summary>
    string Label { get; }

    /// <summary>Sort position among all mod tabs (computed tabs and ribbon-tab
    /// chapters share one ordering), lower first. Defaults to 100; override to
    /// group a mod's tabs in a chosen order. Ties break by label.</summary>
    int Order => 100;

    /// <summary>Called once when the book binds this tab, handing it a host it can
    /// use to request a recompose after changing its own view-state (the drill-down
    /// seam). Default: ignore, for tabs that never drill in.</summary>
    void OnAttached(IAlmanacBookTabHost host) { }

    /// <summary>Called when the tab is chosen from the ribbon, so it can reset to its
    /// top-level view rather than reopen wherever it last was. Default: ignore.</summary>
    void OnActivated() { }

    /// <summary>Spread layout: 2 = one column per page (chapter-like),
    /// 4 = two narrow columns per page (Crops-like). Other values read as 4.</summary>
    int ColumnsPerSpread { get; }

    /// <summary>
    /// Build the tab's content as packed columns; the book shows ColumnsPerSpread
    /// of them per spread and page-turns through the rest. Called lazily on first
    /// view and again on each book open (so live data re-reads), with unscaled GUI
    /// units. Pack to columnHeight yourself — ChapterRenderer.MeasureHeight is the
    /// measuring helper the built-in tabs use.
    /// </summary>
    List<RichTextComponentBase[]> BuildColumns(ICoreClientAPI capi, double columnWidth, double columnHeight);
}
