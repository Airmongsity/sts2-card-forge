using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CardForge.Services;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class Card : Observable
{
    string _name = "", _slug = "", _cls = "colorless", _type = "attack", _rarity = "common", _cost = "1";
    string _description = "", _concept = "", _prompt = "", _negative = "", _project = "default";
    int? _selectedImage;
    double? _selectedCreated;
    int _imageCount, _pending;

    public int Id { get; set; }
    public string Project { get => _project; set => Set(ref _project, value); }
    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(Title)); } }
    public string Slug { get => _slug; set => Set(ref _slug, value); }
    public string Cls { get => _cls; set { if (Set(ref _cls, value)) { Raise(nameof(ClassBrush)); Raise(nameof(Subtitle)); } } }
    public string Type { get => _type; set { if (Set(ref _type, value)) Raise(nameof(Subtitle)); } }
    public string Rarity { get => _rarity; set { if (Set(ref _rarity, value)) Raise(nameof(Subtitle)); } }
    public string Cost { get => _cost; set => Set(ref _cost, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Concept { get => _concept; set => Set(ref _concept, value); }
    public string Prompt { get => _prompt; set { if (Set(ref _prompt, value)) Raise(nameof(Subtitle)); } }
    public string Negative { get => _negative; set => Set(ref _negative, value); }
    public JsonObject Params { get; set; } = new();
    public int? SelectedImage { get => _selectedImage; set { if (Set(ref _selectedImage, value)) Raise(nameof(Thumb)); } }
    public double? SelectedCreated { get => _selectedCreated; set { if (Set(ref _selectedCreated, value)) Raise(nameof(Thumb)); } }
    public int ImageCount { get => _imageCount; set { if (Set(ref _imageCount, value)) Raise(nameof(Subtitle)); } }
    public int Pending { get => _pending; set { if (Set(ref _pending, value)) Raise(nameof(PendingText)); } }

    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Name) ? L.Z("(未命名)", "(unnamed)") : Name;
    [JsonIgnore] public string Subtitle =>
        $"{Meta.TypeName(Type)} · {Meta.RarityName(Rarity)} · {ImageCount} {L.Z("张", "img")}" + (string.IsNullOrWhiteSpace(Prompt) ? L.Z(" · 无提示词", " · no prompt") : "");
    [JsonIgnore] public string PendingText => Pending > 0 ? $"⏳{Pending}" : "";
    [JsonIgnore] public Brush ClassBrush => Meta.ClassBrush(Cls);
    [JsonIgnore] public ImageSource? Thumb => SelectedImage is int id ? Api.Image(id, SelectedCreated, 160) : null;

    public object ToJson() => new
    {
        project = Project, name = Name, slug = Slug, cls = Cls, type = Type, rarity = Rarity, cost = Cost,
        description = Description, concept = Concept, prompt = Prompt, negative = Negative, @params = Params
    };
}

public class ImageRec : Observable
{
    bool _favorite;
    public int Id { get; set; }
    public int? CardId { get; set; }
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Seed { get; set; }
    public string? Prompt { get; set; }
    public JsonObject Params { get; set; } = new();
    [JsonConverter(typeof(IntBoolConverter))]
    public bool Favorite { get => _favorite; set { if (Set(ref _favorite, value)) Raise(nameof(FavGlyph)); } }
    public string? CardName { get; set; }
    public string? Project { get; set; }
    public double Created { get; set; }

    [JsonIgnore] public string FavGlyph => Favorite ? "★" : "";
    [JsonIgnore] public ImageSource Thumb => Api.Image(Id, Created, 360);
    [JsonIgnore] public ImageSource Full => Api.Image(Id, Created, null);
    [JsonIgnore] public string Caption => $"{CardName ?? L.Z("(无卡牌)", "(no card)")} · seed {Seed}";
}

public class Job : Observable
{
    public int Id { get; set; }
    public int? CardId { get; set; }
    public string Status { get; set; } = "";
    public JsonObject Params { get; set; } = new();
    public double Progress { get; set; }
    public string Message { get; set; } = "";
    public int? ImageId { get; set; }
    public string? CardName { get; set; }
    public double? ImageCreated { get; set; }

    [JsonIgnore] public bool Active => Status is "queued" or "running";
    [JsonIgnore] public string StatusText => Status switch
    {
        "queued" => L.Z("排队中", "Queued"),
        "running" => L.Z("生成中", "Running"),
        "done" => L.Z("完成", "Done"),
        "failed" => L.Z("失败", "Failed"),
        "cancelled" => L.Z("已取消", "Cancelled"),
        _ => Status
    };
    [JsonIgnore] public string PromptText => Params["prompt"]?.GetValue<string>() ?? "";
    [JsonIgnore] public string Title => $"#{Id} {CardName ?? L.Z("(无卡牌)", "(no card)")} · seed {Params["seed"]}";
    [JsonIgnore] public double Percent => Progress * 100;
    [JsonIgnore] public ImageSource? Thumb => ImageId is int id ? Api.Image(id, ImageCreated, 160) : null;
    [JsonIgnore] public ImageSource? Large => ImageId is int id ? Api.Image(id, ImageCreated, 720) : null;
}

public class WorkerStatus
{
    public bool Paused { get; set; }
    public int? Current { get; set; }
    public int Step { get; set; }
    public int Steps { get; set; }
    public int Queued { get; set; }
    public int CooldownLeft { get; set; }
    public bool Cooling { get; set; }
    public string CoolReason { get; set; } = "";
    public double? CoolPeak { get; set; }
    public double CoolSince { get; set; }
}

public class ThermalInfo
{
    public GpuStatus? Gpu { get; set; }
    public List<double> History { get; set; } = new();
    public double? Trigger { get; set; }
    public double? Resume { get; set; }
    public WorkerStatus Worker { get; set; } = new();
}

public class GpuStatus
{
    public string Name { get; set; } = "";
    public int Vram { get; set; }
    public int Temp { get; set; }
}

public class ComfyStatus
{
    public string State { get; set; } = "stopped";
    public string Error { get; set; } = "";
    public string Url { get; set; } = "";
    public string Profile { get; set; } = "";
}

public class StatusInfo
{
    public string Version { get; set; } = "";
    public ComfyStatus Comfy { get; set; } = new();
    public WorkerStatus Worker { get; set; } = new();
    public GpuStatus? Gpu { get; set; }
    public ThermalLimits? Thermal { get; set; }
}

public class ThermalLimits
{
    public double Trigger { get; set; } = 90;
    public double Resume { get; set; } = 70;
}

public class JobList
{
    public List<Job> Jobs { get; set; } = new();
    public WorkerStatus Worker { get; set; } = new();
}

public class Option
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Ref { get; set; }
    public bool UseRef { get; set; }
    public override string ToString() => Name;
}

public class SizeOption : Option
{
    public int[] Gen { get; set; } = [];
    public int[] Out { get; set; } = [];
}

public class MetaInfo
{
    public List<Option> Classes { get; set; } = new();
    public List<Option> Types { get; set; } = new();
    public List<Option> Rarities { get; set; } = new();
    public List<SizeOption> Sizes { get; set; } = new();
    public List<string> Loras { get; set; } = new();
    public string DefaultNegative { get; set; } = "";
    public string DefaultStyleSuffix { get; set; } = "";
    public string DefaultPromptSystem { get; set; } = "";
    public string DefaultPromptTemplate { get; set; } = "";
    public List<string> Projects { get; set; } = new();
}

public class PromptResult
{
    public string Prompt { get; set; } = "";
    public string Notes { get; set; } = "";
}

public class IdeaCard : Observable
{
    bool _include = true;
    public string Name { get; set; } = "";
    public string Type { get; set; } = "skill";
    public string Rarity { get; set; } = "common";
    public string Cost { get; set; } = "1";
    public string Description { get; set; } = "";
    public string Concept { get; set; } = "";
    public string Cls { get; set; } = "colorless";
    public bool Include { get => _include; set => Set(ref _include, value); }
    [JsonIgnore] public string Header => $"{Name}  ·  {Meta.TypeName(Type)} · {Meta.RarityName(Rarity)} · {Cost}";
}

public class ModCharacter : Observable
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7a7a9a";
    public string Palette { get; set; } = "";
    public string Appearance { get; set; } = "";
    public string RefImage { get; set; } = "";
    [JsonConverter(typeof(IntBoolConverter))]
    public bool UseRef { get; set; }

    [JsonIgnore] public Brush Swatch => new SolidColorBrush(Meta.ParseColor(Color));
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public class ExportResult
{
    public List<string> Written { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public string Folder { get; set; } = "";
}

/// <summary>SQLite stores booleans as 0/1.</summary>
public class IntBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref System.Text.Json.Utf8JsonReader r, Type t, System.Text.Json.JsonSerializerOptions o) =>
        r.TokenType switch
        {
            System.Text.Json.JsonTokenType.True => true,
            System.Text.Json.JsonTokenType.False => false,
            _ => r.GetInt32() != 0
        };

    public override void Write(System.Text.Json.Utf8JsonWriter w, bool v, System.Text.Json.JsonSerializerOptions o) => w.WriteBooleanValue(v);
}
