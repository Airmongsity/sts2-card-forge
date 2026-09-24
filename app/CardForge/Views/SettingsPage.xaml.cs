using System.Diagnostics;
using System.Text.Json.Nodes;
using CardForge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CardForge.Views;

/// <summary>One entry of the "Provider" dropdown; OpenAI-compatible presets only differ in base URL and model.</summary>
public record LlmPreset(string Id, string Name, string Provider, string BaseUrl, string Model, string KeyHint);

public sealed partial class SettingsPage : Page
{
    static readonly LlmPreset[] Presets =
    [
        new("claude", "Claude (Anthropic)", "anthropic", "", "claude-opus-5",
            L.Z("在 console.anthropic.com 获取 API key。留空则使用环境变量 ANTHROPIC_API_KEY。", "Get a key at console.anthropic.com. Leave blank to use the ANTHROPIC_API_KEY environment variable.")),
        new("openai", "OpenAI", "openai", "https://api.openai.com/v1", "", L.Z("填写你账户可用的模型名。", "Enter a model name your account can use.")),
        new("deepseek", "DeepSeek", "openai", "https://api.deepseek.com/v1", "deepseek-chat", ""),
        new("qwen", L.Z("通义千问 (阿里云百炼)", "Qwen (Alibaba Cloud)"), "openai", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", ""),
        new("gemini", "Google Gemini", "openai", "https://generativelanguage.googleapis.com/v1beta/openai", "", L.Z("填写你账户可用的模型名。", "Enter a model name your account can use.")),
        new("openrouter", "OpenRouter", "openai", "https://openrouter.ai/api/v1", "", L.Z("模型名形如 vendor/model。", "Model names look like vendor/model.")),
        new("ollama", L.Z("Ollama（本地，免费）", "Ollama (local, free)"), "openai", "http://127.0.0.1:11434/v1", "qwen3:8b",
            L.Z("本地运行无需 key。注意：本地大模型与出图共用显存，8 GB 显卡建议用小模型或云端服务。", "No key needed. Note: a local LLM shares VRAM with image generation; on 8 GB cards use a small model or a cloud service.")),
        new("lmstudio", L.Z("LM Studio（本地，免费）", "LM Studio (local, free)"), "openai", "http://127.0.0.1:1234/v1", "", L.Z("本地运行无需 key。", "No key needed.")),
        new("custom", L.Z("其他 OpenAI 兼容接口", "Other OpenAI-compatible API"), "openai", "", "", ""),
        new("template", L.Z("离线模板（不使用 AI）", "Offline template (no AI)"), "template", "", "", ""),
    ];

    static readonly Option[] Profiles =
    [
        new() { Id = "auto", Name = L.Z("自动（按显存/内存选择）", "Auto (by VRAM / RAM)") },
        new() { Id = "standard", Name = L.Z("标准（8 GB 显存 + 16 GB 内存及以上）", "Standard (8 GB VRAM + 16 GB RAM or more)") },
        new() { Id = "low", Name = L.Z("低配（6 GB 显存或内存紧张）", "Low (6 GB VRAM or tight RAM)") },
        new() { Id = "minimal", Name = L.Z("最低（4 GB 显存，非常慢）", "Minimum (4 GB VRAM, very slow)") },
    ];

    static readonly Option[] PreviewModes =
    [
        new() { Id = "latent2rgb", Name = L.Z("低开销预览 latent2rgb（推荐）", "Cheap preview, latent2rgb (recommended)") },
        new() { Id = "none", Name = L.Z("关闭", "Off") },
        new() { Id = "auto", Name = L.Z("高质量预览 auto（仅限大显存）", "High-quality preview, auto (large VRAM only)") },
    ];

    static readonly Option[] UiLangs =
    [
        new() { Id = "", Name = L.Z("跟随系统", "Follow Windows") },
        new() { Id = "zh-CN", Name = "简体中文" },
        new() { Id = "en-US", Name = "English" },
    ];

    static readonly Option[] CardLangs =
    [
        new() { Id = "zh", Name = "简体中文" },
        new() { Id = "en", Name = "English" },
        new() { Id = "ja", Name = "日本語" },
    ];

    static string NoLora => L.Z("（不使用 LoRA）", "(no LoRA)");
    JsonObject _s = new();
    bool _loading;
    string _savedPatch = "";
    readonly DispatcherTimer _dirtyTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    bool Dirty => !_loading && _savedPatch.Length > 0 && Snapshot() != _savedPatch;
    string Snapshot() => BuildPatch() is JsonObject o ? o.ToJsonString() : "";

    public SettingsPage()
    {
        InitializeComponent();
        PresetBox.ItemsSource = Presets;
        ProfileBox.ItemsSource = Profiles;
        PreviewBox.ItemsSource = PreviewModes;
        UiLangBox.ItemsSource = UiLangs;
        CardLangBox.ItemsSource = CardLangs;
        UiLangBox.DisplayMemberPath = CardLangBox.DisplayMemberPath = "Name";
        // the save bar follows the form: compare the current form with what was last loaded/saved
        _dirtyTimer.Tick += (_, _) => SaveBar.Visibility = Dirty ? Visibility.Visible : Visibility.Collapsed;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); SavedToast.Visibility = Visibility.Collapsed; };
    }

    /// <summary>Leaving with unsaved changes: ask, then continue to where the user was going.</summary>
    protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        _dirtyTimer.Stop();
        if (!Dirty) return;
        e.Cancel = true;
        var target = e.SourcePageType;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Z("保存设置？", "Save settings?"),
            Content = L.Z("设置有未保存的更改。", "Your settings have unsaved changes."),
            PrimaryButtonText = L.Z("保存", "Save"),
            SecondaryButtonText = L.Z("不保存", "Don't save"),
            CloseButtonText = L.Z("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        var r = await dlg.ShowAsync();
        if (r == ContentDialogResult.None)
        {
            _dirtyTimer.Start();
            App.Main.Navigate("settings");
            return;
        }
        if (r == ContentDialogResult.Primary && !await SaveAsync()) return;
        _savedPatch = Snapshot();       // clean now, so the next attempt goes through
        App.Main.NavigateTo(target);
    }

    static string Str(JsonNode? n, string fallback = "") => n?.GetValue<string>() ?? fallback;
    static double Num(JsonNode? n, double fallback) => n == null ? fallback : n.GetValue<double>();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        try
        {
            _s = await Api.Get<JsonObject>("/api/settings");
            if (Meta.Info.Sizes.Count == 0) await Meta.Load();
        }
        catch (Exception ex)
        {
            App.Main.ShowError(ex.Message);
            _loading = false;
            return;
        }
        var gen = _s["gen"]!.AsObject();

        UiLangBox.SelectedItem = UiLangs.FirstOrDefault(o => o.Id == Str(_s["ui_language"])) ?? UiLangs[0];
        CardLangBox.SelectedItem = CardLangs.FirstOrDefault(o => o.Id == Str(_s["card_language"], "zh")) ?? CardLangs[0];

        var provider = Str(_s["llm_provider"], "anthropic");
        var preset = Presets.FirstOrDefault(p => p.Id == Str(_s["llm_preset"]) && p.Provider == provider) ??
                     Presets.First(p => p.Provider == provider);
        PresetBox.SelectedItem = preset;
        ShowPreset(preset, fromSettings: true);
        EffortBox.SelectedItem = Str(_s["anthropic_effort"], "medium");

        SizeBox.ItemsSource = Meta.Info.Sizes;
        SizeBox.SelectedValue = Str(gen["size_preset"], "sts2_card");
        VariantsBox.Value = Num(gen["variants"], 2);
        CfgBox.Value = Num(gen["cfg"], 3);
        StepsBox.Value = Num(gen["steps"], 25);
        var loras = new[] { NoLora }.Concat(Meta.Info.Loras).ToList();
        LoraBox.ItemsSource = loras;
        var lora = Str(gen["lora"]);
        LoraBox.SelectedItem = lora.Length == 0 ? NoLora : loras.Contains(lora) ? lora : null;
        StrengthBox.Value = Num(gen["lora_strength"], 0.9);
        NegativeBox.Text = Str(gen["negative"]);
        SuffixBox.Text = Str(gen["style_suffix"]);

        ProfileBox.SelectedItem = Profiles.FirstOrDefault(p => p.Id == Str(_s["comfy_profile"], "auto")) ?? Profiles[0];
        PreviewBox.SelectedItem = PreviewModes.FirstOrDefault(p => p.Id == Str(_s["comfy_preview"], "latent2rgb")) ?? PreviewModes[0];
        CooldownBox.Value = Num(gen["cooldown"], 0);
        CoolTriggerBox.Value = Num(gen["cool_trigger"], 90);
        CoolTempBox.Value = Num(gen["cool_temp"], 70);
        CoolMaxBox.Value = Num(gen["cool_max"], 900);
        AutostartSwitch.IsOn = _s["comfy_autostart"]?.GetValue<bool>() ?? true;

        ComfyRootBox.Text = Str(_s["comfy_root"]);
        ExportDirBox.Text = Str(_s["export_dir"]);
        SystemPromptBox.Text = Str(_s["prompt_system"]);
        TemplateBox.Text = Str(_s["prompt_template"]);
        _loading = false;
        _savedPatch = Snapshot();
        SaveBar.Visibility = Visibility.Collapsed;
        _dirtyTimer.Start();
    }

    void ShowPreset(LlmPreset p, bool fromSettings)
    {
        bool claude = p.Provider == "anthropic", ai = p.Provider != "template";
        LlmFields.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        BaseUrlBox.Visibility = claude ? Visibility.Collapsed : Visibility.Visible;
        EffortBox.Visibility = claude ? Visibility.Visible : Visibility.Collapsed;
        KeyHint.Text = p.KeyHint;
        if (claude)
        {
            ModelBox.Text = fromSettings ? Str(_s["anthropic_model"], p.Model) : p.Model;
            KeyBox.Password = Str(_s["anthropic_api_key"]);
        }
        else if (ai)
        {
            bool same = fromSettings || Str(_s["llm_preset"]) == p.Id;
            BaseUrlBox.Text = same ? Str(_s["openai_base_url"], p.BaseUrl) : p.BaseUrl;
            ModelBox.Text = same ? Str(_s["openai_model"], p.Model) : p.Model;
            KeyBox.Password = same ? Str(_s["openai_api_key"]) : "";
        }
    }

    void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && PresetBox.SelectedItem is LlmPreset p) ShowPreset(p, fromSettings: false);
    }

    object BuildPatch()
    {
        var p = (LlmPreset)PresetBox.SelectedItem;
        var patch = new JsonObject
        {
            ["ui_language"] = ((Option)UiLangBox.SelectedItem).Id,
            ["card_language"] = ((Option)CardLangBox.SelectedItem).Id,
            ["llm_preset"] = p.Id,
            ["llm_provider"] = p.Provider,
            ["comfy_profile"] = ((Option)ProfileBox.SelectedItem).Id,
            ["comfy_preview"] = ((Option)PreviewBox.SelectedItem).Id,
            ["comfy_autostart"] = AutostartSwitch.IsOn,
            ["export_dir"] = ExportDirBox.Text.Trim(),
            ["prompt_system"] = SystemPromptBox.Text.Replace("\r\n", "\n").Replace("\r", "\n"),
            ["prompt_template"] = TemplateBox.Text.Trim(),
            ["gen"] = new JsonObject
            {
                ["size_preset"] = SizeBox.SelectedValue as string ?? "sts2_card",
                ["variants"] = (int)VariantsBox.Value,
                ["cfg"] = Math.Round(CfgBox.Value, 2),
                ["steps"] = (int)StepsBox.Value,
                ["lora"] = LoraBox.SelectedItem is string l && l != NoLora ? l : "",
                ["lora_strength"] = Math.Round(StrengthBox.Value, 3),
                ["negative"] = NegativeBox.Text.Trim(),
                ["style_suffix"] = SuffixBox.Text.Trim(),
                ["cooldown"] = (int)CooldownBox.Value,
                ["cool_trigger"] = (int)CoolTriggerBox.Value,
                ["cool_temp"] = (int)CoolTempBox.Value,
                ["cool_max"] = (int)CoolMaxBox.Value,
            },
        };
        if (p.Provider == "anthropic")
        {
            patch["anthropic_model"] = ModelBox.Text.Trim();
            patch["anthropic_api_key"] = KeyBox.Password.Trim();
            patch["anthropic_effort"] = EffortBox.SelectedItem as string ?? "medium";
        }
        else if (p.Provider == "openai")
        {
            patch["openai_base_url"] = BaseUrlBox.Text.Trim();
            patch["openai_model"] = ModelBox.Text.Trim();
            patch["openai_api_key"] = KeyBox.Password.Trim();
        }
        return patch;
    }

    async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    async Task<bool> SaveAsync()
    {
        try
        {
            var before = Str(_s["ui_language"]);
            _s = await Api.Put<JsonObject>("/api/settings", BuildPatch());
            _savedPatch = Snapshot();
            SaveBar.Visibility = Visibility.Collapsed;
            SavedText.Text = L.Z("设置已保存", "Settings saved") +
                             (Str(_s["ui_language"]) != before ? L.Z("（界面语言将在重启后切换）", " (UI language switches after a restart)") : "");
            SavedToast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
            return true;
        }
        catch (Exception ex)
        {
            App.Main.ShowError(ex.Message);
            return false;
        }
    }

    void Discard_Click(object sender, RoutedEventArgs e)
    {
        _savedPatch = "";
        OnNavigatedTo(null!);           // reload the saved values into the form
    }

    async void ResetSystemPrompt_Click(object sender, RoutedEventArgs e)
    {
        await Meta.Load();
        SystemPromptBox.Text = Meta.Info.DefaultPromptSystem;
    }

    async void ResetTemplate_Click(object sender, RoutedEventArgs e)
    {
        await Meta.Load();
        TemplateBox.Text = Meta.Info.DefaultPromptTemplate;
    }

    async void TestLlm_Click(object sender, RoutedEventArgs e)
    {
        TestRing.IsActive = true;
        TestResult.Text = "";
        try
        {
            var settings = (JsonObject)BuildPatch();
            settings.Remove("gen");
            var res = await Api.Post<PromptResult>("/api/prompt", new
            {
                name = L.Z("袖中刃", "Sleeve Blade"), cls = "silent", type = "attack", rarity = "common", cost = "1",
                description = L.Z("造成 6 点伤害。", "Deal 6 damage."), concept = "", _settings = settings,
            });
            TestResult.Text = "✓ " + res.Prompt + (res.Notes.Length > 0 ? "\n" + res.Notes : "");
        }
        catch (Exception ex) { TestResult.Text = "✗ " + ex.Message; }
        finally { TestRing.IsActive = false; }
    }

    async void RestartComfy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Api.Put<JsonObject>("/api/settings", BuildPatch());
            await Api.Post("/api/comfy/stop");
            await Api.Post("/api/comfy/start");
            App.Main.ShowInfo(L.Z("ComfyUI 正在以新设置重启", "ComfyUI is restarting with the new settings"));
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void ImportLora_Click(object sender, RoutedEventArgs e)
    {
        var src = await App.Main.PickFile(".safetensors");
        if (src == null) return;
        try
        {
            var dst = Path.Combine(AppPaths.Models, "loras", Path.GetFileName(src));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            await Task.Run(() => File.Copy(src, dst, true));
            await Meta.Load();
            var loras = new[] { NoLora }.Concat(Meta.Info.Loras).ToList();
            LoraBox.ItemsSource = loras;
            LoraBox.SelectedItem = loras.FirstOrDefault(l => l.EndsWith(Path.GetFileName(src))) ?? LoraBox.SelectedItem;
            App.Main.ShowInfo(L.Z("已导入 ", "Imported ") + Path.GetFileName(src));
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void PickExport_Click(object sender, RoutedEventArgs e)
    {
        var f = await App.Main.PickFolder();
        if (f != null) ExportDirBox.Text = f;
    }

    void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", AppPaths.Data);
}
