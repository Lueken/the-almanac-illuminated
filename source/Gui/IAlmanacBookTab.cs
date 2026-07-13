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
