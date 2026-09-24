using System.Runtime.InteropServices.WindowsRuntime;
using CardForge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Streams;

namespace CardForge.Views;

public sealed partial class QueuePage : Page
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    string _lastSignature = "";

    public QueuePage()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await Refresh();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _lastSignature = "";
        _timer.Start();
        await Refresh();
        await LoadLog();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    async Task Refresh()
    {
        JobList list;
        try { list = await Api.Get<JobList>("/api/jobs"); }
        catch (Exception ex) { WorkerInfo.Text = ex.Message; return; }

        // rebuild the list only when something changed, so scrolling isn't reset every tick
        var sig = string.Join(";", list.Jobs.Select(j => $"{j.Id}:{j.Status}:{j.Progress:0.00}"));
        if (sig != _lastSignature)
        {
            _lastSignature = sig;
            JobList.ItemsSource = list.Jobs;
        }

        var w = list.Worker;
        PauseBtn.IsChecked = w.Paused;
        SkipCoolBtn.IsEnabled = w.Cooling;
        var st = App.Main.Status;
        WorkerInfo.Text = (w.Current != null ? L.Z($"正在生成 #{w.Current}：{w.Step}/{w.Steps} 步", $"Generating #{w.Current}: step {w.Step}/{w.Steps}")
                          : w.Cooling && w.CoolReason != "heat" ? L.Z($"间隔等待 {w.CooldownLeft}s", $"Waiting {w.CooldownLeft}s between images")
                          : w.Queued > 0 ? L.Z($"排队 {w.Queued} 张", $"{w.Queued} queued")
                          : L.Z("空闲", "Idle"));

        if (st != null)
            ComfyInfo.Text = $"{st.Comfy.State}  ·  {st.Comfy.Url}" +
                             (string.IsNullOrEmpty(st.Comfy.Profile) ? "" : L.Z("  ·  性能档位 ", "  ·  profile ") + st.Comfy.Profile) +
                             (string.IsNullOrEmpty(st.Comfy.Error) ? "" : "\n" + st.Comfy.Error);

        try { Scene.Update(await Api.Get<ThermalInfo>("/api/thermal")); } catch { }

        _runningJob = w.Current;
        UpdateAbortOverlay();

        var bytes = w.Current != null ? await Api.GetBytes("/api/preview") : null;
        if (bytes != null)
        {
            var bmp = new BitmapImage();
            using var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            ms.Seek(0);
            await bmp.SetSourceAsync(ms);
            Preview.Source = bmp;
            PreviewHint.Text = "";
        }
        else
        {
            var last = list.Jobs.FirstOrDefault(j => j.ImageId != null);
            Preview.Source = last?.Large;
            PreviewHint.Text = last == null ? L.Z("生成的图片会显示在这里", "Generated images show up here") : "";
        }
    }

    async Task LoadLog()
    {
        try
        {
            LogBox.Text = await Api.GetText("/api/comfy/log?tail=300");
            LogBox.SelectionStart = LogBox.Text.Length;
        }
        catch { LogBox.Text = BackendHost.LogText; }
    }

    async void Pause_Click(object sender, RoutedEventArgs e) =>
        await Try(() => Api.Post("/api/worker", new { paused = PauseBtn.IsChecked == true }));

    async void SkipCool_Click(object sender, RoutedEventArgs e) => await Try(() => Api.Post("/api/worker", new { skip_cooldown = true }));
    async void CancelAll_Click(object sender, RoutedEventArgs e) => await Try(() => Api.Post("/api/jobs/cancel-all"));
    async void Clear_Click(object sender, RoutedEventArgs e) => await Try(() => Api.Post("/api/jobs/clear"));
    async void StartComfy_Click(object sender, RoutedEventArgs e) => await Try(() => Api.Post("/api/comfy/start"));
    async void StopComfy_Click(object sender, RoutedEventArgs e) => await Try(() => Api.Post("/api/comfy/stop"));
    async void Log_Click(object sender, RoutedEventArgs e) => await LoadLog();

    // hovering the preview offers to stop the image being drawn
    int? _runningJob;
    bool _pointerOnPreview;

    void Preview_PointerEntered(object sender, PointerRoutedEventArgs e) { _pointerOnPreview = true; UpdateAbortOverlay(); }
    void Preview_PointerExited(object sender, PointerRoutedEventArgs e) { _pointerOnPreview = false; UpdateAbortOverlay(); }

    void UpdateAbortOverlay() =>
        AbortOverlay.Visibility = _pointerOnPreview && _runningJob != null ? Visibility.Visible : Visibility.Collapsed;

    async void Abort_Click(object sender, RoutedEventArgs e)
    {
        if (_runningJob is not int id) return;
        _runningJob = null;
        UpdateAbortOverlay();
        await Try(() => Api.Delete($"/api/jobs/{id}"));
    }

    async void CancelJob_Click(object sender, RoutedEventArgs e) =>
        await Try(() => Api.Delete($"/api/jobs/{(int)((Button)sender).Tag}"));

    async Task Try(Func<Task> action)
    {
        try
        {
            await action();
            _lastSignature = "";
            await Refresh();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }
}
