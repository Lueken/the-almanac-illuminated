using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;

namespace AlmanacIlluminated;

/// <summary>
/// Author preview: reads guide JSON straight off a disk folder, bypassing the
/// frozen startup asset cache, so an author can edit a chapter and see it in
/// game without a restart. Uses the exact same <see cref="GuidePack"/> binding
/// the shipping loader uses, so what you preview is what ships.
///
/// Parse failures are not swallowed (the asset loader logs and drops them);
/// here they are collected and rendered as an in-book error chapter, so the
/// author sees which file broke and why without alt-tabbing to the log.
/// Dev-only, reached through the .almpreview command.
/// </summary>
public static class PreviewSource
{
    /// <summary>The outcome of a preview read: the chapters that parsed, and the files that did not.</summary>
    public sealed record Result(List<GuidePack> Packs, List<(string File, string Error)> Errors);

    /// <summary>
    /// A resolved preview target: an unpacked folder, or a packed .zip (optionally
    /// scoped to an inner path). <see cref="DiskPath"/> is the folder or the .zip
    /// file; for a zip, <see cref="ZipPrefix"/> narrows which entries to read.
    /// </summary>
    public sealed record Location(bool IsZip, string DiskPath, string? ZipPrefix)
    {
        public string Describe() => IsZip
            ? (ZipPrefix == null ? DiskPath : $"{DiskPath} → {ZipPrefix}")
            : DiskPath;
    }

    /// <summary>
    /// Parse a user-supplied target into a Location. A path containing ".zip"
    /// splits into the archive plus the inner folder after it (so a path the
    /// player copied into a zip, like ...mod.zip\assets\x\almanac\guides, works).
    /// Anything else is treated as a plain folder. Returns null only for empty input.
    /// </summary>
    public static Location? Resolve(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        target = target.Trim().Trim('"');

        int zi = target.IndexOf(".zip", StringComparison.OrdinalIgnoreCase);
        if (zi >= 0)
        {
            string zipPath = target.Substring(0, zi + 4);
            string rest = target.Substring(zi + 4).Replace('\\', '/').Trim('/', ' ');
            return new Location(true, zipPath, rest.Length > 0 ? rest : null);
        }
        return new Location(false, target, null);
    }

    /// <summary>Read guides from a resolved target, folder or zip.</summary>
    public static Result Load(ICoreClientAPI capi, Location loc)
        => loc.IsZip ? LoadFromZip(capi, loc.DiskPath, loc.ZipPrefix) : LoadFromFolder(capi, loc.DiskPath);

    /// <summary>
    /// Read and parse every *.json in the folder (one level, like the asset
    /// scanner). Each pack's Source is the file path. Files that fail to parse
    /// land in Errors instead of vanishing.
    /// </summary>
    public static Result LoadFromFolder(ICoreClientAPI capi, string folder)
    {
        var packs = new List<GuidePack>();
        var errors = new List<(string, string)>();

        string[] files;
        try { files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly); }
        catch (Exception e)
        {
            errors.Add((folder, $"Could not read folder: {e.Message}"));
            return new Result(packs, errors);
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            string name = Path.GetFileName(path);
            try
            {
                string json = File.ReadAllText(path);
                var pack = JsonConvert.DeserializeObject<GuidePack>(json);
                if (pack == null) { errors.Add((name, "Parsed to nothing (empty or null JSON).")); continue; }
                pack.Source = path;
                packs.Add(pack);
                IlluminatedLogger.Debug(capi, "preview",
                    $"Read {name}: '{pack.Title}' ({pack.Sections.Count} section(s))");
            }
            catch (JsonException je)
            {
                // Newtonsoft messages already carry the line/position; surface them verbatim.
                errors.Add((name, je.Message));
                IlluminatedLogger.Warn(capi, "preview", $"Parse failed for {name}: {je.Message}");
            }
            catch (Exception e)
            {
                errors.Add((name, e.Message));
                IlluminatedLogger.Warn(capi, "preview", $"Read failed for {name}: {e.Message}");
            }
        }

        return new Result(packs, errors);
    }

    /// <summary>
    /// Read and parse the guide JSON entries inside a packed .zip. With a prefix
    /// (e.g. assets/thequire/almanac/guides) only entries under it are read;
    /// without one, every assets/&lt;mod&gt;/almanac/guides/*.json in the archive is.
    /// The zip is opened read-only, so it can be the one the game itself loaded.
    /// </summary>
    public static Result LoadFromZip(ICoreClientAPI capi, string zipPath, string? prefix)
    {
        var packs = new List<GuidePack>();
        var errors = new List<(string, string)>();

        if (!File.Exists(zipPath))
        {
            errors.Add((Path.GetFileName(zipPath), "No such zip file."));
            return new Result(packs, errors);
        }

        string? norm = prefix?.Replace('\\', '/').Trim('/');
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var matched = archive.Entries
                .Where(en => en.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && IsGuideEntry(en.FullName, norm))
                .OrderBy(en => en.FullName, StringComparer.OrdinalIgnoreCase);

            foreach (var en in matched)
            {
                string name = en.FullName;
                try
                {
                    using var sr = new StreamReader(en.Open());
                    var pack = JsonConvert.DeserializeObject<GuidePack>(sr.ReadToEnd());
                    if (pack == null) { errors.Add((Path.GetFileName(name), "Parsed to nothing (empty or null JSON).")); continue; }
                    pack.Source = $"{zipPath}!{name}";
                    packs.Add(pack);
                    IlluminatedLogger.Debug(capi, "preview", $"Read {name} from zip: '{pack.Title}' ({pack.Sections.Count} section(s))");
                }
                catch (JsonException je) { errors.Add((Path.GetFileName(name), je.Message)); }
                catch (Exception ex) { errors.Add((Path.GetFileName(name), ex.Message)); }
            }

            if (packs.Count == 0 && errors.Count == 0)
                errors.Add((Path.GetFileName(zipPath), norm != null
                    ? $"No *.json under '{norm}' in the zip."
                    : "No almanac/guides/*.json found anywhere in the zip."));
        }
        catch (Exception e)
        {
            // A zip mid-rewrite (a build in progress) is locked; the next watcher event retries.
            errors.Add((Path.GetFileName(zipPath), $"Could not open zip: {e.Message}"));
        }

        return new Result(packs, errors);
    }

    /// <summary>True if a zip entry is a guide JSON we should read for the given scope.</summary>
    private static bool IsGuideEntry(string fullName, string? prefix)
    {
        string f = fullName.Replace('\\', '/');
        return prefix != null
            ? f.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            : f.Contains("almanac/guides/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A synthetic chapter listing the files that failed to parse, so the book
    /// lands on the problem. Overview:true pins it as the default page; it is
    /// built from ordinary heading/paragraph blocks so it renders like any chapter.
    /// </summary>
    public static GuidePack BuildErrorChapter(IReadOnlyList<(string File, string Error)> errors)
    {
        var sections = new List<GuideSection>();

        var intro = new GuideSection { Id = "preview-error-intro", Title = "Preview — Problems Found" };
        intro.Blocks.Add(Paragraph(
            $"{errors.Count} guide file{(errors.Count == 1 ? "" : "s")} could not be read. " +
            "Fix the file and save; the preview reloads on its own. The chapters that did parse follow this page."));
        sections.Add(intro);

        foreach (var (file, error) in errors)
        {
            var sec = new GuideSection { Id = "preview-error-" + file };
            sec.Blocks.Add(Heading(file));
            sec.Blocks.Add(Paragraph(error));
            sections.Add(sec);
        }

        return new GuidePack
        {
            Id = "preview-errors",
            Title = "Preview — Problems Found",
            Byline = "by the Almanac",
            Overview = true,   // pin the error page as the landing page so it cannot be missed
            Sections = sections,
            Source = "(preview)",
        };
    }

    private static GuideBlock Heading(string text) => Block("heading", "text", text);
    private static GuideBlock Paragraph(string text) => Block("paragraph", "text", text);

    private static GuideBlock Block(string type, string key, string value)
    {
        var b = new GuideBlock { Type = type };
        b.Props[key] = new JValue(value);
        return b;
    }

    /// <summary>
    /// Best guess at the author's live guides folder, for `.almpreview` with no
    /// argument: the running mod's own source assets. Returns null if it cannot
    /// be found, in which case the author passes an explicit path.
    /// </summary>
    public static string? ResolveDefaultFolder(ICoreClientAPI capi)
    {
        const string sub = "assets/almanacilluminated/almanac/guides";
        var candidates = new List<string>();

        var mod = capi.ModLoader.Mods.FirstOrDefault(m =>
            string.Equals(m.Info?.ModID, "almanacilluminated", StringComparison.OrdinalIgnoreCase));
        string? src = mod?.SourcePath;
        if (!string.IsNullOrEmpty(src))
        {
            // src may be the mod folder, or the assembly/zip inside it. Try the
            // folder and its parent so an unpacked dev build resolves either way.
            candidates.Add(Path.Combine(src, sub));
            try
            {
                string? dir = Directory.Exists(src) ? src : Path.GetDirectoryName(src);
                if (dir != null)
                {
                    candidates.Add(Path.Combine(dir, sub));
                    var parent = Directory.GetParent(dir)?.FullName;
                    if (parent != null) candidates.Add(Path.Combine(parent, sub));
                }
            }
            catch { /* path probing only; an unreadable candidate just doesn't match */ }
        }

        foreach (var c in candidates)
        {
            try { if (Directory.Exists(c)) return Path.GetFullPath(c); }
            catch { /* skip malformed candidate */ }
        }
        return null;
    }
}
