using Vintagestory.API.Common;

namespace AlmanacIlluminated;

/// <summary>
/// Structured logging for Illuminated. Every line is prefixed
/// <c>[almanac:illuminated:&lt;component&gt;]</c> so it greps cleanly out of
/// client-main.log. Verbose categorized debug logging on by default, per
/// Almanac convention.
/// </summary>
public static class IlluminatedLogger
{
    private const string Prefix = "almanac:illuminated";

    public static void Info(ICoreAPI api, string component, string message)
        => api.Logger.Notification($"[{Prefix}:{component}] {message}");

    public static void Debug(ICoreAPI api, string component, string message)
        => api.Logger.Debug($"[{Prefix}:{component}] {message}");

    public static void Warn(ICoreAPI api, string component, string message)
        => api.Logger.Warning($"[{Prefix}:{component}] {message}");

    public static void Error(ICoreAPI api, string component, string message)
        => api.Logger.Error($"[{Prefix}:{component}] {message}");
}
