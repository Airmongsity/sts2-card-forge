using System.Text.Json;
using System.Text.Json.Nodes;

namespace CardForge.Services;

/// <summary>Locates the project root (the folder holding backend/server.py) and the shared settings file.</summary>
public static class AppPaths
{
    public static string Root { get; } = FindRoot();
    public static string Backend => Path.Combine(Root, "backend");
    public static string Data => Path.Combine(Root, "data");
    public static string Tools => Path.Combine(Data, "tools");
    public static string Logs => Path.Combine(Data, "logs");
    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string BundledLora => Path.Combine(Root, "assets", "lora");

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "backend", "server.py")))
                return d.FullName;
        return dir.FullName;
    }

    // ---- settings.json (the backend owns it while running; the app edits it directly only during setup)

    public static JsonObject LoadSettings()
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsFile)) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    public static void SaveSettings(Action<JsonObject> edit)
    {
        var s = LoadSettings();
        edit(s);
        Directory.CreateDirectory(Data);
        File.WriteAllText(SettingsFile, s.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }

    /// <summary>First run without a configured ComfyUI: adopt a portable ComfyUI that sits next to the app or in a
    /// parent folder, so an existing install is reused instead of downloaded again.</summary>
    public static void DetectComfyRoot()
    {
        var configured = LoadSettings()["comfy_root"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured)) return;
        for (var d = new DirectoryInfo(Root); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "ComfyUI_windows_portable");
            if (File.Exists(Path.Combine(candidate, "python_embeded", "python.exe")))
            {
                ComfyRoot = candidate;
                return;
            }
        }
    }

    public static string ComfyRoot
    {
        get => LoadSettings()["comfy_root"]?.GetValue<string>() is { Length: > 0 } r ? r : Path.Combine(Root, "ComfyUI_windows_portable");
        set => SaveSettings(s => s["comfy_root"] = value);
    }

    public static string ComfyDir => File.Exists(Path.Combine(ComfyRoot, "ComfyUI", "main.py")) ? Path.Combine(ComfyRoot, "ComfyUI") : ComfyRoot;
    public static string Python => Path.Combine(ComfyRoot, "python_embeded", "python.exe");
    public static string Models => Path.Combine(ComfyDir, "models");
}
