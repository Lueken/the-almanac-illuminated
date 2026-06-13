using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// Phase 0 spike: the open book as a native GuiDialog. Two-page spread of
/// heavy richtext content, page turning via recomposition, composition time
/// logged per spread. The book frame art, materialize animation, and sprite
/// page-flip arrive in Phase 2/3 — this dialog exists to prove (or kill)
/// native composition as the foundation.
/// </summary>
public class GuiDialogIlluminatedBook : GuiDialog
{
    public const string HotkeyCode = "almanacilluminatedbook";

    private const double PageWidth = 420;
    private const double PageHeight = 580;
    private const double GutterWidth = 40;

    private List<MockChapter.Section>? sections;
    private int spreadIndex;

    public override string ToggleKeyCombinationCode => HotkeyCode;

    public GuiDialogIlluminatedBook(ICoreClientAPI capi) : base(capi)
    {
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();

        if (sections == null)
        {
            var sw = Stopwatch.StartNew();
            sections = MockChapter.Generate(capi);
            IlluminatedLogger.Info(capi, "book", $"Mock chapter generated: {sections.Count} sections in {sw.ElapsedMilliseconds} ms");
        }

        ComposeSpread();
    }

    private void ComposeSpread()
    {
        if (sections == null || sections.Count == 0) return;

        var sw = Stopwatch.StartNew();

        int leftIdx = spreadIndex * 2;
        int rightIdx = leftIdx + 1;

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        ElementBounds leftPage = ElementBounds.Fixed(0, 35, PageWidth, PageHeight);
        ElementBounds rightPage = ElementBounds.Fixed(PageWidth + GutterWidth, 35, PageWidth, PageHeight);

        ElementBounds prevBtn = ElementBounds.Fixed(0, 45 + PageHeight, 110, 28);
        ElementBounds pageLabel = ElementBounds.Fixed(PageWidth - 60, 50 + PageHeight, 160, 28);
        ElementBounds nextBtn = ElementBounds.Fixed(PageWidth + GutterWidth + PageWidth - 110, 45 + PageHeight, 110, 28);

        bgBounds.WithChildren(leftPage, rightPage, prevBtn, pageLabel, nextBtn);

        int maxSpread = (sections.Count - 1) / 2;

        var composer = capi.Gui
            .CreateCompo("illuminatedbook", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("The Almanac — Phase 0 spike", OnTitleBarClose)
            .AddRichtext(sections[leftIdx].Components, leftPage, "leftpage");

        if (rightIdx < sections.Count)
        {
            composer.AddRichtext(sections[rightIdx].Components, rightPage, "rightpage");
        }

        // UI chrome in Josefin Sans (role split): page label + keybind hints
        var chromeFont = CairoFont.WhiteSmallText().WithFont(FontRegistry.Sans);
        composer
            .AddSmallButton("◀ Previous", OnPrevPage, prevBtn)
            .AddRichtext($"<font align=\"center\">Spread {spreadIndex + 1} / {maxSpread + 1}</font>",
                chromeFont, pageLabel, "pagelabel")
            .AddSmallButton("Next ▶", OnNextPage, nextBtn);

        SingleComposer = composer.Compose();

        // The number Phase 0 lives or dies on. Target: < ~30 ms per spread on iGPU.
        IlluminatedLogger.Info(capi, "book",
            $"Spread {spreadIndex + 1}/{maxSpread + 1} composed in {sw.ElapsedMilliseconds} ms " +
            $"({sections[leftIdx].Components.Length}+{(rightIdx < sections.Count ? sections[rightIdx].Components.Length : 0)} components)");
    }

    private bool OnPrevPage()
    {
        if (spreadIndex > 0)
        {
            spreadIndex--;
            PlayPageTurnSound();
            ComposeSpread();
        }
        return true;
    }

    private bool OnNextPage()
    {
        if (sections != null && (spreadIndex + 1) * 2 < sections.Count)
        {
            spreadIndex++;
            PlayPageTurnSound();
            ComposeSpread();
        }
        return true;
    }

    private void PlayPageTurnSound()
    {
        // Vanilla book page sound for the spike; own randomized set in Phase 3
        // (prior art: Sketchbook ships 3 pageturn oggs — the sound carries the flip).
        capi.World.PlaySoundAt(new AssetLocation("game:sounds/effect/writing"),
            capi.World.Player.Entity, null, true, 8);
    }

    private void OnTitleBarClose() => TryClose();

    public override bool PrefersUngrabbedMouse => true;
}
