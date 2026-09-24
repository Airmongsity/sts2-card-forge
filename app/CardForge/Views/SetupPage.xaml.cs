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
        GpuText.Text = Setup.VramMiB is int mib
            ? $"{Setup.GpuName} · {mib / 1024.0:0.#} GB VRAM" + (mib < 7000 ? L.Z("（显存偏小，出图会更慢）", " (low VRAM: generation will be slower)") : "")
            : L.Z("未检测到 NVIDIA 显卡（nvidia-smi 不可用）。本工具需要 NVIDIA 显卡。", "No NVIDIA GPU detected (nvidia-smi unavailable). An NVIDIA GPU is required.");

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
        MirrorBox.SelectedItem = MirrorBox.Items.OfType<string>().FirstOrDefault(m => m == Setup.HfEndpoint) ?? MirrorBox.Items[0];
        ComfyPath.Text = AppPaths.ComfyRoot;
        _loading = false;
        await CheckAll();
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

    void MirrorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MirrorBox.SelectedItem is not string m) return;
        AppPaths.SaveSettings(s => s["hf_endpoint"] = m);
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
