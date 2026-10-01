using System.Collections.ObjectModel;
using CardForge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace CardForge.Views;

public sealed partial class CharactersPage : Page
{
    readonly ObservableCollection<ModCharacter> _chars = new();
    ModCharacter? _current;
    bool _isNew, _syncingColor;
    readonly List<string> _pool = new();
    const int MaxPoolColors = 8;

    public CharactersPage()
    {
        InitializeComponent();
        CharList.ItemsSource = _chars;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            await Meta.Load();
            await Reload();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async Task Reload(string? select = null)
    {
        var list = await Api.Get<List<ModCharacter>>("/api/characters");
        _chars.Clear();
        foreach (var c in list) _chars.Add(c);
        CharList.SelectedItem = _chars.FirstOrDefault(c => c.Id == (select ?? _current?.Id)) ?? _chars.FirstOrDefault();
        if (CharList.SelectedItem == null) Show(null, false);
    }

    void CharList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CharList.SelectedItem is ModCharacter c) Show(c, false);
    }

    void Show(ModCharacter? c, bool isNew)
    {
        _current = c;
        _isNew = isNew;
        Editor.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = c == null ? Visibility.Visible : Visibility.Collapsed;
        SavedText.Text = "";
        if (c == null) return;
        NameBox.Text = c.Name;
        IdBox.Text = c.Id;
        IdBox.IsReadOnly = !isNew;   // cards and export folders refer to the id
        ColorBox.Text = c.Color;
        SwatchPreview.Background = new SolidColorBrush(Meta.ParseColor(c.Color));
        SetPool(c.Colors.Count > 0 ? c.Colors : [c.Color]);
        PaletteBox.Text = c.Palette;
        AppearanceBox.Text = c.Appearance;
        RefBox.Text = c.RefImage;
        UseRefSwitch.IsOn = c.UseRef;
        ShowRef();
    }

    void ShowRef() =>
        RefPreview.Source = File.Exists(RefBox.Text) ? new BitmapImage(new Uri(RefBox.Text)) : null;

    void New_Click(object sender, RoutedEventArgs e)
    {
        CharList.SelectedItem = null;
        Show(new ModCharacter
        {
            Name = L.Z("新角色", "New character"), Color = "#6a5acd",
            Colors = ["#6a5acd", "#334f80", "#9b4f91", "#c68a35"], UseRef = true
        }, true);
        IdBox.Focus(FocusState.Programmatic);
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var id = IdBox.Text.Trim().ToLowerInvariant();
        if (id.Length == 0)
        {
            App.Main.ShowError(L.Z("请填写角色 id（英文字母/数字）", "Enter a character id (letters/digits)"));
            return;
        }
        if (_isNew && _chars.Any(c => c.Id == id))
        {
            App.Main.ShowError(L.Z("已存在同 id 的角色", "A character with this id already exists"));
            return;
        }
        try
        {
            var saved = await Api.Put<ModCharacter>($"/api/characters/{Uri.EscapeDataString(id)}", new
            {
                name = NameBox.Text.Trim(),
                color = ColorBox.Text.Trim(),
                colors = _pool.Where(IsHex).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                palette = PaletteBox.Text.Trim(),
                appearance = AppearanceBox.Text.Trim(),
                ref_image = RefBox.Text,
                use_ref = UseRefSwitch.IsOn,
            });
            _current = saved;
            await Reload(saved.Id);
            SavedText.Text = L.Z("已保存 ", "Saved ") + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _isNew)
        {
            Show(null, false);
            return;
        }
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Z("删除角色？", "Delete character?"),
            Content = L.Z("属于该角色的卡牌会回退为“无色”画风，卡牌本身不会被删除。", "Cards of this character fall back to the Colorless style; the cards themselves are kept."),
            PrimaryButtonText = L.Z("删除", "Delete"),
            CloseButtonText = L.Z("取消", "Cancel"),
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await Api.Delete($"/api/characters/{Uri.EscapeDataString(_current.Id)}");
            _current = null;
            await Reload();
        }
        catch (Exception ex) { App.Main.ShowError(ex.Message); }
    }

    async void PickRef_Click(object sender, RoutedEventArgs e)
    {
        var f = await App.Main.PickFile(".png", ".jpg", ".jpeg", ".webp");
        if (f == null) return;
        RefBox.Text = f;
        ShowRef();
    }

    void ClearRef_Click(object sender, RoutedEventArgs e)
    {
        RefBox.Text = "";
        ShowRef();
    }

    void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var color = Meta.ParseColor(ColorBox.Text);
        SwatchPreview.Background = new SolidColorBrush(color);
        if (_syncingColor) return;
        _syncingColor = true;
        Picker.Color = color;
        _syncingColor = false;
    }

    void Picker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingColor) return;
        _syncingColor = true;
        ColorBox.Text = $"#{args.NewColor.R:x2}{args.NewColor.G:x2}{args.NewColor.B:x2}";
        _syncingColor = false;
    }

    static bool IsHex(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9a-fA-F]{6}$");
    static string Hex(Windows.UI.Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    void SetPool(IEnumerable<string> colors)
    {
        _pool.Clear();
        _pool.AddRange(colors.Where(IsHex).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPoolColors));
        if (_pool.Count == 0) _pool.Add(IsHex(ColorBox.Text) ? ColorBox.Text : "#6a5acd");
        RebuildPool();
    }

    void RebuildPool()
    {
        PoolColors.Children.Clear();
        for (var i = 0; i < _pool.Count; i++) PoolColors.Children.Add(PoolChip(i));
        AddPoolBtn.IsEnabled = _pool.Count < MaxPoolColors;
    }

    FrameworkElement PoolChip(int index)
    {
        var color = Meta.ParseColor(_pool[index]);
        var swatch = new Border
        {
            Width = 40, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xff, 0xff, 0xff)),
            Background = new SolidColorBrush(color),
        };
        var picker = new ColorPicker { IsAlphaEnabled = false, Color = color };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(picker);
        var button = new DropDownButton { Content = swatch, Padding = new Thickness(6, 4, 6, 4), Flyout = new Flyout { Content = content } };
        void SetTip() => ToolTipService.SetToolTip(button, _pool[index]);
        SetTip();
        picker.ColorChanged += (_, args) =>
        {
            if (index >= _pool.Count) return;
            _pool[index] = Hex(args.NewColor);
            swatch.Background = new SolidColorBrush(args.NewColor);
            SetTip();
        };
        var remove = new Button { Content = L.Z("移除此颜色", "Remove this colour"), HorizontalAlignment = HorizontalAlignment.Right };
        remove.IsEnabled = _pool.Count > 1;
        remove.Click += (_, _) =>
        {
            button.Flyout.Hide();
            _pool.RemoveAt(index);
            RebuildPool();
        };
        content.Children.Add(remove);
        return button;
    }

    void AddPool_Click(object sender, RoutedEventArgs e)
    {
        if (_pool.Count >= MaxPoolColors) return;
        _pool.Add(IsHex(ColorBox.Text) && !_pool.Contains(ColorBox.Text, StringComparer.OrdinalIgnoreCase)
            ? ColorBox.Text.ToLowerInvariant() : "#d08a1e");
        RebuildPool();
        if (PoolColors.Children.LastOrDefault() is DropDownButton b) b.Flyout.ShowAt(b);
    }

    void UseFrameColor_Click(object sender, RoutedEventArgs e)
    {
        var color = ColorBox.Text.Trim().ToLowerInvariant();
        if (!IsHex(color) || _pool.Contains(color, StringComparer.OrdinalIgnoreCase) || _pool.Count >= MaxPoolColors) return;
        _pool.Add(color);
        RebuildPool();
    }
}
