using System.Diagnostics;
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

    public SetupPage()
    {
        InitializeComponent();
        StepsList.ItemsSource = _steps;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
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

        FillQuants();
        ComfyPath.Text = AppPaths.ComfyRoot;
        _loading = false;
        await CheckAll();
    }

    void ShowWarnings()
    {
        var w = Gpu.Warnings(Gpu.Package);
        GpuWarn.Message = string.Join("\n", w);
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
        bool done = _steps.All(s => s.Optional || s.State == StepState.Ok);
        DoneBar.IsOpen = done;
        App.Main.MarkSetupDone(done);
    }

    async Task RunSteps(IEnumerable<SetupStep> steps)
    {
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
        if (_steps.All(s => s.Optional || s.State == StepState.Ok)) await App.Main.RestartBackend();
    }

    async void InstallAll_Click(object sender, RoutedEventArgs e) =>
        await RunSteps(_steps.Where(s => !s.Optional || IncludeLora.IsChecked == true));

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

    void OpenModels_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(AppPaths.Models)) Process.Start("explorer.exe", AppPaths.Models);
    }

    void GoCards_Click(object sender, RoutedEventArgs e) => App.Main.Navigate("cards");
}
