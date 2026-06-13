using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>One page's worth of rendered content: a title and its richtext components.</summary>
public record RenderedSection(string Title, RichTextComponentBase[] Components);
