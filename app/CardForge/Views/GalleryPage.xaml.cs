using System.Collections.ObjectModel;
using System.Diagnostics;
using CardForge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace CardForge.Views;

public sealed partial class GalleryPage : Page
{
    readonly ObservableCollection<ImageRec> _items = new();
    static string AllProjects => L.Z("全部项目", "All projects");
    bool _loading;

    public GalleryPage()
    {
        InitializeComponent();
        Grid.ItemsSource = _items;
    }

    ImageRec? Selected => Grid.SelectedItem as ImageRec;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        try
        {
            var meta = await Api.Get<MetaInfo>("/api/meta");
            var current = ProjectFilter.SelectedItem as string ?? AllProjects;
            ProjectFilter.ItemsSource = new[] { AllProjects }.Concat(meta.Projects).ToList();
            ProjectFilter.SelectedItem = current;
            if (ProjectFilter.SelectedItem == null) ProjectFilter.SelectedIndex = 0;
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
        _loading = false;
        await Load();
    }

    async Task Load()
    {
        var project = ProjectFilter.SelectedItem as string;
        var q = "/api/images?limit=1000";
        if (project != null && project != AllProjects) q += "&project=" + Uri.EscapeDataString(project);
        if (FavFilter.IsChecked == true) q += "&favorites=1";
        try
        {
            var list = await Api.Get<List<ImageRec>>(q);
            var keep = Selected?.Id;
            Sync(list);
            CountText.Text = L.Z($"{list.Count} 张", $"{list.Count} images");
            // never leave the detail column empty: keep the selection, else show the newest image
            Grid.SelectedItem = _items.FirstOrDefault(i => i.Id == keep) ?? _items.FirstOrDefault();
            ShowDetail();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    // Keeps existing ImageRec instances (and their already-decoded thumbnails) for ids that are still present,
    // instead of clearing and re-adding every item, which forced every thumbnail to re-decode on each refresh.
    void Sync(List<ImageRec> list)
    {
        var ids = list.Select(i => i.Id).ToHashSet();
        foreach (var stale in _items.Where(i => !ids.Contains(i.Id)).ToList()) _items.Remove(stale);
        var byId = _items.ToDictionary(i => i.Id);
        for (var pos = 0; pos < list.Count; pos++)
        {
            var incoming = list[pos];
            if (!byId.ContainsKey(incoming.Id))
            {
                _items.Insert(Math.Min(pos, _items.Count), incoming);
                byId[incoming.Id] = incoming;
            }
            else
            {
                var existing = byId[incoming.Id];
                var from = _items.IndexOf(existing);
                if (from != pos) _items.Move(from, pos);
                existing.Favorite = incoming.Favorite;
            }
        }
    }

    async void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) await Load(); }
    async void Filter_Click(object sender, RoutedEventArgs e) => await Load();
    async void Refresh_Click(object sender, RoutedEventArgs e) => await Load();

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

    void ShowDetail()
    {
        var img = Selected;
        Detail.Visibility = img == null ? Visibility.Collapsed : Visibility.Visible;
        DetailColumn.Width = img == null ? new GridLength(0) : new GridLength(440);
        if (img == null) { Big.Path = ""; return; }
        Big.Path = img.Path;
        DetailTitle.Text = img.CardName ?? L.Z("(无卡牌)", "(no card)");
        var p = img.Params;
        DetailMeta.Text = $"{img.Width}×{img.Height} · seed {img.Seed} · cfg {p["cfg"]} · {p["steps"]} steps\n" +
                          $"LoRA {p["lora"]} @ {p["lora_strength"]}" + (p["init"]?.GetValue<string>() is { Length: > 0 } ? " · img2img" : "");
        DetailPrompt.Text = img.Prompt ?? "";
    }

    async void Fav_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } img) return;
        await Api.Post($"/api/images/{img.Id}/favorite", new { favorite = !img.Favorite });
        img.Favorite = !img.Favorite;
    }

    void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(DetailPrompt.Text);
        Clipboard.SetContent(dp);
    }

    async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } img) return;
        var path = await App.Main.PickSaveFile($"image_{img.Id}", "PNG", ".png");
        if (path == null) return;
        try
        {
            await Api.Post($"/api/images/{img.Id}/export", new { path });
            App.Main.ShowInfo(L.Z("已保存：", "Saved: ") + path);
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    void Show_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } img) Process.Start("explorer.exe", $"/select,\"{img.Path}\"");
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } img) return;
        try
        {
            var index = _items.IndexOf(img);
            await Api.Delete($"/api/images/{img.Id}");
            _items.Remove(img);
            Grid.SelectedItem = _items.Count == 0 ? null : _items[Math.Min(index, _items.Count - 1)];
            ShowDetail();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }
}
