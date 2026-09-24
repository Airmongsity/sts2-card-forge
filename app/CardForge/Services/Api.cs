using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace CardForge.Services;

public class ApiException(string message) : Exception(message);

/// <summary>Thin client for the Python backend (backend/server.py).</summary>
public static class Api
{
    public static int Port { get; set; } = 8190;
    public static string Base => $"http://127.0.0.1:{Port}";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static async Task<T> Read<T>(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode)
        {
            string msg = text;
            try { msg = JsonNode.Parse(text)?["error"]?.GetValue<string>() ?? text; } catch { }
            throw new ApiException(string.IsNullOrWhiteSpace(msg) ? $"HTTP {(int)r.StatusCode}" : msg);
        }
        return JsonSerializer.Deserialize<T>(text, Json)!;
    }

    public static async Task<T> Get<T>(string path)
    {
        try { return await Read<T>(await Http.GetAsync(Base + path)); }
        catch (HttpRequestException) { throw new ApiException(L.Z("后端未运行", "Backend is not running")); }
    }

    public static async Task<T> Send<T>(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, Base + path);
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        try { return await Read<T>(await Http.SendAsync(req)); }
        catch (HttpRequestException) { throw new ApiException(L.Z("后端未运行", "Backend is not running")); }
    }

    public static Task<T> Post<T>(string path, object? body = null) => Send<T>(HttpMethod.Post, path, body ?? new { });
    public static Task<JsonNode> Post(string path, object? body = null) => Send<JsonNode>(HttpMethod.Post, path, body ?? new { });
    public static Task<T> Put<T>(string path, object body) => Send<T>(HttpMethod.Put, path, body);
    public static Task<JsonNode> Delete(string path) => Send<JsonNode>(HttpMethod.Delete, path);

    /// <summary>Image URLs carry the image's creation time, so an id reused by another database (or a replaced
    /// file) never shows a stale picture from WinUI's image cache.</summary>
    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage Image(int id, double? created, int? width)
    {
        var v = ((long)((created ?? 0) * 1000)).ToString();
        var url = width is int w ? $"{Base}/api/images/{id}/thumb?w={w}&v={v}" : $"{Base}/api/images/{id}/file?v={v}";
        return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url));
    }

    public static async Task<string> GetText(string path) => await Http.GetStringAsync(Base + path);

    public static async Task<byte[]?> GetBytes(string path)
    {
        try
        {
            var r = await Http.GetAsync(Base + path);
            return r.StatusCode == System.Net.HttpStatusCode.OK ? await r.Content.ReadAsByteArrayAsync() : null;
        }
        catch (HttpRequestException) { return null; }
    }

    public static async Task<bool> Healthy()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var r = await Http.GetAsync(Base + "/api/health", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}

/// <summary>Enumerations served by the backend (classes, types, sizes, LoRAs), with localized names.</summary>
public static class Meta
{
    public static MetaInfo Info { get; private set; } = new();

    public static async Task Load()
    {
        Info = await Api.Get<MetaInfo>("/api/meta");
        foreach (var o in Info.Classes.Concat(Info.Types).Concat(Info.Rarities)) o.Name = L.Pick(o.Name);
        Brushes.Clear();
    }

    public static string TypeName(string id) => Info.Types.FirstOrDefault(t => t.Id == id)?.Name ?? id;
    public static string RarityName(string id) => Info.Rarities.FirstOrDefault(t => t.Id == id)?.Name ?? id;

    static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static Brush ClassBrush(string cls)
    {
        if (Brushes.TryGetValue(cls, out var b)) return b;
        var hex = Info.Classes.FirstOrDefault(c => c.Id == cls)?.Color ?? "#808080";
        b = new SolidColorBrush(ParseColor(hex));
        Brushes[cls] = b;
        return b;
    }

    public static Windows.UI.Color ParseColor(string hex)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        return hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : Colors.Gray;
    }
}
