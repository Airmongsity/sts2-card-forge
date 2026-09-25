using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CardForge.Services;

/// <summary>Which proxy downloads, pip and the backend (Claude / OpenAI APIs) go through.
/// Setting "net_proxy": "auto" (default) | "off" | a URL such as "http://127.0.0.1:7890".
/// Auto: the Windows system proxy when one is set (everything already uses it); otherwise a proxy app's local port
/// found by probing the usual ones. Many users run Clash / v2rayN with "system proxy" off, so nothing used it.</summary>
public static class NetProxy
{
    /// <summary>Default local ports of common proxy apps, tried in this order.</summary>
    static readonly (int Port, string App)[] KnownPorts =
    [
        (7890, "Clash / mihomo"), (7897, "Clash Verge"), (10809, "v2rayN"),
        (2080, "NekoRay / sing-box"), (8889, "Qv2ray"), (20171, "v2rayA"), (33210, "ClashX"), (1080, "HTTP"),
    ];

    const string ProbeHost = "huggingface.co";

    static readonly SemaphoreSlim Gate = new(1, 1);
    static (string Setting, Uri? Proxy, string Label)? _cached;

    public static string Setting => AppPaths.LoadSettings()["net_proxy"]?.GetValue<string>()?.Trim() is { Length: > 0 } v ? v : "auto";

    /// <summary>The proxy in use (null = direct) and a short description for the Setup page.</summary>
    public static async Task<(Uri? Proxy, string Label)> Resolve(bool refresh = false)
    {
        await Gate.WaitAsync();
        try
        {
            var setting = Setting;
            if (!refresh && _cached is { } c && c.Setting == setting) return (c.Proxy, c.Label);
            var (proxy, label) = setting switch
            {
                "off" => (null, L.Z("不使用代理", "No proxy")),
                "auto" => await Auto(),
                _ => Uri.TryCreate(setting.Contains("://") ? setting : "http://" + setting, UriKind.Absolute, out var u)
                    ? (u, L.Z("手动", "Manual") + $": {u.Host}:{u.Port}")
                    : ((Uri?)null, L.Z("代理地址无效，未使用代理", "Invalid proxy address; not using a proxy")),
            };
            _cached = (setting, proxy, label);
            Setup.Log($"proxy: {label}");
            return (proxy, label);
        }
        finally { Gate.Release(); }
    }

    static async Task<(Uri?, string)> Auto()
    {
        if (SystemProxy() is Uri sys)
            return (sys, L.Z("系统代理", "System proxy") + $": {sys.Host}:{sys.Port}");
        var clock = Stopwatch.StartNew();
        // all ports at once: closed ones refuse instantly, so this takes about one handshake
        var probes = KnownPorts.Select(k => (k, Task.Run(() => IsHttpProxy(k.Port)))).ToList();
        foreach (var ((port, app), task) in probes)
        {
            if (await task)
            {
                Setup.Log($"proxy probe: 127.0.0.1:{port} ({app}) answers CONNECT, {clock.ElapsedMilliseconds} ms");
                return (new Uri($"http://127.0.0.1:{port}"), L.Z("自动检测", "Detected") + $": 127.0.0.1:{port} ({app})");
            }
        }
        return (null, L.Z("直连（未发现代理）", "Direct (no proxy found)"));
    }

    /// <summary>The Windows (WinINET) proxy for an HTTPS request to HuggingFace, or null when there is none.</summary>
    static Uri? SystemProxy()
    {
        try
        {
            var target = new Uri($"https://{ProbeHost}/");
            var p = HttpClient.DefaultProxy.GetProxy(target);
            return p == null || p == target || p.Host == target.Host ? null : p;
        }
        catch { return null; }
    }

    /// <summary>True when something on 127.0.0.1:port accepts an HTTP CONNECT to the probe host (Clash's mixed port,
    /// v2rayN's HTTP port…). A plain open port that speaks something else doesn't count.</summary>
    static async Task<bool> IsHttpProxy(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var s = tcp.GetStream();
            var req = Encoding.ASCII.GetBytes($"CONNECT {ProbeHost}:443 HTTP/1.1\r\nHost: {ProbeHost}:443\r\n\r\n");
            await s.WriteAsync(req, cts.Token);
            var buf = new byte[64];
            var n = await s.ReadAsync(buf, cts.Token);
            var line = Encoding.ASCII.GetString(buf, 0, n);
            return line.StartsWith("HTTP/1.") && line.Length > 12 && line[9] == '2';
        }
        catch { return false; }
    }

    /// <summary>An HttpClient handler that goes through the resolved proxy (or direct).</summary>
    public static async Task<HttpClientHandler> Handler(bool allowRedirect = true)
    {
        var (proxy, _) = await Resolve();
        return new HttpClientHandler
        {
            AllowAutoRedirect = allowRedirect,
            UseProxy = proxy != null,
            Proxy = proxy != null ? new WebProxy(proxy) { BypassProxyOnLocal = true } : null,
        };
    }

    /// <summary>Hands the proxy to a child process (pip, the backend and the ComfyUI it starts) through the standard
    /// variables, keeping local addresses (ComfyUI, the backend, Ollama) direct.</summary>
    public static async Task Apply(System.Diagnostics.ProcessStartInfo psi)
    {
        var (proxy, _) = await Resolve();
        if (proxy == null) return;
        var url = proxy.GetLeftPart(UriPartial.Authority);
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
            psi.Environment[name] = url;
        psi.Environment["NO_PROXY"] = psi.Environment["no_proxy"] = "localhost,127.0.0.1,::1";
    }
}
