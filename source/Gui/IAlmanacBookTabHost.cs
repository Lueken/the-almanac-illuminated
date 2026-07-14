namespace AlmanacIlluminated;

/// <summary>
/// Handed to a book tab so it can drive the book after changing its own internal
/// view-state — the seam a tab uses to drill from an overview into a detail page
/// and back. The book implements this; a tab holds the reference and calls it
/// from a link's click handler (never from inside BuildColumns).
/// </summary>
public interface IAlmanacBookTabHost
{
    /// <summary>Rebuild the active tab's columns and redraw, landing on the first
    /// spread. Call after the tab changes what it wants to show.</summary>
    void Recompose();
}
