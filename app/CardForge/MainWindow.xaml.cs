using CardForge.Services;
using CardForge.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Storage.Pickers;

namespace CardForge;

public sealed partial class MainWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public StatusInfo? Status { get; private set; }
    public event Action<StatusInfo?>? StatusChanged;
    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public MainWindow()
    {
        InitializeComponent();
        Title = "STS2 Card Forge";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        FitToScreen(1480, 940);
        SystemBackdrop = Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()
            ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
        Setup.Ui = DispatcherQueue;
        Closed += (_, _) => Task.Run(BackendHost.Stop).Wait(4000);
        _timer.Tick += async (_, _) => await PollStatus();
        _ = Startup();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Size in DIPs, scaled by the monitor DPI and clamped to the work area; maximizes on small screens.</summary>
    void FitToScreen(int width, int height)
    {
        double scale = GetDpiForWindow(Hwnd) / 96.0;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        int w = (int)(width * scale), h = (int)(height * scale);
        if (w >= area.Width || h >= area.Height)
        {
            (AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Maximize();
            return;
        }
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    async Task Startup()
    {
        BackendText.Text = L.Z("后端启动中…", "Starting backend…");
        AppPaths.DetectComfyRoot();
        await Gpu.Detect();
        Gpu.Publish();
        var steps = Setup.CreateSteps();
        bool allOk = true;
        foreach (var s in steps.Where(s => !s.Optional)) allOk &= await s.Check();
        SetupBadge.Visibility = allOk ? Visibility.Collapsed : Visibility.Visible;

        if (await BackendHost.EnsureRunning())
        {
            try { await Meta.Load(); } catch (Exception e) { ShowError(e.Message); }
        }
        _timer.Start();
        await PollStatus();
        Navigate(allOk && Status != null ? "cards" : "setup");
        if (Updater.AutoCheck) await CheckForUpdates(manual: false);
    }

    // ---- updates ---------------------------------------------------------------------------------

    UpdateInfo? _update;

    /// <summary>Looks for a newer release. Quiet on startup (no errors, skipped versions stay hidden);
    /// a manual check reports every outcome.</summary>
    public async Task CheckForUpdates(bool manual)
    {
        try
        {
            var u = await Updater.Check();
            if (u == null)
            {
                if (manual) ShowInfo(L.Z($"已是最新版本（{Updater.Current}）", $"You are on the latest version ({Updater.Current})"));
                return;
            }
            if (!manual && Updater.Skipped == u.Tag) return;
            _update = u;
            UpdateBar.Severity = InfoBarSeverity.Informational;
            UpdateBar.Title = L.Z($"新版本 {u.Tag} 可用", $"Version {u.Tag} is available");
            UpdateBar.Message = L.Z($"当前 {Updater.Current}。", $"You have {Updater.Current}. ") +
                (Updater.CanInstall ? L.Z("更新只替换程序文件，你的卡牌、图片、设置与模型都会保留。", "Updating replaces only the program files; your cards, images, settings and models are kept.")
                                    : L.Z("这是源码目录，请用 git pull 或到发布页下载。", "This is a source checkout: git pull, or download from the releases page."));
            UpdateNowBtn.Content = Updater.CanInstall ? L.Z("立即更新", "Update now") : L.Z("打开发布页", "Open releases page");
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateButtons.Visibility = Visibility.Visible;
            UpdateBar.IsOpen = true;
        }
        catch (Exception e)
        {
            if (manual) ShowError(L.Z("检查更新失败：", "Update check failed: ") + e.Message);
        }
    }

    async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } u) return;
        if (!Updater.CanInstall)
        {
            OpenUrl(u.PageUrl);
            return;
        }
        if (Status?.Worker is { } w && (w.Current != null || w.Queued > 0))
        {
            ShowError(L.Z("生成队列还有任务，请等它们完成（或暂停并清空）后再更新。", "The queue still has jobs; let them finish (or pause and clear it) before updating."));
            return;
        }

        var progress = new SetupStep
        {
            Id = "update", Title = "", Description = "",
            Check = () => Task.FromResult(false), Install = (_, _) => Task.CompletedTask,
        };
        progress.PropertyChanged += (_, _) =>
        {
            UpdateBar.Message = progress.Detail;
            UpdateProgress.IsIndeterminate = progress.Indeterminate;
            UpdateProgress.Value = progress.Progress;
        };
        UpdateButtons.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = true;
        UpdateBar.IsClosable = false;
        try
        {
            var dir = await Task.Run(() => Updater.Download(u, progress, CancellationToken.None));
            UpdateBar.Message = L.Z("正在安装，应用将自动重启…", "Installing; the app restarts by itself…");
            Updater.Install(dir);
            Close();   // Closed stops the backend; the update script waits for this process to exit
        }
        catch (Exception ex)
        {
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.Message = L.Z("更新失败：", "Update failed: ") + ex.Message;
            UpdateButtons.Visibility = Visibility.Visible;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            UpdateBar.IsClosable = true;
        }
    }

    void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) OpenUrl(_update.PageUrl);
    }

    void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) AppPaths.SaveSettings(s => s["update_skip"] = _update.Tag);
        UpdateBar.IsOpen = false;
    }

    static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Called by the setup page once the runtime is in place.</summary>
    public async Task<bool> RestartBackend()
    {
        var ok = await BackendHost.EnsureRunning();
        if (ok) await Meta.Load();
        await PollStatus();
        return ok;
    }

    public void MarkSetupDone(bool done) => SetupBadge.Visibility = done ? Visibility.Collapsed : Visibility.Visible;

    public void Navigate(string tag)
    {
        var all = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>();
        Nav.SelectedItem = all.First(i => (string)i.Tag == tag);
    }

    static readonly Dictionary<Type, string> PageTags = new()
    {
        [typeof(CardsPage)] = "cards", [typeof(CharactersPage)] = "characters", [typeof(QueuePage)] = "queue",
        [typeof(GalleryPage)] = "gallery", [typeof(SetupPage)] = "setup", [typeof(SettingsPage)] = "settings",
    };

    public void NavigateTo(Type page)
    {
        if (PageTags.TryGetValue(page, out var tag))
        {
            Navigate(tag);
            if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
        }
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = ((args.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "cards" => typeof(CardsPage),
            "characters" => typeof(CharactersPage),
            "queue" => typeof(QueuePage),
            "gallery" => typeof(GalleryPage),
            "setup" => typeof(SetupPage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };
        if (page != null && ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }

    async Task PollStatus()
    {
        try
        {
            Status = await Api.Get<StatusInfo>("/api/status");
        }
        catch
        {
            Status = null;
        }
        UpdateStatusBar();
        StatusChanged?.Invoke(Status);
    }

    static SolidColorBrush Brush(byte r, byte g, byte b) => new(ColorHelper.FromArgb(255, r, g, b));

    void UpdateStatusBar()
    {
        if (Status == null)
        {
            BackendDot.Fill = Brush(230, 80, 70);
            BackendText.Text = L.Z("后端未运行", "Backend offline");
            ComfyText.Text = WorkerText.Text = "";
            WorkerBar.Visibility = Visibility.Collapsed;
            QueueBadge.Visibility = Visibility.Collapsed;
            return;
        }
        BackendDot.Fill = Brush(80, 190, 110);
        BackendText.Text = L.Z("后端 ", "Backend ") + Status.Version;

        var c = Status.Comfy;
        (ComfyDot.Fill, ComfyText.Text) = c.State switch
        {
            "ready" => (Brush(80, 190, 110), "ComfyUI " + L.Z("就绪", "ready")),
            "external" => (Brush(80, 190, 110), "ComfyUI " + L.Z("就绪（外部进程）", "ready (external)")),
            "starting" => (Brush(230, 170, 60), "ComfyUI " + L.Z("启动中…", "starting…")),
            "error" => (Brush(230, 80, 70), "ComfyUI: " + c.Error),
            _ => (Brush(128, 128, 128), "ComfyUI " + L.Z("未启动（生成时自动启动）", "stopped (starts on demand)")),
        };

        var w = Status.Worker;
        var parts = new List<string>();
        if (w.Current != null) parts.Add(L.Z("生成中 ", "Generating ") + $"{w.Step}/{w.Steps}");
        if (w.Queued > 0) parts.Add(L.Z($"排队 {w.Queued}", $"{w.Queued} queued"));
        if (w.Cooling && w.CoolReason != "heat") parts.Add(L.Z($"间隔等待 {w.CooldownLeft}s", $"waiting {w.CooldownLeft}s"));
        UpdateTempChip(Status.Gpu, w, Status.Thermal ?? new ThermalLimits());
        if (w.Paused) parts.Add(L.Z("已暂停", "paused"));
        WorkerText.Text = string.Join("  ·  ", parts);
        WorkerBar.Visibility = w.Current != null ? Visibility.Visible : Visibility.Collapsed;
        WorkerBar.Value = w.Steps > 0 ? 100.0 * w.Step / w.Steps : 0;

        int active = w.Queued + (w.Current != null ? 1 : 0);
        QueueBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueBadge.Value = active;
    }

    Storyboard? _chipPulse;

    /// <summary>Colour-coded temperature pill in the status bar (pulses while cooling) and the full-window
    /// fire / frost overlay.</summary>
    void UpdateTempChip(GpuStatus? gpu, WorkerStatus w, ThermalLimits limits)
    {
        TempChip.Visibility = gpu == null ? Visibility.Collapsed : Visibility.Visible;
        if (gpu == null)
        {
            Fx.SetMode("", 0);
            return;
        }
        bool cooling = w.Cooling && w.CoolReason == "heat";
        var c = Controls.ThermalScene.TempColor(gpu.Temp);
        TempDot.Fill = new SolidColorBrush(c);
        TempChip.Background = new SolidColorBrush(ColorHelper.FromArgb(cooling ? (byte)0x66 : (byte)0x26, c.R, c.G, c.B));
        TempChipText.Text = $"GPU {gpu.Temp}°C";
        TempFlake.Visibility = cooling ? Visibility.Visible : Visibility.Collapsed;

        _chipPulse ??= MakeAnimation(TempChip, "Opacity", 1, 0.45, 700, true);
        if (cooling) _chipPulse.Begin(); else { _chipPulse.Stop(); TempChip.Opacity = 1; }

        if (cooling)
        {
            double peak = Math.Max(w.CoolPeak ?? gpu.Temp, gpu.Temp);
            Fx.SetMode("frost", (float)Math.Clamp((peak - gpu.Temp) / Math.Max(1, peak - limits.Resume), 0, 1));
        }
        else if (limits.Trigger > 0 && gpu.Temp >= limits.Trigger)
            Fx.SetMode("fire", (float)Math.Clamp((gpu.Temp - limits.Trigger) / Math.Max(1, 100 - limits.Trigger), 0, 1));
        else
            Fx.SetMode("", 0);
    }


    static Storyboard MakeAnimation(DependencyObject target, string property, double from, double to, int ms, bool reverse)
    {
        var anim = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EnableDependentAnimation = true };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = reverse };
        sb.Children.Add(anim);
        return sb;
    }

    public void ShowError(string message) => ShowBar(message, InfoBarSeverity.Error, L.Z("出错了", "Error"));
    public void ShowInfo(string message) => ShowBar(message, InfoBarSeverity.Success, "");

    void ShowBar(string message, InfoBarSeverity severity, string title)
    {
        ErrorBar.Severity = severity;
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    // ---- pickers (unpackaged apps must associate them with the window handle) -------------------

    public async Task<string?> PickFile(params string[] extensions)
    {
        var p = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var e in extensions) p.FileTypeFilter.Add(e);
        WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd);
        return (await p.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickFolder()
    {
        var p = new FolderPicker();
        p.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd);
        return (await p.PickSingleFolderAsync())?.Path;
    }

    public async Task<string?> PickSaveFile(string suggestedName, string label, string extension)
    {
        var p = new FileSavePicker { SuggestedFileName = suggestedName };
        p.FileTypeChoices.Add(label, [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd);
        return (await p.PickSaveFileAsync())?.Path;
    }
}
