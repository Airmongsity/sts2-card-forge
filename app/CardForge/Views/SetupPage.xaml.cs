using System.Diagnostics;
using System.Text.Json.Nodes;
using CardForge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace CardForge.Views;

public sealed partial class SetupPage : Page
{
    readonly List<SetupStep> _steps = Setup.CreateSteps();
    CancellationTokenSource? _cts;
    bool _loading;
    bool _serviceLoading;
    JsonObject _serviceSettings = new();
    IReadOnlyList<LlmPreset> PromptPresets => SettingsPage.Presets;
    IReadOnlyList<ImagePreset> ImagePresets => SettingsPage.ImagePresets;

    public SetupPage()
    {
        InitializeComponent();
        StepsList.ItemsSource = _steps;
        PresetBox.ItemsSource = PromptPresets;
        ImagePresetBox.ItemsSource = ImagePresets;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        _serviceLoading = true;
        try
        {
            _serviceSettings = await Api.Get<JsonObject>("/api/settings");
            var provider = Str(_serviceSettings["llm_provider"], "anthropic");
            var promptPreset = PromptPresets.FirstOrDefault(p => p.Id == Str(_serviceSettings["llm_preset"]) && p.Provider == provider)
                               ?? PromptPresets.First(p => p.Provider == provider);
            PresetBox.SelectedItem = promptPreset;
            ShowPromptPreset(promptPreset, fromSettings: true);
            EffortBox.SelectedItem = Str(_serviceSettings["anthropic_effort"], "medium");
            SystemPromptBox.Text = Str(_serviceSettings["prompt_system"]);
            TemplateBox.Text = Str(_serviceSettings["prompt_template"]);

            var imageProvider = Str(_serviceSettings["image_provider"], "comfy");
            var savedImagePreset = Str(_serviceSettings["image_preset"]);
            var imagePreset = ImagePresets.FirstOrDefault(p => p.Id == savedImagePreset && p.Provider == imageProvider)
                              ?? (savedImagePreset == "openai" && imageProvider == "openai" ? ImagePresets.First(p => p.Id == "custom") : null)
                              ?? ImagePresets.FirstOrDefault(p => p.Provider == imageProvider) ?? ImagePresets[0];
            ImagePresetBox.SelectedItem = imagePreset;
            ShowImagePreset(imagePreset, fromSettings: true);
            ShowMode();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
        finally { _serviceLoading = false; }
        GpuText.Text = Gpu.Summary();

        PackageBox.Items.Clear();
        var auto = Gpu.AutoPackage;
        var overridden = AppPaths.LoadSettings()["gpu_package_override"]?.GetValue<string>();
        PackageBox.Items.Add(new ComboBoxItem { Tag = "", Content = L.Z("自动：", "Auto: ") + Gpu.PackageLabel(auto) });
        foreach (var p in Gpu.Packages)
            PackageBox.Items.Add(new ComboBoxItem { Tag = p, Content = Gpu.PackageLabel(p) });
        PackageBox.SelectedItem = PackageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == overridden) ?? PackageBox.Items[0];
        ShowWarnings();

        SourceBox.Items.Clear();
        foreach (var (id, label) in new[]
                 {
                     ("auto", L.Z("自动（按系统地区）", "Auto (by system region)")),
                     ("global", L.Z("国际：HuggingFace / GitHub / PyPI 优先", "Global: HuggingFace / GitHub / PyPI first")),
                     ("china", L.Z("中国大陆：ModelScope 魔搭 / 清华 PyPI 镜像优先", "Mainland China: ModelScope / Tsinghua PyPI mirror first")),
                 })
            SourceBox.Items.Add(new ComboBoxItem { Tag = id, Content = label });
        SourceBox.SelectedItem = SourceBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == Setup.Source);
        GithubProxyBox.Text = Setup.GithubProxy;

        NetProxyBox.Items.Clear();
        foreach (var (id, label) in new[]
                 {
                     ("auto", L.Z("自动（系统代理或本机代理端口）", "Auto (system proxy or a local proxy port)")),
                     ("off", L.Z("不使用", "Off")),
                     ("manual", L.Z("手动填写", "Manual")),
                 })
            NetProxyBox.Items.Add(new ComboBoxItem { Tag = id, Content = label });
        var np = NetProxy.Setting;
        var npMode = np is "auto" or "off" ? np : "manual";
        NetProxyBox.SelectedItem = NetProxyBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == npMode);
        NetProxyUrl.Text = npMode == "manual" ? np : "";
        NetProxyUrl.Visibility = npMode == "manual" ? Visibility.Visible : Visibility.Collapsed;
        _ = ShowProxy(false);

        FillQuants();
        ComfyPath.Text = AppPaths.ComfyRoot;
        _loading = false;
        await CheckAll();
    }

    void ShowWarnings()
    {
        var w = Gpu.Warnings(Gpu.Package);
        GpuWarn.Severity = w.Any(x => x.Severe) ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        GpuWarn.Message = string.Join("\n", w.Select(x => x.Text));
        GpuWarn.IsOpen = w.Count > 0;
    }

    void FillQuants()
    {
        QuantBox.Items.Clear();
        var rec = Setup.RecommendedQuant;
        foreach (var (_, f) in Setup.Quants)
            QuantBox.Items.Add(new ComboBoxItem
            {
                Tag = f,
                Content = $"{f.Name.Replace("qwen_image_2.1_", "").Replace(".gguf", "")}  ·  {f.Size / 1e9:0.0} GB" +
                          (f == rec ? L.Z("  ·  推荐", "  ·  recommended") : "") + (f.Present ? L.Z("  ·  已下载", "  ·  downloaded") : ""),
            });
        var current = Setup.CurrentQuant;
        QuantBox.SelectedItem = QuantBox.Items.OfType<ComboBoxItem>().First(i => current.Equals(i.Tag));
    }

    async Task CheckAll()
    {
        var unet = _steps.First(s => s.Id == "unet");
        var q = Setup.CurrentQuant;
        unet.Detail = "";
        unet.Description = $"{q.Name} · {q.Size / 1e9:0.0} GB";
        foreach (var s in _steps)
        {
            if (s.State == StepState.Working) continue;
            try
            {
                s.State = await s.Check() ? StepState.Ok : StepState.Missing;
                if (s.Id == "runtime" && s.State == StepState.Missing && !Setup.RuntimeOk(out var problem)) s.Detail = problem;
                else if (s.State == StepState.Ok) s.Detail = "";
            }
            catch (Exception ex) { s.State = StepState.Failed; s.Detail = ex.Message; }
        }
        StepsList.ItemsSource = null;
        StepsList.ItemsSource = _steps;
        bool cloud = (AppPaths.LoadSettings()["image_provider"]?.GetValue<string>() ?? "comfy") != "comfy";
        bool done = _steps.Where(s => Setup.Required(s, cloud)).All(s => s.State == StepState.Ok);
        DoneBar.Title = cloud ? L.Z("云端模式已就绪", "Cloud mode is ready") : L.Z("环境就绪", "All set");
        DoneBar.Message = cloud
            ? L.Z("无需下载图像模型权重；在“图像生成”中配置 API 即可。", "No image model weights are required. Configure the API under Image generation.")
            : L.Z("所有组件已安装。前往“卡牌”页开始创作。", "Everything is installed. Head to Cards to start.");
        DoneBar.IsOpen = done;
        App.Main.MarkSetupDone(done);
    }

    /// <summary>Before any big download: when this machine is unlikely to generate images (no suitable GPU, old driver,
    /// too little RAM) or the disk is too small, say so and let the user decide, so nobody downloads ~14 GB for nothing.</summary>
    async Task<bool> ConfirmMachine(List<SetupStep> steps)
    {
        var todo = new List<SetupStep>();
        foreach (var s in steps)
            if (s.Id != "deps" && s.Id != "gputest" && !await s.Check()) todo.Add(s);
        if (todo.Count == 0) return true;

        long download = 0, disk = 0;
        foreach (var s in todo)
        {
            var (d, k) = s.Id switch
            {
                "runtime" => (2_000_000_000L, 9_000_000_000L),   // ~2 GB archive, ~7 GB unpacked
                "gguf" => (1_000_000L, 1_000_000L),
                "unet" => (Setup.CurrentQuant.Size, Setup.CurrentQuant.Size),
                "te" => (Setup.TextEncoder.Size, Setup.TextEncoder.Size),
                "vae" => (Setup.Vae.Size, Setup.Vae.Size),
                "lora" => (Setup.Lora.Size, Setup.Lora.Size),
                _ => (0L, 0L),
            };
            download += d;
            disk += k;
        }

        bool cloud = (AppPaths.LoadSettings()["image_provider"]?.GetValue<string>() ?? "comfy") != "comfy";
        List<string> problems = cloud ? [] : Gpu.Warnings(Gpu.Package).Where(w => w.Severe).Select(w => w.Text).ToList();
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(AppPaths.Root))!).AvailableFreeSpace;
            if (free < disk + 1_000_000_000L)
                problems.Add(L.Z($"磁盘空间不足：需要约 {disk / 1e9:0} GB，所在磁盘只剩 {free / 1e9:0.#} GB。",
                                 $"Not enough disk space: about {disk / 1e9:0} GB needed, {free / 1e9:0.#} GB free on this drive."));
        }
        catch { }
        if (problems.Count == 0 || download < 50_000_000) return true;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Z("这台电脑可能无法正常出图", "This PC may not be able to generate images"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = string.Join("\n", problems.Select(p => "• " + p)) + "\n\n" +
                       L.Z($"接下来要下载约 {download / 1e9:0.#} GB。建议先解决上面的问题（或在“运行包”中改选）。确定仍要下载吗？",
                           $"About {download / 1e9:0.#} GB would be downloaded next. Fix the issues above first (or pick another package). Download anyway?"),
            },
            PrimaryButtonText = L.Z("仍然下载", "Download anyway"),
            CloseButtonText = L.Z("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    async Task RunSteps(IEnumerable<SetupStep> stepsToRun)
    {
        var steps = stepsToRun.ToList();
        if (!await ConfirmMachine(steps)) return;
        var (proxy, _) = await NetProxy.Resolve();
        Setup.Log($"==== install {string.Join(",", steps.Select(x => x.Id))} | app {Updater.Current} | {Environment.OSVersion} | " +
                  $"{Gpu.Summary()} | package {Gpu.Package} | source {Setup.Source} (china={Setup.China}) | " +
                  $"github proxy '{Setup.GithubProxy}' | net proxy {NetProxy.Setting} -> {proxy?.ToString() ?? "direct"} | root {AppPaths.Root}");
        _cts = new CancellationTokenSource();
        InstallAll.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        try
        {
            foreach (var step in steps)
            {
                if (await step.Check()) { step.State = StepState.Ok; continue; }
                step.State = StepState.Working;
                try
                {
                    await Task.Run(() => step.Install(step, _cts.Token));
                    step.State = await step.Check() ? StepState.Ok : StepState.Failed;
                    step.Detail = "";
                    if (step.Id is "runtime" or "deps") await App.Main.RestartBackend();
                }
                catch (OperationCanceledException)
                {
                    step.State = StepState.Missing;
                    step.Detail = L.Z("已取消（再次安装将断点续传）", "Cancelled (installing again resumes)");
                    break;
                }
                catch (Exception ex)
                {
                    Setup.Log($"step {step.Id} failed: {ex}");
                    step.State = StepState.Failed;
                    step.Detail = ex.Message;
                    if (step.Id == "runtime") break;   // everything else needs the runtime
                }
            }
        }
        finally
        {
            InstallAll.IsEnabled = true;
            CancelBtn.IsEnabled = false;
            _cts = null;
        }
        await CheckAll();
        bool cloud = (AppPaths.LoadSettings()["image_provider"]?.GetValue<string>() ?? "comfy") != "comfy";
        if (_steps.Where(s => Setup.Required(s, cloud)).All(s => s.State == StepState.Ok))
            await App.Main.RestartBackend();
    }

    static string Str(JsonNode? node, string fallback = "") => node?.GetValue<string>() ?? fallback;

    void Sections_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var item = sender.SelectedItem;
        ImageSection.Visibility = item == ImageSectionItem ? Visibility.Visible : Visibility.Collapsed;
        PromptSection.Visibility = item == PromptSectionItem ? Visibility.Visible : Visibility.Collapsed;
        LocalSection.Visibility = item == LocalSectionItem ? Visibility.Visible : Visibility.Collapsed;
        SectionScroll.ChangeView(null, 0, null, true);
    }

    /// <summary>The saved image mode: shown in the header, and the active mode's button is highlighted.</summary>
    void ShowMode()
    {
        bool cloud = Str(_serviceSettings["image_provider"], "comfy") != "comfy";
        var preset = ImagePresets.FirstOrDefault(p => p.Id == Str(_serviceSettings["image_preset"]));
        ModeText.Text = cloud
            ? L.Z("当前出图方式：云端 API", "Current image mode: cloud API") + (preset != null ? $" · {preset.Name}" : "")
            : L.Z("当前出图方式：本地 ComfyUI", "Current image mode: local ComfyUI");
        var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        LocalModeBtn.Style = cloud ? null : accent;
        CloudModeBtn.Style = cloud ? accent : null;
    }

    void ShowPromptPreset(LlmPreset preset, bool fromSettings)
    {
        bool claude = preset.Provider == "anthropic", enabled = preset.Provider != "template";
        LlmFields.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        BaseUrlBox.Visibility = Visibility.Visible;
        EffortBox.Visibility = Visibility.Collapsed;
        KeyHint.Text = preset.KeyHint;
        if (claude)
        {
            bool same = fromSettings || Str(_serviceSettings["llm_preset"]) == preset.Id;
            BaseUrlBox.Text = same ? Str(_serviceSettings["anthropic_base_url"], preset.BaseUrl) : preset.BaseUrl;
            ModelBox.Text = fromSettings ? Str(_serviceSettings["anthropic_model"], preset.Model) : preset.Model;
            KeyBox.Password = Str(_serviceSettings["anthropic_api_key"]);
        }
        else if (enabled)
        {
            bool same = fromSettings || Str(_serviceSettings["llm_preset"]) == preset.Id;
            BaseUrlBox.Text = same ? Str(_serviceSettings["openai_base_url"], preset.BaseUrl) : preset.BaseUrl;
            ModelBox.Text = same ? Str(_serviceSettings["openai_model"], preset.Model) : preset.Model;
            KeyBox.Password = same ? Str(_serviceSettings["openai_api_key"]) : "";
        }
    }

    void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_serviceLoading && PresetBox.SelectedItem is LlmPreset preset) ShowPromptPreset(preset, fromSettings: false);
    }

    void ShowImagePreset(ImagePreset preset, bool fromSettings)
    {
        bool cloud = preset.Provider != "comfy";
        ImageApiFields.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
        ImageKeyHint.Text = preset.KeyHint;
        if (!cloud) return;
        bool same = fromSettings || Str(_serviceSettings["image_preset"]) == preset.Id;
        var endpoint = same ? Str(_serviceSettings["image_api_base_url"], preset.BaseUrl) : preset.BaseUrl;
        if (preset.Id == "siliconflow") endpoint = endpoint.Replace("https://api.siliconflow.com", "https://api.siliconflow.cn", StringComparison.OrdinalIgnoreCase);
        ImageEndpointBox.Text = endpoint;
        ImageModelBox.Text = same ? Str(_serviceSettings["image_api_model"], preset.Model) : preset.Model;
        ImageKeyBox.Password = same ? Str(_serviceSettings["image_api_key"]) : "";
        CloudConcurrencyBox.Value = same ? _serviceSettings["cloud_concurrency"]?.GetValue<int>() ?? 3 : 3;
        ImageExtraBox.Text = same ? Str(_serviceSettings["image_api_extra"], preset.Extra) : preset.Extra;
        var quality = same ? Str(_serviceSettings["image_api_quality"], "auto") : "auto";
        ImageQualityBox.SelectedItem = new[] { "auto", "low", "medium", "high" }.Contains(quality) ? quality : "auto";
    }

    void ImagePresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_serviceLoading && ImagePresetBox.SelectedItem is ImagePreset preset) ShowImagePreset(preset, fromSettings: false);
    }

    JsonObject BuildServicePatch()
    {
        var prompt = (LlmPreset)PresetBox.SelectedItem;
        var image = (ImagePreset)ImagePresetBox.SelectedItem;
        var patch = new JsonObject
        {
            ["llm_preset"] = prompt.Id,
            ["llm_provider"] = prompt.Provider,
            ["image_preset"] = image.Id,
            ["image_provider"] = image.Provider,
            ["prompt_system"] = SystemPromptBox.Text.Replace("\r\n", "\n").Replace("\r", "\n"),
            ["prompt_template"] = TemplateBox.Text.Trim(),
        };
        if (image.Provider != "comfy")
        {
            patch["image_api_base_url"] = ImageEndpointBox.Text.Trim();
            patch["image_api_model"] = ImageModelBox.Text.Trim();
            patch["image_api_key"] = ImageKeyBox.Password.Trim();
            patch["image_api_quality"] = ImageQualityBox.SelectedItem as string ?? "auto";
            patch["cloud_concurrency"] = (int)CloudConcurrencyBox.Value;
            patch["image_api_extra"] = string.IsNullOrWhiteSpace(ImageExtraBox.Text) ? "{}" : ImageExtraBox.Text.Trim();
        }
        if (prompt.Provider == "anthropic")
        {
            patch["anthropic_base_url"] = BaseUrlBox.Text.Trim();
            patch["anthropic_model"] = ModelBox.Text.Trim();
            patch["anthropic_api_key"] = KeyBox.Password.Trim();
            patch["anthropic_effort"] = EffortBox.SelectedItem as string ?? "medium";
        }
        else if (prompt.Provider == "openai")
        {
            patch["openai_base_url"] = BaseUrlBox.Text.Trim();
            patch["openai_model"] = ModelBox.Text.Trim();
            patch["openai_api_key"] = KeyBox.Password.Trim();
        }
        return patch;
    }

    async Task<bool> SaveServiceConfigAsync()
    {
        try
        {
            if (ImagePresetBox.SelectedItem is ImagePreset { Provider: not "comfy" } &&
                JsonNode.Parse(string.IsNullOrWhiteSpace(ImageExtraBox.Text) ? "{}" : ImageExtraBox.Text) is not JsonObject)
                throw new FormatException(L.Z("图像 API 的额外参数必须是 JSON 对象。", "Image API extra parameters must be a JSON object."));
            var previousImageProvider = Str(_serviceSettings["image_provider"], "comfy");
            var patch = BuildServicePatch();
            try { _serviceSettings = await Api.Put<JsonObject>("/api/settings", patch); }
            catch
            {
                // The app can be in first-run setup before the local backend is available.
                AppPaths.SaveSettings(settings =>
                {
                    foreach (var pair in patch) settings[pair.Key] = pair.Value?.DeepClone();
                });
                _serviceSettings = AppPaths.LoadSettings();
            }
            App.Main.ShowInfo(L.Z("配置已保存。", "Configuration saved."));
            ShowMode();
            if (previousImageProvider != Str(_serviceSettings["image_provider"], "comfy")) await CheckAll();
            return true;
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); return false; }
    }

    async void SaveServiceConfig_Click(object sender, RoutedEventArgs e) => await SaveServiceConfigAsync();

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
            var result = await Api.Post<PromptResult>("/api/prompt", new
            {
                name = L.Z("袖中刃", "Sleeve Blade"), cls = "silent", type = "attack", rarity = "common", cost = "1",
                description = L.Z("造成 6 点伤害。", "Deal 6 damage."), concept = "", _settings = BuildServicePatch(),
            });
            TestResult.Text = "✓ " + result.Prompt + (result.Notes.Length > 0 ? "\n" + result.Notes : "");
        }
        catch (Exception ex) { TestResult.Text = "✗ " + ex.Message; }
        finally { TestRing.IsActive = false; }
    }

    async void UseLocalMode_Click(object sender, RoutedEventArgs e)
    {
        Sections.SelectedItem = LocalSectionItem;   // where the model files are installed
        ImagePresetBox.SelectedItem = ImagePresets.First(p => p.Provider == "comfy");
        if (!await SaveServiceConfigAsync()) return;
        App.Main.ShowInfo(L.Z("已切换到本地模式；需要模型文件时可在此页安装。", "Switched to local mode. Install the model files here if needed."));
    }

    async void UseCloud_Click(object sender, RoutedEventArgs e)
    {
        Sections.SelectedItem = ImageSectionItem;   // where the API key is entered
        if (ImagePresetBox.SelectedItem is not ImagePreset selected || selected.Provider == "comfy")
        {
            var preset = ImagePresets.First(p => p.Id == "siliconflow");
            ImagePresetBox.SelectedItem = preset;
            ShowImagePreset(preset, fromSettings: false);
        }
        if (!await SaveServiceConfigAsync()) return;
        var needed = _steps.Where(s => Setup.Required(s, cloud: true)).ToList();
        if (needed.All(s => s.State == StepState.Ok))
        {
            // a release has nothing to install for cloud mode; just make sure the backend is up
            await App.Main.RestartBackend();
            App.Main.ShowInfo(L.Z("已切换到云端模式，无需下载。", "Switched to cloud mode; nothing to download."));
            return;
        }
        await RunSteps(needed);
    }

    async void InstallAll_Click(object sender, RoutedEventArgs e)
    {
        ImagePresetBox.SelectedItem = ImagePresets.First(p => p.Provider == "comfy");
        if (!await SaveServiceConfigAsync()) return;
        await RunSteps(_steps.Where(s => !s.Optional || IncludeLora.IsChecked == true));
    }

    async void InstallOne_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        if (_cts != null) return;
        await RunSteps(_steps.Where(s => s.Id == id));
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    async void Recheck_Click(object sender, RoutedEventArgs e) => await CheckAll();

    async void QuantBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QuantBox.SelectedItem is not ComboBoxItem { Tag: ModelFile f }) return;
        Setup.ChooseQuant(f);
        try { await Api.Put<object>("/api/settings", new { unet_file = f.Name }); } catch { }
        await CheckAll();
    }

    async void PackageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PackageBox.SelectedItem is not ComboBoxItem { Tag: string p }) return;
        AppPaths.SaveSettings(s =>
        {
            if (p.Length == 0) s.Remove("gpu_package_override");
            else s["gpu_package_override"] = p;
        });
        Gpu.Publish();
        ShowWarnings();
        _loading = true;
        FillQuants();
        _loading = false;
        await CheckAll();
    }

    void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        AppPaths.SaveSettings(s => s["download_source"] = id);
    }

    async Task ShowProxy(bool refresh)
    {
        NetProxyStatus.Text = L.Z("检测中…", "Detecting…");
        var (_, label) = await NetProxy.Resolve(refresh);
        NetProxyStatus.Text = label + L.Z("。用于下载、pip 与 AI 提示词（改动后重启应用生效于 AI 提示词）。",
                                          ". Used for downloads, pip and AI prompts (AI prompts pick up a change after restarting the app).");
    }

    void NetProxyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || NetProxyBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        NetProxyUrl.Visibility = id == "manual" ? Visibility.Visible : Visibility.Collapsed;
        if (id == "manual" && NetProxyUrl.Text.Trim().Length == 0) { NetProxyUrl.Focus(FocusState.Programmatic); return; }
        AppPaths.SaveSettings(s => s["net_proxy"] = id == "manual" ? NetProxyUrl.Text.Trim() : id);
        _ = ShowProxy(true);
    }

    void NetProxyUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = NetProxyUrl.Text.Trim();
        if (v.Length == 0) return;
        if (!Uri.TryCreate(v.Contains("://") ? v : "http://" + v, UriKind.Absolute, out _))
        {
            App.Main.ShowError(L.Z("代理地址应形如 http://127.0.0.1:7890", "The proxy address should look like http://127.0.0.1:7890"));
            return;
        }
        AppPaths.SaveSettings(s => s["net_proxy"] = v);
        _ = ShowProxy(true);
    }

    void NetProxyDetect_Click(object sender, RoutedEventArgs e) => _ = ShowProxy(true);

    void GithubProxyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = GithubProxyBox.Text.Trim();
        if (v.Length > 0 && !Uri.TryCreate(v, UriKind.Absolute, out _))
        {
            App.Main.ShowError(L.Z("GitHub 代理应是完整网址，如 https://ghfast.top/", "The GitHub proxy must be a full URL, e.g. https://ghfast.top/"));
            return;
        }
        AppPaths.SaveSettings(s => s["github_proxy"] = v);
    }

    async void PickComfy_Click(object sender, RoutedEventArgs e)
    {
        var folder = await App.Main.PickFolder();
        if (folder == null) return;
        // accept either the portable package folder or its ComfyUI subfolder
        if (!File.Exists(Path.Combine(folder, "python_embeded", "python.exe")) && File.Exists(Path.Combine(folder, "..", "python_embeded", "python.exe")))
            folder = Path.GetFullPath(Path.Combine(folder, ".."));
        if (!File.Exists(Path.Combine(folder, "python_embeded", "python.exe")))
        {
            App.Main.ShowError(L.Z("请选择 ComfyUI 便携版文件夹（包含 python_embeded 的那一层）", "Pick the ComfyUI portable folder (the one containing python_embeded)"));
            return;
        }
        AppPaths.ComfyRoot = folder;
        ComfyPath.Text = folder;
        await CheckAll();
        await App.Main.RestartBackend();
    }

    void CopyLinks_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(Setup.ManualLinks());
        Clipboard.SetContent(dp);
        App.Main.ShowInfo(L.Z("已复制下载链接与目标路径。用下载工具下载后放到对应位置，再点“重新检查”。",
                              "Links and target paths copied. Download with any manager, place the files, then click Re-check."));
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Setup.LogFile)) Process.Start("explorer.exe", $"/select,\"{Setup.LogFile}\"");
        else App.Main.ShowInfo(L.Z("还没有安装日志", "No install log yet"));
    }

    void OpenModels_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(AppPaths.Models)) Process.Start("explorer.exe", AppPaths.Models);
    }

    void GoCards_Click(object sender, RoutedEventArgs e) => App.Main.Navigate("cards");
}
