using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CardForge.Services;

/// <summary>Loads a local image without relying on WinUI's sticky localhost URI cache.</summary>
public sealed class LocalImage : UserControl
{
    bool _isLoaded;
    readonly Image _image = new();

    public LocalImage()
    {
        Content = _image;
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            QueueLoad();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            _image.Source = null;
        };
    }

    public ImageSource? Source
    {
        get => _image.Source;
        set => _image.Source = value;
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(Stretch), typeof(LocalImage), new PropertyMetadata(Stretch.Uniform, OnStretchChanged));

    static void OnStretchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((LocalImage)sender)._image.Stretch = (Stretch)args.NewValue;

    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(LocalImage), new PropertyMetadata("", OnChanged));

    public int DecodeWidth
    {
        get => (int)GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(
        nameof(DecodeWidth), typeof(int), typeof(LocalImage), new PropertyMetadata(0, OnChanged));

    static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs _)
    {
        var image = (LocalImage)sender;
        if (image._isLoaded) image.QueueLoad();
    }

    void QueueLoad()
    {
        _image.Source = null;
        var path = Path;
        if (!_isLoaded || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            // Let the stock Image/BitmapImage URI pipeline own decoding and cancellation.
            // Calling SetSourceAsync ourselves raced GridView recycling and could surface
            // as an unrecoverable Microsoft.UI.Xaml.dll 0xc000027b crash.
            var bitmap = new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            };
            if (DecodeWidth > 0) bitmap.DecodePixelWidth = DecodeWidth;
            bitmap.UriSource = new Uri(System.IO.Path.GetFullPath(path));
            _image.Source = bitmap;
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Local image failed: {path}: {error.Message}");
        }
    }
}
