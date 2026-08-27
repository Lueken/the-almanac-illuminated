using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AlmanacIlluminated;

/// <summary>
/// The reader's own settings, bound into the book as a tab: the cover picker today,
/// and the home for whatever else wants tweaking in-game rather than through
/// <c>.almbook</c>. It rides the same seam mod tabs use, so it costs the dialog no
/// new mode plumbing.
///
/// <para>Every binding is shown as the REAL item — the same stack the handbook draws
/// for that hide or bolt of cloth — because a swatch of colour is an abstraction and
/// the hide is the thing you would actually bind a book in. Clicking one applies it
/// at once: no confirm, no apply button. It is a cover, and changing your mind costs
/// a second click.</para>
/// </summary>
public class BindingTab : IAlmanacBookTab
{
    private readonly Func<string> get;
    private readonly Action<string> set;
    private IAlmanacBookTabHost? host;

    public BindingTab(Func<string> get, Action<string> set)
    {
        this.get = get;
        this.set = set;
    }

    public string Label => "Binding";

    /// <summary>Last of the built-in tabs: settings sit behind the content.</summary>
    public int Order => 900;

    /// <summary>Hangs under the reader's tools, below Index — it is about the book,
    /// not about what is written in it.</summary>
    public bool FootGroup => true;

    public int ColumnsPerSpread => 4;

    public void OnAttached(IAlmanacBookTabHost host) => this.host = host;

    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 1 };
    private static readonly double[] Muted = { 0.42, 0.36, 0.28, 1 };
    private static readonly double[] Rubric = { 0.541, 0.353, 0.133, 1 };

    public List<RichTextComponentBase[]> BuildColumns(ICoreClientAPI capi, double columnWidth, double columnHeight)
    {
        CairoFont heading = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink).WithFontSize(22f);
        CairoFont body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithColor(Ink).WithFontSize(17f);
        CairoFont mutedItalic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithSlant(Cairo.FontSlant.Italic).WithColor(Muted).WithFontSize(15.5f);
        CairoFont caps = CairoFont.WhiteSmallText().WithFont(FontRegistry.DisplaySans)
            .WithColor(Muted).WithFontSize(14f);
        CairoFont chosen = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Rubric).WithFontSize(15.5f);

        string current = get() ?? CoverLibrary.DefaultKey;

        var columns = new List<RichTextComponentBase[]>();
        var col = new List<RichTextComponentBase>
        {
            new RichTextComponent(capi, "The Binding\n", heading),
            new ClearFloatTextComponent(capi, 2),
            new RichTextComponent(capi,
                "Bind this copy in whatever you have a mind to. The choice is yours and yours only — it travels with your book, not with what you are reading.\n",
                mutedItalic),
            new ClearFloatTextComponent(capi, 10),
        };

        string group = "";
        int shown = 0;
        foreach (CoverLibrary.Cover cover in CoverLibrary.All)
        {
            ItemStack? stack = CoverLibrary.StackFor(capi, cover);
            if (stack == null) continue;   // material absent from this world: not on offer

            string g = cover.Key == CoverLibrary.DefaultKey ? "As it came"
                : cover.Key.StartsWith("leather-") ? "Leather"
                : cover.Key.StartsWith("cloth-") ? "Dyed cloth"
                : "Wool";
            if (g != group)
            {
                group = g;
                if (shown > 0) col.Add(new ClearFloatTextComponent(capi, 12));
                col.Add(new RichTextComponent(capi, g.ToUpperInvariant() + "\n", caps));
                col.Add(new DividerComponent(capi, 6));
                // A divider floats and must clear before the next line.
                col.Add(new ClearFloatTextComponent(capi, 8));
            }

            string key = cover.Key;
            bool isCurrent = key == current;

            col.Add(new ItemstackTextComponent(capi, stack, 40.0, 4.0, EnumFloat.Inline,
                _ => Apply(key)));
            col.Add(new RichTextComponent(capi, " " + cover.Display + (isCurrent ? "  " : "\n"),
                isCurrent ? chosen : body));
            if (isCurrent) col.Add(new RichTextComponent(capi, "· in use now\n", mutedItalic));
            col.Add(new ClearFloatTextComponent(capi, 6));
            shown++;
        }

        if (shown == 0)
        {
            col.Add(new RichTextComponent(capi,
                "No binding materials could be found in this world.\n", mutedItalic));
        }

        // Flow into page-height columns: the list is longer than one page.
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double availH = columnHeight * scale;
        var run = new List<RichTextComponentBase>();
        foreach (RichTextComponentBase c in col)
        {
            var trial = new List<RichTextComponentBase>(run) { c };
            if (run.Count > 0 && ChapterRenderer.MeasureHeight(capi, trial.ToArray(), columnWidth) > availH)
            {
                columns.Add(run.ToArray());
                run = new List<RichTextComponentBase> { c };
            }
            else run = trial;
        }
        if (run.Count > 0) columns.Add(run.ToArray());
        if (columns.Count == 0) columns.Add(Array.Empty<RichTextComponentBase>());
        return columns;
    }

    private void Apply(string key)
    {
        set(key);
        host?.Recompose();   // redraw so the "in use now" mark moves with the click
    }
}
