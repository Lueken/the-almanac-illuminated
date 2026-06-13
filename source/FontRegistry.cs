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

    // Shipped manuscript serifs (always available, no registration needed).
    public const string SerifBody = "Lora";
    public const string SerifDecorative = "Almendra";

    private static readonly string[] FontFiles =
    {
        "JosefinSans-Regular.ttf", "JosefinSans-Bold.ttf",
        "JosefinSans-Italic.ttf", "JosefinSans-BoldItalic.ttf",
        "JosefinSans-SemiBold.ttf", "JosefinSans-Light.ttf",
        "OdibeeSans-Regular.ttf",
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

        int ok = 0;
        foreach (var file in FontFiles)
        {
            try
            {
                string dest = Path.Combine(cacheDir, file);
                if (!ExtractFont(mod, file, dest))
                {
                    IlluminatedLogger.Warn(capi, "fonts", $"Font not found in mod: {file}");
                    continue;
                }

                int added = AddFontResourceExW(dest, FR_PRIVATE, 0);
                if (added > 0) { ok++; IlluminatedLogger.Debug(capi, "fonts", $"Registered {file} ({added} face(s))"); }
                else IlluminatedLogger.Warn(capi, "fonts", $"AddFontResourceExW returned 0 for {file}");
            }
            catch (System.Exception e)
            {
                IlluminatedLogger.Error(capi, "fonts", $"Failed registering {file}: {e.Message}");
            }
        }

        IlluminatedLogger.Info(capi, "fonts",
            $"Registered {ok}/{FontFiles.Length} font files — families: '{Script}', '{Sans}', '{DisplaySans}'");
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
