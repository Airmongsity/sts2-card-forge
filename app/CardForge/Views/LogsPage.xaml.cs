using System.Diagnostics;
using System.Text;
using CardForge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace CardForge.Views;

public sealed partial class LogsPage : Page
{
    readonly record struct Source(string Id, string Name);
    readonly Source[] _sources =
    [
        new("all", L.Z("全部日志", "All logs")),
        new("backend", L.Z("后端", "Backend")),
        new("jobs", L.Z("生成任务", "Generation jobs")),
        new("comfy", "ComfyUI"),
        new("setup", L.Z("安装", "Setup")),
        new("update", L.Z("更新", "Update")),
    ];
    bool _ready;

    public LogsPage()
    {
        InitializeComponent();
        LogSourceBox.ItemsSource = _sources;
        LogSourceBox.SelectedIndex = 0;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _ready = true;
        await Refresh();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Refresh();

    async void LogSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) await Refresh();
    }

    async Task Refresh()
    {
        LoadingRing.IsActive = true;
        LogStatus.Text = L.Z("读取中…", "Loading…");
        try
        {
            var selected = LogSourceBox.SelectedItem is Source s ? s.Id : "all";
            var sections = new List<(string Id, string Title, string Text)>
            {
                ("backend", L.Z("后端进程输出", "Backend process output"), BackendHost.LogText),
                ("jobs", L.Z("最近生成任务", "Recent generation jobs"), await JobLog()),
                ("comfy", "ComfyUI", await ApiText("/api/comfy/log?tail=800")),
                ("setup", L.Z("安装日志", "Setup log"), await TailFile(Path.Combine(AppPaths.Logs, "setup.log"))),
                ("update", L.Z("更新日志", "Update log"), await TailFile(Path.Combine(AppPaths.Logs, "update.log"))),
            };
            var shown = selected == "all" ? sections : sections.Where(x => x.Id == selected);
            LogBox.Text = string.Join("\r\n\r\n", shown.Select(x => $"===== {x.Title} =====\r\n{Empty(x.Text)}"));
            LogBox.SelectionStart = LogBox.Text.Length;
            LogStatus.Text = L.Z($"{LogBox.Text.Length:N0} 个字符", $"{LogBox.Text.Length:N0} characters");
        }
        catch (Exception ex)
        {
            LogBox.Text = ex.ToString();
            LogStatus.Text = L.Z("读取失败", "Failed to load");
        }
        finally { LoadingRing.IsActive = false; }
    }

    static string Empty(string? text) => string.IsNullOrWhiteSpace(text) ? L.Z("（暂无记录）", "(no entries)") : text.Trim();

    static async Task<string> ApiText(string path)
    {
        try { return await Api.GetText(path); }
        catch (Exception ex) { return L.Z("无法读取：", "Unavailable: ") + ex.Message; }
    }

    static async Task<string> JobLog()
    {
        try
        {
            var list = await Api.Get<JobList>("/api/jobs");
            return string.Join("\r\n", list.Jobs.Select(j =>
            {
                var provider = j.Params["image_provider"]?.ToString() ?? "comfy";
                var model = j.Params["image_api_model"]?.ToString();
                var when = j.Created > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(j.Created * 1000)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : "?";
                return $"{when}  #{j.Id}  {j.Status,-9}  {provider}" +
                       (string.IsNullOrWhiteSpace(model) ? "" : $"/{model}") +
                       $"  {j.CardName ?? "-"}\r\n    {j.Message}";
            }));
        }
        catch (Exception ex) { return L.Z("无法读取任务：", "Jobs unavailable: ") + ex.Message; }
    }

    static async Task<string> TailFile(string path)
    {
        if (!File.Exists(path)) return "";
        return await Task.Run(() =>
        {
            const int maxBytes = 256_000;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var count = (int)Math.Min(maxBytes, stream.Length);
            stream.Seek(-count, SeekOrigin.End);
            var bytes = new byte[count];
            _ = stream.Read(bytes, 0, count);
            var text = Encoding.UTF8.GetString(bytes);
            if (stream.Length > count && text.IndexOf('\n') is var cut && cut >= 0) text = text[(cut + 1)..];
            return text;
        });
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage();
        data.SetText(LogBox.Text);
        Clipboard.SetContent(data);
        LogStatus.Text = L.Z("已复制", "Copied");
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Logs);
        Process.Start("explorer.exe", AppPaths.Logs);
    }
}
