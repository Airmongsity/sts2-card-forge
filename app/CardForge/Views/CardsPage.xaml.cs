using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using CardForge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Streams;

namespace CardForge.Views;

public sealed partial class CardsPage : Page
{
    readonly List<Card> _all = new();
    readonly ObservableCollection<Card> _visible = new();
    readonly ObservableCollection<ImageRec> _images = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    JsonObject _gen = new();
    Card? _current;
    string _project = "default";
    ImageRec? _menuImage;
    HashSet<int> _activeJobs = new();
    bool _loadingEditor;

    static string NoLora => L.Z("（不使用 LoRA）", "(no LoRA)");

    readonly MenuFlyout _imageMenu;

    public CardsPage()
    {
        InitializeComponent();
        _imageMenu = CreateImageMenu();
        CardList.ItemsSource = _visible;
        VariantGrid.ItemsSource = _images;
        _timer.Tick += async (_, _) => await Poll();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            await Meta.Load();   // MOD characters may have changed
            var settings = await Api.Get<JsonObject>("/api/settings");
            _gen = settings["gen"]!.AsObject();
            ClassBox.ItemsSource = Meta.Info.Classes.ToList();
            IdeaClassBox.ItemsSource = Meta.Info.Classes.ToList();
            TypeBox.ItemsSource = Meta.Info.Types;
            RarityBox.ItemsSource = Meta.Info.Rarities;
            SizeBox.ItemsSource = Meta.Info.Sizes;
            LoraBox.ItemsSource = new[] { NoLora }.Concat(Meta.Info.Loras).ToList();
            await LoadProjects();
            _timer.Start();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        await SaveCurrent(quiet: true);
    }

    // ---- projects & list ---------------------------------------------------------------------

    async Task LoadProjects()
    {
        var meta = await Api.Get<MetaInfo>("/api/meta");
        var projects = meta.Projects.Count > 0 ? meta.Projects : [_project];
        if (!projects.Contains(_project)) _project = projects[0];
        ProjectBox.ItemsSource = projects;
        ProjectBox.SelectedItem = _project;
        await LoadCards();
    }

    async Task LoadCards(int? select = null)
    {
        var cards = await Api.Get<List<Card>>("/api/cards?project=" + Uri.EscapeDataString(_project));
        _all.Clear();
        _all.AddRange(cards);
        ApplyFilter();
        var target = _visible.FirstOrDefault(c => c.Id == (select ?? _current?.Id)) ?? _visible.FirstOrDefault();
        CardList.SelectedItem = target;
        if (target == null) ShowEditor(null);
    }

    /// <summary>Refresh counts/art of listed cards without disturbing the selection or the editor.</summary>
    async Task RefreshCards()
    {
        var fresh = await Api.Get<List<Card>>("/api/cards?project=" + Uri.EscapeDataString(_project));
        foreach (var f in fresh)
        {
            var c = _all.FirstOrDefault(x => x.Id == f.Id);
            if (c == null) continue;
            c.ImageCount = f.ImageCount;
            c.Pending = f.Pending;
            c.SelectedImage = f.SelectedImage;
            c.SelectedCreated = f.SelectedCreated;
            if (c != _current) c.Prompt = f.Prompt;
        }
    }

    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        _visible.Clear();
        foreach (var c in _all.Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || c.Slug.Contains(q, StringComparison.OrdinalIgnoreCase)))
            _visible.Add(c);
    }

    async void ProjectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectBox.SelectedItem is string p && p != _project)
        {
            await SaveCurrent(quiet: true);
            _project = p;
            _current = null;
            await LoadCards();
        }
    }

    async void ProjectBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        var name = args.Text.Trim();
        if (name.Length == 0 || name == _project) return;
        await SaveCurrent(quiet: true);
        _project = name;
        _current = null;
        if (ProjectBox.ItemsSource is List<string> list && !list.Contains(name))
            ProjectBox.ItemsSource = list.Append(name).ToList();
        args.Handled = true;
        ProjectBox.SelectedItem = name;
        await LoadCards();
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    async void CardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardList.SelectedItem is not Card card || card == _current) return;
        await SaveCurrent(quiet: true);
        ShowEditor(card);
        await LoadImages();
    }

    // ---- editor ------------------------------------------------------------------------------

    JsonNode? Param(string key) => _current?.Params[key] ?? _gen[key];

    void ShowEditor(Card? card)
    {
        _current = card;
        Editor.Visibility = card == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = card == null ? Visibility.Visible : Visibility.Collapsed;
        _images.Clear();
        ArtImage.Source = null;
        ArtHint.Visibility = Visibility.Visible;
        PromptNotes.Text = SavedText.Text = CardJobText.Text = "";
        if (card == null) return;

        _loadingEditor = true;
        NameBox.Text = card.Name;
        SlugBox.Text = card.Slug;
        CostBox.Text = card.Cost;
        ClassBox.SelectedValue = card.Cls;
        TypeBox.SelectedValue = card.Type;
        RarityBox.SelectedValue = card.Rarity;
        DescBox.Text = card.Description;
        ConceptBox.Text = card.Concept;
        PromptBox.Text = card.Prompt;
        UpdatePromptPlaceholder();
        NegativeBox.Text = string.IsNullOrWhiteSpace(card.Negative) ? DefaultNegative : card.Negative;
        SuffixBox.Text = Param("style_suffix")?.GetValue<string>() ?? "";

        SizeBox.SelectedValue = Param("size_preset")?.GetValue<string>() ?? "sts2_card";
        VariantsBox.Value = Num(Param("variants"), 2);
        SeedBox.Text = card.Params["seed"]?.ToString() ?? "";
        var lora = Param("lora")?.GetValue<string>() ?? "";
        LoraBox.SelectedItem = string.IsNullOrEmpty(lora) ? NoLora : lora;
        if (LoraBox.SelectedItem == null && lora.Length > 0)
        {
            LoraBox.ItemsSource = ((List<string>)LoraBox.ItemsSource).Append(lora).ToList();
            LoraBox.SelectedItem = lora;
        }
        StrengthBox.Value = Num(Param("lora_strength"), 0.9);
        CfgBox.Value = Num(Param("cfg"), 3.0);
        StepsBox.Value = Num(Param("steps"), 25);
        DenoiseBox.Value = Num(Param("denoise"), 0.55);
        _inheritRef = !card.Params.ContainsKey("refs");
        RefBox.Text = _inheritRef ? CharacterRef(card.Cls) : card.Params["refs"]!.AsArray().FirstOrDefault()?.GetValue<string>() ?? "";
        InitBox.Text = card.Params["init"]?.GetValue<string>() ?? "";
        _inheritTheme = card.Params["theme_color"] is null;
        SetTheme(_inheritTheme ? CharacterColor(card.Cls) : card.Params["theme_color"]!.GetValue<string>());
        _loadingEditor = false;
        PromptParts_TextChanged(PromptBox, null!);
    }

    static double Num(JsonNode? n, double fallback) =>
        n == null ? fallback : double.TryParse(n.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    void ReadEditor()
    {
        var c = _current!;
        c.Name = NameBox.Text.Trim();
        c.Slug = SlugBox.Text.Trim();
        c.Cost = CostBox.Text.Trim();
        c.Cls = ClassBox.SelectedValue as string ?? c.Cls;
        c.Type = TypeBox.SelectedValue as string ?? c.Type;
        c.Rarity = RarityBox.SelectedValue as string ?? c.Rarity;
        c.Description = DescBox.Text;
        c.Concept = ConceptBox.Text;
        c.Prompt = PromptBox.Text.Trim();
        c.Negative = NegativeBox.Text.Trim() == DefaultNegative ? "" : NegativeBox.Text.Trim();

        // per-card params: keep only what differs from the global defaults, so changing a default still applies
        var p = new JsonObject();
        void Keep(string key, JsonNode? value)
        {
            if (value == null) return;
            if (_gen[key] is JsonNode d && d.ToJsonString() == value.ToJsonString()) return;
            p[key] = value;
        }
        Keep("size_preset", SizeBox.SelectedValue as string);
        Keep("variants", (int)VariantsBox.Value);
        var lora = LoraBox.SelectedItem as string;
        Keep("lora", lora == NoLora ? "" : lora);
        Keep("lora_strength", Math.Round(StrengthBox.Value, 3));
        Keep("cfg", Math.Round(CfgBox.Value, 2));
        Keep("steps", (int)StepsBox.Value);
        Keep("denoise", Math.Round(DenoiseBox.Value, 2));
        Keep("style_suffix", SuffixBox.Text.Trim());
        if (!_inheritTheme) p["theme_color"] = ThemeColorBox.Text.Trim();
        if (long.TryParse(SeedBox.Text.Trim(), out var seed)) p["seed"] = seed;
        if (!_inheritRef) p["refs"] = RefBox.Text.Length > 0 ? new JsonArray(RefBox.Text) : new JsonArray();
        if (InitBox.Text.Length > 0) p["init"] = InitBox.Text;
        c.Params = p;
    }

    async Task SaveCurrent(bool quiet = false)
    {
        if (_current == null || _loadingEditor) return;
        ReadEditor();
        try
        {
            await Api.Put<Card>($"/api/cards/{_current.Id}", _current.ToJson());
            if (!quiet) SavedText.Text = L.Z("已保存 ", "Saved ") + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void Save_Click(object sender, RoutedEventArgs e) => await SaveCurrent();

    string DefaultNegative => _gen["negative"]?.GetValue<string>() ?? Meta.Info.DefaultNegative;

    /// <summary>Cards belong to MOD characters; without one there is nothing to attach a card to.</summary>
    bool RequireCharacter()
    {
        if (Meta.Info.Classes.Count > 0) return true;
        App.Main.ShowError(L.Z("请先在“MOD 角色”页创建角色。", "Create a character on the Characters page first."));
        App.Main.Navigate("characters");
        return false;
    }

    void UpdatePromptPlaceholder()
    {
        var trigger = ClassBox.SelectedValue as string ?? "my_hero";
        PromptBox.PlaceholderText = $"sts2 card art, {trigger} card. " +
                                    L.Z("（描述画面内容与配色，或点上方“AI 生成提示词”）", "(describe the subject and colours, or click Generate prompt with AI)");
    }

    bool _inheritRef = true;

    static string CharacterRef(string? cls) =>
        Meta.Info.Classes.FirstOrDefault(c => c.Id == cls) is { UseRef: true, Ref: { Length: > 0 } r } ? r : "";

    /// <summary>What generation actually sends: the prompt plus the style suffix (same rule as the backend).</summary>
    void PromptParts_TextChanged(object sender, TextChangedEventArgs e)
    {
        var prompt = PromptBox.Text.Trim();
        var suffix = SuffixBox.Text.Trim();
        FinalPromptText.Text = prompt.Length == 0 ? L.Z("（先写提示词或点“AI 生成提示词”）", "(write a prompt or click Generate prompt with AI)")
                             : suffix.Length == 0 || prompt.Contains(suffix) ? prompt : $"{prompt} {suffix}";
    }

    void ClassBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePromptPlaceholder();
        if (_inheritRef && !_loadingEditor) RefBox.Text = CharacterRef(ClassBox.SelectedValue as string);
        if (_inheritTheme && !_loadingEditor) SetTheme(CharacterColor(ClassBox.SelectedValue as string));
    }

    // ---- theme colour: defaults to the character colour; the card keeps its own once changed ----------------

    bool _inheritTheme = true, _syncingTheme;
    static readonly Random Rng = new();

    static string CharacterColor(string? cls) =>
        Meta.Info.Classes.FirstOrDefault(c => c.Id == cls)?.Color is { Length: > 0 } c ? c : "#2a8a8a";

    void SetTheme(string hex)
    {
        _syncingTheme = true;
        ThemeColorBox.Text = hex;
        var color = Meta.ParseColor(hex);
        ThemeSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        ThemePicker.Color = color;
        _syncingTheme = false;
    }

    void ThemeColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingTheme) return;
        var color = Meta.ParseColor(ThemeColorBox.Text);
        ThemeSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        _syncingTheme = true;
        ThemePicker.Color = color;
        _syncingTheme = false;
        if (!_loadingEditor) _inheritTheme = false;
    }

    void ThemePicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingTheme) return;
        var c = args.NewColor;
        SetTheme($"#{c.R:x2}{c.G:x2}{c.B:x2}");
        _inheritTheme = false;
    }

    /// <summary>A random vivid colour (any hue, strong saturation, mid brightness) like the game's card backdrops.</summary>
    void RandomTheme_Click(object sender, RoutedEventArgs e)
    {
        double h = Rng.NextDouble() * 6, s = 0.6 + Rng.NextDouble() * 0.35, v = 0.5 + Rng.NextDouble() * 0.4;
        double c = v * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
        var (r, g, b) = (int)h switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        SetTheme($"#{(int)((r + m) * 255):x2}{(int)((g + m) * 255):x2}{(int)((b + m) * 255):x2}");
        _inheritTheme = false;
    }

    void InheritTheme_Click(object sender, RoutedEventArgs e)
    {
        _inheritTheme = true;
        SetTheme(CharacterColor(ClassBox.SelectedValue as string));
    }

    void InheritRef_Click(object sender, RoutedEventArgs e)
    {
        _inheritRef = true;
        RefBox.Text = CharacterRef(ClassBox.SelectedValue as string);
    }

    async void NewCard_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrent(quiet: true);
        if (!RequireCharacter()) return;
        try
        {
            var cls = Meta.Info.Classes.Any(c => c.Id == _current?.Cls) ? _current!.Cls : Meta.Info.Classes[0].Id;
            var card = await Api.Post<Card>("/api/cards", new
            {
                project = _project, name = L.Z("新卡牌", "New card"),
                cls, type = "attack", rarity = "common", cost = "1"
            });
            await LoadCards(card.Id);
            NameBox.Focus(FocusState.Programmatic);
            NameBox.SelectAll();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void DeleteCard_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Z("删除卡牌？", "Delete card?"),
            Content = L.Z($"“{_current.Title}”将被删除，它的图片仍保留在图库中。", $"\"{_current.Title}\" will be deleted. Its images stay in the gallery."),
            PrimaryButtonText = L.Z("删除", "Delete"),
            CloseButtonText = L.Z("取消", "Cancel"),
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await Api.Delete($"/api/cards/{_current.Id}");
            _current = null;
            await LoadCards();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    // ---- prompt & generation -----------------------------------------------------------------

    async void GenPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        await SaveCurrent(quiet: true);
        PromptBtn.IsEnabled = false;
        PromptRing.IsActive = true;
        PromptNotes.Text = L.Z("正在构思画面…", "Thinking about the picture…");
        var card = _current;
        try
        {
            var res = await Api.Post<PromptResult>($"/api/cards/{card.Id}/prompt");
            card.Prompt = res.Prompt;
            if (_current == card)
            {
                PromptBox.Text = res.Prompt;
                PromptNotes.Text = res.Notes;
            }
        }
        catch (Exception ex)
        {
            PromptNotes.Text = "";
            App.Main.ShowError(ex.Message);
        }
        finally
        {
            PromptBtn.IsEnabled = true;
            PromptRing.IsActive = false;
        }
    }

    async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        await SaveCurrent(quiet: true);
        try
        {
            var res = await Api.Post("/api/jobs", new { card_id = _current.Id });
            var n = res["jobs"]!.AsArray().Count;
            CardJobText.Text = L.Z($"已加入队列：{n} 张", $"Queued {n} image(s)");
            _current.Pending += n;
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async Task Poll()
    {
        if (_current == null) return;
        JobList jobs;
        try { jobs = await Api.Get<JobList>("/api/jobs?active=1"); }
        catch { return; }

        var active = jobs.Jobs.Select(j => j.Id).ToHashSet();
        bool finished = _activeJobs.Except(active).Any();
        _activeJobs = active;
        if (finished)
        {
            await RefreshCards();
            await LoadImages();
        }

        var mine = jobs.Jobs.Where(j => j.CardId == _current.Id).ToList();
        var running = mine.FirstOrDefault(j => j.Status == "running");
        CardProgress.Visibility = mine.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CardProgress.IsIndeterminate = running == null;
        if (running != null) CardProgress.Value = running.Percent;
        CardJobText.Text = mine.Count == 0 ? "" :
            running != null ? $"{running.Message}" + (mine.Count > 1 ? L.Z($"  ·  还有 {mine.Count - 1} 张排队", $"  ·  {mine.Count - 1} more queued") : "")
                            : L.Z($"排队中：{mine.Count} 张", $"{mine.Count} queued") + (jobs.Worker.CooldownLeft > 0 ? L.Z($"（GPU 冷却 {jobs.Worker.CooldownLeft}s）", $" (GPU cooldown {jobs.Worker.CooldownLeft}s)") : "");

        if (running != null)
        {
            var bytes = await Api.GetBytes("/api/preview");
            if (bytes != null)
            {
                var bmp = new BitmapImage();
                using var ms = new InMemoryRandomAccessStream();
                await ms.WriteAsync(bytes.AsBuffer());
                ms.Seek(0);
                await bmp.SetSourceAsync(ms);
                LivePreview.Source = bmp;
                LivePreview.Visibility = Visibility.Visible;
                return;
            }
        }
        LivePreview.Visibility = Visibility.Collapsed;
    }

    // ---- images ------------------------------------------------------------------------------

    async Task LoadImages()
    {
        if (_current == null) return;
        var card = _current;
        List<ImageRec> list;
        try { list = await Api.Get<List<ImageRec>>($"/api/images?card_id={card.Id}"); }
        catch { return; }
        if (card != _current) return;
        _images.Clear();
        foreach (var i in list) _images.Add(i);
        ShowArt();
    }

    void ShowArt()
    {
        var sel = _current?.SelectedImage;
        var img = _images.FirstOrDefault(i => i.Id == sel);
        ArtImage.Source = img?.Full;
        ArtHint.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
    }

    async void VariantGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ImageRec img) await SelectArt(img);
    }

    async Task SelectArt(ImageRec img)
    {
        if (_current == null) return;
        try
        {
            await Api.Post($"/api/images/{img.Id}/select", new { card_id = _current.Id });
            _current.SelectedImage = img.Id;
            _current.SelectedCreated = img.Created;
            ShowArt();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    void Variant_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var el = (FrameworkElement)sender;
        _menuImage = el.DataContext as ImageRec;
        _imageMenu.ShowAt(el, e.GetPosition(el));
    }

    MenuFlyout CreateImageMenu()
    {
        var menu = new MenuFlyout();
        void Add(string zh, string en, Symbol icon, RoutedEventHandler click)
        {
            var item = new MenuFlyoutItem { Text = L.Z(zh, en), Icon = new SymbolIcon(icon) };
            item.Click += click;
            menu.Items.Add(item);
        }
        Add("设为卡图", "Use as card art", Symbol.Accept, ImgSelect_Click);
        Add("精修（img2img 放大重绘）", "Refine (img2img at full size)", Symbol.Refresh, ImgRefine_Click);
        Add("复用种子", "Reuse seed", Symbol.Repair, ImgSeed_Click);
        Add("作为参考图", "Use as reference image", Symbol.Pictures, ImgRef_Click);
        Add("作为起始图 (img2img)", "Use as img2img start", Symbol.Edit, ImgInit_Click);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("收藏 / 取消收藏", "Favorite / unfavorite", Symbol.OutlineStar, ImgFav_Click);
        Add("另存为…", "Save as…", Symbol.Save, ImgSaveAs_Click);
        Add("在资源管理器中显示", "Show in Explorer", Symbol.OpenLocal, ImgShow_Click);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("删除", "Delete", Symbol.Delete, ImgDelete_Click);
        return menu;
    }

    async void ImgSelect_Click(object sender, RoutedEventArgs e) { if (_menuImage != null) await SelectArt(_menuImage); }

    async void ImgRefine_Click(object sender, RoutedEventArgs e)
    {
        if (_menuImage == null) return;
        try
        {
            await Api.Post($"/api/images/{_menuImage.Id}/refine", new { denoise = DenoiseBox.Value });
            CardJobText.Text = L.Z("已加入精修队列", "Refine queued");
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    void ImgSeed_Click(object sender, RoutedEventArgs e) { if (_menuImage != null) { SeedBox.Text = _menuImage.Seed.ToString(); VariantsBox.Value = 1; ParamsExpander.IsExpanded = true; } }
    void ImgRef_Click(object sender, RoutedEventArgs e) { if (_menuImage != null) { RefBox.Text = _menuImage.Path; _inheritRef = false; ParamsExpander.IsExpanded = true; } }
    void ImgInit_Click(object sender, RoutedEventArgs e) { if (_menuImage != null) { InitBox.Text = _menuImage.Path; ParamsExpander.IsExpanded = true; } }

    async void ImgFav_Click(object sender, RoutedEventArgs e)
    {
        if (_menuImage == null) return;
        var img = _menuImage;
        await Api.Post($"/api/images/{img.Id}/favorite", new { favorite = !img.Favorite });
        img.Favorite = !img.Favorite;
    }

    async void ImgSaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_menuImage == null || _current == null) return;
        var img = _menuImage;
        var path = await App.Main.PickSaveFile(string.IsNullOrEmpty(_current.Slug) ? $"card_{_current.Id}" : _current.Slug, "PNG", ".png");
        if (path == null) return;
        try
        {
            var res = await Api.Post($"/api/images/{img.Id}/export", new { path, size_preset = SizeBox.SelectedValue as string });
            App.Main.ShowInfo(L.Z("已保存：", "Saved: ") + res["path"]);
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    void ImgShow_Click(object sender, RoutedEventArgs e)
    {
        if (_menuImage != null) Process.Start("explorer.exe", $"/select,\"{_menuImage.Path}\"");
    }

    async void ImgDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_menuImage == null) return;
        try
        {
            await Api.Delete($"/api/images/{_menuImage.Id}");
            _images.Remove(_menuImage);
            if (_current?.SelectedImage == _menuImage.Id) _current.SelectedImage = null;
            ShowArt();
            await RefreshCards();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void PickRef_Click(object sender, RoutedEventArgs e)
    {
        var f = await App.Main.PickFile(".png", ".jpg", ".jpeg", ".webp");
        if (f == null) return;
        RefBox.Text = f;
        _inheritRef = false;
    }

    void ClearRef_Click(object sender, RoutedEventArgs e)
    {
        RefBox.Text = "";
        _inheritRef = false;
    }

    async void PickInit_Click(object sender, RoutedEventArgs e)
    {
        var f = await App.Main.PickFile(".png", ".jpg", ".jpeg", ".webp");
        if (f != null) InitBox.Text = f;
    }

    void ClearInit_Click(object sender, RoutedEventArgs e) => InitBox.Text = "";

    // ---- AI ideas ----------------------------------------------------------------------------

    async void Ideas_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrent(quiet: true);
        if (!RequireCharacter()) return;
        IdeaClassBox.SelectedValue = Meta.Info.Classes.Any(c => c.Id == _current?.Cls) ? _current!.Cls : Meta.Info.Classes[0].Id;
        IdeasDialog.XamlRoot = XamlRoot;
        if (await IdeasDialog.ShowAsync() != ContentDialogResult.Primary) return;
        var chosen = (IdeaList.ItemsSource as List<IdeaCard>)?.Where(i => i.Include).ToList();
        if (chosen is not { Count: > 0 }) return;
        try
        {
            var created = await Api.Post<List<Card>>("/api/cards/bulk", new
            {
                project = _project,
                cards = chosen.Select(c => new { name = c.Name, cls = c.Cls, type = c.Type, rarity = c.Rarity, cost = c.Cost, description = c.Description, concept = c.Concept })
            });
            IdeaList.ItemsSource = null;
            await LoadCards(created.FirstOrDefault()?.Id);
            App.Main.ShowInfo(L.Z($"已添加 {created.Count} 张卡。可用“批量 → 为缺少提示词的卡生成提示词”。",
                                  $"Added {created.Count} cards. Try Batch → Generate missing prompts."));
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void IdeaGo_Click(object sender, RoutedEventArgs e)
    {
        IdeaGoBtn.IsEnabled = false;
        IdeaRing.IsActive = true;
        try
        {
            IdeaList.ItemsSource = await Api.Post<List<IdeaCard>>("/api/ideas", new
            {
                theme = ThemeBox.Text, cls = IdeaClassBox.SelectedValue as string ?? "colorless",
                count = (int)IdeaCountBox.Value, project = _project
            });
        }
        catch (Exception ex)
        {
            IdeaList.ItemsSource = new List<IdeaCard> { new() { Name = L.Z("出错了", "Error"), Description = ex.Message, Include = false } };
        }
        finally
        {
            IdeaGoBtn.IsEnabled = true;
            IdeaRing.IsActive = false;
        }
    }

    // ---- batch -------------------------------------------------------------------------------

    async void BatchPrompts_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrent(quiet: true);
        var todo = _all.Where(c => string.IsNullOrWhiteSpace(c.Prompt)).ToList();
        int done = 0;
        foreach (var c in todo)
        {
            SavedText.Text = L.Z($"生成提示词 {done + 1}/{todo.Count}：{c.Title}", $"Prompt {done + 1}/{todo.Count}: {c.Title}");
            try
            {
                var res = await Api.Post<PromptResult>($"/api/cards/{c.Id}/prompt");
                c.Prompt = res.Prompt;
                if (c == _current) PromptBox.Text = res.Prompt;
                done++;
            }
            catch (Exception ex)
            {
                App.Main.ShowError($"{c.Title}: {ex.Message}");
                break;
            }
        }
        SavedText.Text = L.Z($"已生成 {done} 条提示词", $"Generated {done} prompts");
    }

    async Task QueueProject(bool onlyMissing)
    {
        await SaveCurrent(quiet: true);
        try
        {
            var res = await Api.Post("/api/jobs/project", new { project = _project, only_missing = onlyMissing });
            var n = res["jobs"]!.AsArray().Count;
            var skipped = res["skipped"]!.AsArray().Count;
            App.Main.ShowInfo(L.Z($"已排队 {n} 张图", $"Queued {n} images") +
                              (skipped > 0 ? L.Z($"；{skipped} 张卡没有提示词被跳过", $"; skipped {skipped} cards without a prompt") : ""));
            await RefreshCards();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void BatchGenerateMissing_Click(object sender, RoutedEventArgs e) => await QueueProject(true);
    async void BatchGenerateAll_Click(object sender, RoutedEventArgs e) => await QueueProject(false);

    async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var path = await App.Main.PickFile(".csv");
        if (path == null) return;
        try
        {
            var created = await Api.Post<List<Card>>("/api/cards/import", new { csv = await File.ReadAllTextAsync(path), project = _project });
            await LoadCards(created.FirstOrDefault()?.Id);
            App.Main.ShowInfo(L.Z($"已导入 {created.Count} 张卡", $"Imported {created.Count} cards"));
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrent(quiet: true);
        var path = await App.Main.PickSaveFile(_project + "_cards", "CSV", ".csv");
        if (path == null) return;
        try
        {
            await File.WriteAllTextAsync(path, await Api.GetText("/api/cards/export.csv?project=" + Uri.EscapeDataString(_project)));
            App.Main.ShowInfo(L.Z("已导出：", "Exported: ") + path);
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    // ---- export art ----------------------------------------------------------------------------

    async void ExportCard_Click(object sender, RoutedEventArgs e) { if (_current != null) await Export([_current.Id]); }
    async void ExportProject_Click(object sender, RoutedEventArgs e) => await Export(null);

    async Task Export(int[]? ids)
    {
        await SaveCurrent(quiet: true);
        var settings = await Api.Get<JsonObject>("/api/settings");
        ExportDirBox.Text = settings["export_dir"]?.GetValue<string>() ?? "";
        ExportByClass.IsChecked = settings["export_by_class"]?.GetValue<bool>() ?? false;
        var sizes = new List<Option> { new() { Id = "", Name = L.Z("每张卡使用自己的尺寸设置", "Use each card's own size") } };
        sizes.AddRange(Meta.Info.Sizes.Where(s => s.Id != "draft"));
        ExportSizeBox.ItemsSource = sizes;
        ExportSizeBox.SelectedIndex = 0;
        ExportDialog.XamlRoot = XamlRoot;
        if (await ExportDialog.ShowAsync() != ContentDialogResult.Primary) return;

        var folder = ExportDirBox.Text.Trim();
        var byClass = ExportByClass.IsChecked == true;
        try
        {
            await Api.Put<JsonObject>("/api/settings", new { export_dir = folder, export_by_class = byClass });
            var size = ExportSizeBox.SelectedValue as string;
            var res = await Api.Post<ExportResult>("/api/export", new
            {
                card_ids = ids, project = _project, folder, by_class = byClass,
                size_preset = string.IsNullOrEmpty(size) ? null : size,
            });
            App.Main.ShowInfo(L.Z($"已导出 {res.Written.Count} 张到 {res.Folder}", $"Exported {res.Written.Count} to {res.Folder}") +
                              (res.Skipped.Count > 0 ? L.Z($"；无卡图跳过：{string.Join("、", res.Skipped)}", $"; skipped (no art): {string.Join(", ", res.Skipped)}") : ""));
            if (res.Written.Count > 0) Process.Start("explorer.exe", res.Folder);
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void PickExportDir_Click(object sender, RoutedEventArgs e)
    {
        var f = await App.Main.PickFolder();
        if (f != null) ExportDirBox.Text = f;
    }
}
