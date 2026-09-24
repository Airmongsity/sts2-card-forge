using System.Globalization;

namespace CardForge.Services;

/// <summary>Two-language UI strings: every call site carries both texts, so there is no key table to keep in sync.
/// Used from XAML as {x:Bind s:L.Z('中文','English')}.</summary>
public static class L
{
    public static bool Zh { get; private set; } = Detect();

    static bool Detect()
    {
        var saved = AppPaths.LoadSettings()["ui_language"]?.GetValue<string>();
        var lang = string.IsNullOrEmpty(saved) ? CultureInfo.CurrentUICulture.Name : saved;
        return lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    public static string Z(string zh, string en) => Zh ? zh : en;

    /// <summary>"Ironclad / 铁甲战士" style names from the backend: pick the half for the current language.</summary>
    public static string Pick(string both)
    {
        var parts = both.Split(" / ", 2);
        return parts.Length == 2 ? (Zh ? parts[1] : parts[0]) : both;
    }
}
