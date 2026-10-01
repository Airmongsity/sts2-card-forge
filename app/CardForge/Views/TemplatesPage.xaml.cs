using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using CardForge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CardForge.Views;

public sealed partial class TemplatesPage : Page
{
    readonly List<ReferenceAsset> _allAssets = new();
    readonly ObservableCollection<AssetSection> _sections = new();
    ReferenceAsset? _selectedAsset;
    NativeScanResult? _scan;
    string _assetsRoot = "";

    public ObservableCollection<AssetSection> Sections => _sections;

    public TemplatesPage()
    {
        InitializeComponent();
        _sections.Add(new() { Title = L.Z("可玩角色", "Playable characters") });
        _sections.Add(new() { Title = L.Z("敌人", "Enemies") });
        _sections.Add(new() { Title = L.Z("遗物", "Relics") });
        _sections.Add(new() { Title = L.Z("药水", "Potions") });
        _sections.Add(new() { Title = L.Z("能力视觉效果", "Power effects") });
        _sections.Add(new() { Title = L.Z("其他与自定义素材", "Other & custom assets"), IsExpanded = true });
        RoleBox.ItemsSource = new List<Option>
        {
            new() { Id = "character", Name = L.Z("主角外观", "Main character") },
            new() { Id = "enemy", Name = L.Z("敌人外观", "Enemy") },
            new() { Id = "ally", Name = L.Z("队友外观", "Ally") },
            new() { Id = "object", Name = L.Z("物件", "Object") },
            new() { Id = "scene", Name = L.Z("场景", "Scene") },
            new() { Id = "style", Name = L.Z("风格", "Style") },
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            var settings = await Api.Get<JsonObject>("/api/settings");
            GameRootBox.Text = settings["sts2_game_root"]?.ToString() ?? "";
            _assetsRoot = settings["sts2_assets_root"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(GameRootBox.Text))
            {
                var discovery = await Api.Get<NativeGameDiscovery>("/api/reference-assets/discover-game");
                if (discovery.Found.FirstOrDefault() is { } game) GameRootBox.Text = game;
            }
            if (GdreTools.InstalledPath() != null)
                ExtractBtn.Content = L.Z("解包并导入", "Extract & import");
            await LoadAssets();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async Task LoadAssets()
    {
        var includeInternal = IncludeInternalBox.IsChecked == true ? "1" : "0";
        var assets = await Api.Get<List<ReferenceAsset>>($"/api/reference-assets?include_internal={includeInternal}");
        _allAssets.Clear();
        _allAssets.AddRange(assets);
        ApplyAssetFilter();
    }

    void ApplyAssetFilter()
    {
        var query = AssetSearchBox.Text.Trim();
        foreach (var section in _sections)
        {
            section.Assets.Clear();
            section.Count = 0;
        }
        foreach (var asset in _allAssets)
        {
            var section = _sections[GetSectionIndex(asset)];
            if (query.Length > 0 && !MatchesSearch(asset, section.Title, query)) continue;
            section.Assets.Add(asset);
            section.Count++;
        }
    }

    static bool MatchesSearch(ReferenceAsset asset, string section, string query)
    {
        var haystack = string.Join(" ", asset.Name, asset.Role, asset.Category, asset.SourceKind,
            asset.SourcePath, asset.Metadata.ToJsonString(), section);
        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    static int GetSectionIndex(ReferenceAsset asset)
    {
        var path = (asset.SourcePath + " " + (asset.Metadata["atlas"]?.ToString() ?? ""))
            .Replace('\\', '/').ToLowerInvariant();
        var parts = path.Split('/', ' ', StringSplitOptions.RemoveEmptyEntries);
        bool Has(params string[] words) => parts.Any(part => words.Contains(part, StringComparer.OrdinalIgnoreCase));

        if (asset.Role.Equals("character", StringComparison.OrdinalIgnoreCase) || Has("characters", "playable_characters"))
            return 0;
        if (asset.Role.Equals("enemy", StringComparison.OrdinalIgnoreCase) || Has("monsters", "enemies", "enemy"))
            return 1;
        if (Has("relic", "relics")) return 2;
        if (Has("potion", "potions")) return 3;
        if (Has("power", "powers", "effect", "effects", "vfx", "visual_effects")) return 4;
        return 5;
    }

    void AssetSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyAssetFilter();

    async void IncludeInternal_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadAssets(); } catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async Task SavePaths()
    {
        var settings = await Api.Get<JsonObject>("/api/settings");
        settings["sts2_game_root"] = GameRootBox.Text.Trim();
        settings["sts2_assets_root"] = _assetsRoot;
        await Api.Put<JsonObject>("/api/settings", settings);
    }

    async void PickGame_Click(object sender, RoutedEventArgs e)
    {
        if (await App.Main.PickFolder() is not { } path) return;
        GameRootBox.Text = path;
        _assetsRoot = Directory.EnumerateFiles(path, "*.pck", SearchOption.TopDirectoryOnly).Any() ? "" : path;
    }

    async void DetectGame_Click(object sender, RoutedEventArgs e)
    {
        var path = await Task.Run(() =>
        {
            foreach (var name in new[] { "SlayTheSpire2", "Slay the Spire 2" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                try { return Path.GetDirectoryName(process.MainModule?.FileName); }
                catch { }
                finally { process.Dispose(); }
            }
            return null;
        });
        if (path == null)
        {
            try
            {
                var discovery = await Api.Get<NativeGameDiscovery>("/api/reference-assets/discover-game");
                path = discovery.Found.FirstOrDefault();
            }
            catch (Exception ex) { App.Main.ShowError(ex.Message); return; }
        }
        if (path != null) { GameRootBox.Text = path; _assetsRoot = ""; }
        else App.Main.ShowError(L.Z("没有在运行实例或 Steam 库中找到 Slay the Spire 2。请手动选择目录。",
                                    "Slay the Spire 2 was not found in a running process or Steam library. Choose the folder manually."));
    }

    async Task ScanAssets()
    {
        var root = Directory.Exists(_assetsRoot) ? _assetsRoot : GameRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root)) return;
        Busy.IsActive = true;
        ScanBtn.IsEnabled = false;
        try
        {
            await SavePaths();
            var renderer = await Api.Get<SpineRendererInfo>("/api/reference-assets/renderer-info");
            _scan = await Api.Post<NativeScanResult>("/api/reference-assets/scan",
                new { root, language = L.Z("zh-CN", "en-US") });
            var incomplete = _scan.Spine.Count - _scan.Counts.ReadySpine;
            ScanStatus.Text = L.Z(
                $"发现 {_scan.Counts.Images} 张独立图片、{_scan.Counts.Spine} 个 Spine 骨骼包（{_scan.Counts.ReadySpine} 个纹理齐全，{incomplete} 个待补全）。",
                $"Found {_scan.Counts.Images} standalone images and {_scan.Counts.Spine} Spine bundles " +
                $"({_scan.Counts.ReadySpine} complete, {incomplete} incomplete).") + " " +
                (renderer.Ok ? L.Z($"4.2.{renderer.RuntimeVersion.Split('.').Last()} 渲染器就绪。 ",
                                   $"Renderer {renderer.RuntimeVersion} is ready. ")
                             : L.Z("4.2 渲染器不可用（需要 Node.js 20+ 与已安装的本地运行时）。 ",
                                   "The 4.2 renderer is unavailable (Node.js 20+ and the installed local runtime are required). ")) +
                (_scan.Counts.Packages > 0 && _scan.Counts.Spine == 0
                    ? L.Z($"发现 {_scan.Counts.Packages} 个 PCK；需要先在本机解包，才能索引其中的 Spine 资源。 ",
                          $"Found {_scan.Counts.Packages} PCK package(s); local extraction is required before their Spine assets can be indexed. ")
                    : "") +
                L.Z("扫描完成后会自动导入静态模板并渲染可用的 Spine 首帧。",
                    "After scanning, static templates and supported Spine first frames are imported automatically.");
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
        finally { Busy.IsActive = false; ScanBtn.IsEnabled = true; }
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ExtractBtn.IsEnabled = ScanBtn.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        _scan = null;
        try
        {
            await ScanAssets();
            if (_scan != null) await ImportScannedAssets();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
        finally
        {
            Busy.IsActive = false;
            InstallProgress.Visibility = Visibility.Collapsed;
            ExtractBtn.IsEnabled = ScanBtn.IsEnabled = true;
        }
    }

    async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GameRootBox.Text) || !ExtractBtn.IsEnabled) return;
        // Disable synchronously before the first await, so rapid clicks cannot enter the handler twice.
        ExtractBtn.IsEnabled = ScanBtn.IsEnabled = false;
        try { await SavePaths(); }
        catch (Exception ex)
        {
            App.Main.ShowError(ex.Message);
            ExtractBtn.IsEnabled = ScanBtn.IsEnabled = true;
            return;
        }
        string? pck = null;
        try
        {
            if (Directory.Exists(GameRootBox.Text))
                pck = Directory.EnumerateFiles(GameRootBox.Text, "*.pck", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(path => Path.GetFileName(path).Equals("SlayTheSpire2.pck", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Main.ShowError(ex.Message);
            ExtractBtn.IsEnabled = ScanBtn.IsEnabled = true;
            return;
        }
        if (pck == null)
        {
            ExtractBtn.IsEnabled = ScanBtn.IsEnabled = true;
            App.Main.ShowError(L.Z("所选目录中没有找到 PCK 文件。", "No PCK file was found in the selected folder."));
            return;
        }

        Busy.IsActive = true;
        ExtractBtn.IsEnabled = ScanBtn.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = true;
        var progress = new SetupStep
        {
            Id = "gdre", Title = "GDRE Tools", Description = "",
            Check = () => Task.FromResult(GdreTools.InstalledPath() != null),
            Install = (_, _) => Task.CompletedTask,
        };
        progress.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SetupStep.Detail) && progress.Detail.Length > 0)
                ScanStatus.Text = progress.Detail;
            if (e.PropertyName == nameof(SetupStep.Progress)) InstallProgress.Value = progress.Progress;
            if (e.PropertyName == nameof(SetupStep.Indeterminate)) InstallProgress.IsIndeterminate = progress.Indeterminate;
        };
        try
        {
            var gdre = await GdreTools.Install(progress);
            ExtractBtn.Content = L.Z("解包并导入", "Extract & import");
            ScanStatus.Text = L.Z("正在本地解包游戏资源，这可能需要几分钟…",
                                  "Extracting game assets locally; this may take a few minutes…");
            InstallProgress.IsIndeterminate = true;
            var result = await Api.Post<NativeExtractionResult>("/api/reference-assets/extract-pck",
                new { root = GameRootBox.Text.Trim(), pck, gdre });
            _assetsRoot = result.Root;
            await SavePaths();
            _scan = null;
            await ScanAssets();
            if (_scan != null) await ImportScannedAssets();
            ScanStatus.Text += result.Cached
                ? L.Z("；使用了已完成的解包缓存。", "; used the completed extraction cache.")
                : L.Z("；GDRE 解包完成。", "; GDRE extraction completed.");
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
        finally
        {
            Busy.IsActive = false;
            InstallProgress.Visibility = Visibility.Collapsed;
            ExtractBtn.IsEnabled = ScanBtn.IsEnabled = true;
        }
    }

    async Task ImportScannedAssets()
    {
        if (_scan == null) return;
        Busy.IsActive = true;
        InstallProgress.IsIndeterminate = false;
        InstallProgress.Value = 0;
        int done = 0, imported = 0, failed = 0;
        int skipped = 0;
        var errors = new List<NativeImportError>();
        var images = _scan.Images.Where(x => x.Localized || IncludeInternalBox.IsChecked == true).ToList();
        foreach (var batch in images.Chunk(200))
        {
            NativeImportResult? result = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    result = await Api.Post<NativeImportResult>("/api/reference-assets/import-batch",
                        new { items = batch.Select(x => new { path = x.Path, name = x.Name, role = x.Role, localized = x.Localized }).ToArray(),
                              category = "native", role = "style", language = L.Z("zh-CN", "en-US") });
                    break;
                }
                catch when (attempt == 0)
                {
                    ScanStatus.Text = L.Z($"导入批次 {done + 1}–{done + batch.Length} 失败，正在自动重试…",
                                          $"Batch {done + 1}–{done + batch.Length} failed; retrying automatically…");
                }
            }
            if (result == null) throw new InvalidOperationException(L.Z("静态模板批量导入失败。", "Static template batch import failed."));
            done += result.Total;
            imported += result.Imported;
            skipped += result.Skipped;
            failed += result.Failed;
            errors.AddRange(result.Errors.Take(Math.Max(0, 20 - errors.Count)));
            InstallProgress.Value = images.Count == 0 ? 100 : 100.0 * done / images.Count;
            ScanStatus.Text = L.Z($"正在导入静态模板 {done}/{images.Count}（跳过同名 {skipped}，失败 {failed}）…",
                                  $"Importing static templates {done}/{images.Count} ({skipped} same-name skipped, {failed} failed)…");
        }

        int rendered = 0, renderedBundles = 0, renderFailed = 0;
        var renderer = await Api.Get<SpineRendererInfo>("/api/reference-assets/renderer-info");
        var bundles = _scan.Spine.Where(x => x.Ready && x.Version.StartsWith("4.2.") &&
                                             (x.Role is "character" or "enemy") &&
                                             (x.Localized || IncludeInternalBox.IsChecked == true)).ToList();
        if (renderer.Ok)
        {
            InstallProgress.IsIndeterminate = false;
            for (var i = 0; i < bundles.Count; i++)
            {
                var bundle = bundles[i];
                try
                {
                    var frames = await Api.Post<List<ReferenceAsset>>("/api/reference-assets/render-spine", new
                    {
                        skeleton = bundle.Path, atlas = bundle.Atlas, textures = bundle.Textures,
                        name = bundle.Name, category = "native", role = bundle.Role, localized = bundle.Localized
                    });
                    rendered += frames?.Count ?? 0;
                    renderedBundles++;
                }
                catch (Exception ex)
                {
                    renderFailed++;
                    if (errors.Count < 20) errors.Add(new NativeImportError { Path = bundle.Path, Error = ex.Message });
                }
                InstallProgress.Value = bundles.Count == 0 ? 100 : 100.0 * (i + 1) / bundles.Count;
                ScanStatus.Text = L.Z($"正在渲染 Spine {i + 1}/{bundles.Count}（失败 {renderFailed}）：{bundle.Name}",
                                      $"Rendering Spine {i + 1}/{bundles.Count} ({renderFailed} failed): {bundle.Name}");
            }
        }
        await LoadAssets();
        Busy.IsActive = false;
        var summary = L.Z($"完成：新增静态模板 {imported}，跳过同名 {skipped}，Spine 动画帧 {rendered}（成功 {renderedBundles}/{bundles.Count} 个动画）。",
                          $"Done: {imported} new static templates, {skipped} same-name skipped, {rendered} Spine animation frames from {renderedBundles}/{bundles.Count} bundles.");
        if (failed + renderFailed > 0)
            summary += L.Z($" 跳过 {failed + renderFailed} 个失败文件。", $" Skipped {failed + renderFailed} failed files.");
        ScanStatus.Text = summary;
        if (errors.Count > 0)
            Setup.Log("Native import skipped files:\n" + string.Join("\n", errors.Select(e => $"{e.Path}: {e.Error}")));
    }

    async void ImportOne_Click(object sender, RoutedEventArgs e)
    {
        var path = await App.Main.PickFile(".png", ".jpg", ".jpeg", ".webp");
        if (path == null) return;
        try
        {
            await Api.Post<List<ReferenceAsset>>("/api/reference-assets/import",
                new { paths = new[] { path }, category = "custom", role = "style",
                      language = L.Z("zh-CN", "en-US") });
            await LoadAssets();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadAssets(); } catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    void AssetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var grid = (GridView)sender;
        var selected = grid.SelectedItem as ReferenceAsset;
        if (selected != null)
        {
            _selectedAsset = selected;
            ClearOtherSelections(this, grid);
            RoleBox.SelectedValue = selected.Role;
        }
        else if (_selectedAsset != null && e.RemovedItems.Contains(_selectedAsset))
            _selectedAsset = null;
        DeleteBtn.IsEnabled = RoleBox.IsEnabled = SaveRoleBtn.IsEnabled = _selectedAsset != null;
    }

    static void ClearOtherSelections(DependencyObject parent, GridView selectedGrid)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is GridView grid && grid != selectedGrid)
                grid.SelectedItem = null;
            else
                ClearOtherSelections(child, selectedGrid);
        }
    }

    async void SaveRole_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset is not ReferenceAsset asset || RoleBox.SelectedValue is not string role) return;
        try
        {
            var updated = await Api.Put<ReferenceAsset>($"/api/reference-assets/{asset.Id}",
                new { role });
            var all = _allAssets.IndexOf(asset);
            if (all >= 0) _allAssets[all] = updated;
            _selectedAsset = updated;
            ApplyAssetFilter();
            RoleBox.SelectedValue = updated.Role;
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset is not ReferenceAsset asset) return;
        try
        {
            await Api.Delete($"/api/reference-assets/{asset.Id}");
            _allAssets.Remove(asset);
            _selectedAsset = null;
            DeleteBtn.IsEnabled = RoleBox.IsEnabled = SaveRoleBtn.IsEnabled = false;
            ApplyAssetFilter();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }
}
