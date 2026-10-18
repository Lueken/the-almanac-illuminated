using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AlmanacIlluminated;

/// <summary>
/// Registers the Almanac's bundled fonts with the OS font system at client
/// startup so Cairo's <c>SelectFontFace(family)</c> can resolve them by name.
/// VS has no mod-font auto-scan, so we do it ourselves.
///
/// Windows: GDI <c>AddFontResourceExW</c> with FR_PRIVATE (process-scoped, no
/// install, no admin). Non-Windows: not yet implemented — callers fall back to
/// the shipped serifs (Lora/Almendra). This is a client-only mod so servers
/// never reach here.
/// </summary>
public static class FontRegistry
{
    // Family names (verified from each font's name table). Use via CairoFont.WithFont(...).
    // Script (Eyesome Script) is not bundled yet: its license is unconfirmed, so it
    // is held out of the public repo. Re-add the file and a FontFiles entry once cleared.
    public const string Script = "Eyesome Script";   // reserved, font not bundled
    public const string Sans = "Josefin Sans";       // UI chrome: tabs, keybinds, page numbers
    public const string DisplaySans = "Odibee Sans"; // small-caps section headers

    // Manuscript serifs. The game's own Lora/Almendra carry ZERO combining marks
    // (cmap verified 2026-09-03), which is why THE SPOTTED PAGE's garble drew
    // tofu boxes. The bundled derivatives — "Illuminated Serif"/"Illuminated
    // Decorative", built by tools/build-foxed-fonts.py — are those same faces
    // with the nine marks the game's Montserrat carries grafted in (all OFL;
    // renamed per the license's Reserved Font Name rule). When all seven faces
    // register, the book swaps to them: pixel-identical rendering until a
    // combining mark is actually asked for. Where registration is unavailable
    // the game families stay. The faces are kept through the 0.3.3 withdrawal of
    // the lens that motivated them: they render identically either way, and
    // ripping them back out would churn every page for no visible difference.
    public static string SerifBody => foxCapable ? "Illuminated Serif" : "Lora";
    public static string SerifDecorative => foxCapable ? "Illuminated Decorative" : "Almendra";
    private static bool foxCapable;

    private static readonly string[] FontFiles =
    {
        "JosefinSans-Regular.ttf", "JosefinSans-Bold.ttf",
        "JosefinSans-Italic.ttf", "JosefinSans-BoldItalic.ttf",
        "JosefinSans-SemiBold.ttf", "JosefinSans-Light.ttf",
        "OdibeeSans-Regular.ttf",
    };

    /// <summary>The mark-capable serif derivatives. Registered as a set: a partial
    /// registration would let GDI substitute faces across families mid-paragraph,
    /// so it is all seven or none.</summary>
    private static readonly string[] FoxedFontFiles =
    {
        "IlluminatedSerif-Regular.ttf", "IlluminatedSerif-Italic.ttf",
        "IlluminatedSerif-Bold.ttf", "IlluminatedSerif-BoldItalic.ttf",
        "IlluminatedDecorative-Regular.ttf", "IlluminatedDecorative-Italic.ttf",
        "IlluminatedDecorative-Bold.ttf",
    };

    private const string AssetSubPath = "assets/almanacilluminated/fonts";
    private const uint FR_PRIVATE = 0x10;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceExW(string lpszFilename, uint fl, nint pdv);

    private static bool registered;

    public static void RegisterAll(ICoreClientAPI capi)
    {
        if (registered) return;
        registered = true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            IlluminatedLogger.Warn(capi, "fonts",
                "Custom font registration is Windows-only for now — falling back to shipped serifs (Lora/Almendra).");
            return;
        }

        Mod? mod = null;
        foreach (var m in capi.ModLoader.Mods)
        {
            if (m.Info?.ModID == "almanacilluminated") { mod = m; break; }
        }
        if (mod == null)
        {
            IlluminatedLogger.Error(capi, "fonts", "Could not locate own mod to source font files.");
            return;
        }

        string cacheDir = Path.Combine(GamePaths.Cache, "almanacilluminated-fonts");
        Directory.CreateDirectory(cacheDir);

        int RegisterOne(string file)
        {
            try
            {
                string dest = Path.Combine(cacheDir, file);
                if (!ExtractFont(mod, file, dest))
                {
                    IlluminatedLogger.Warn(capi, "fonts", $"Font not found in mod: {file}");
                    return 0;
                }

                int added = AddFontResourceExW(dest, FR_PRIVATE, 0);
                if (added > 0) { IlluminatedLogger.Debug(capi, "fonts", $"Registered {file} ({added} face(s))"); return 1; }
                IlluminatedLogger.Warn(capi, "fonts", $"AddFontResourceExW returned 0 for {file}");
            }
            catch (System.Exception e)
            {
                IlluminatedLogger.Error(capi, "fonts", $"Failed registering {file}: {e.Message}");
            }
            return 0;
        }

        int ok = 0;
        foreach (var file in FontFiles) ok += RegisterOne(file);

        int foxed = 0;
        foreach (var file in FoxedFontFiles) foxed += RegisterOne(file);
        foxCapable = foxed == FoxedFontFiles.Length;

        IlluminatedLogger.Info(capi, "fonts",
            $"Registered {ok}/{FontFiles.Length} font files — families: '{Script}', '{Sans}', '{DisplaySans}'; "
            + (foxCapable
                ? $"mark-capable serifs up ({foxed}/{FoxedFontFiles.Length}) — body '{SerifBody}', decorative '{SerifDecorative}'"
                : $"mark-capable serifs incomplete ({foxed}/{FoxedFontFiles.Length}) — staying on the game's Lora/Almendra"));
    }

    /// <summary>Copy (folder mod) or extract (zip mod) one font file to dest. Returns false if absent.</summary>
    private static bool ExtractFont(Mod mod, string file, string dest)
    {
        if (mod.SourceType == EnumModSourceType.Folder)
        {
            string src = Path.Combine(mod.SourcePath, AssetSubPath.Replace('/', Path.DirectorySeparatorChar), file);
            if (!File.Exists(src)) return false;
            // Refresh only when changed, so we don't churn the cache every launch.
            if (!File.Exists(dest) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest))
                File.Copy(src, dest, overwrite: true);
            return true;
        }

        if (mod.SourceType == EnumModSourceType.ZIP)
        {
            using var zip = ZipFile.OpenRead(mod.SourcePath);
            var entry = zip.GetEntry($"{AssetSubPath}/{file}");
            if (entry == null) return false;
            if (!File.Exists(dest) || entry.Length != new FileInfo(dest).Length)
                entry.ExtractToFile(dest, overwrite: true);
            return true;
        }

        return false; // CS / DLL mods carry no assets
    }
}
