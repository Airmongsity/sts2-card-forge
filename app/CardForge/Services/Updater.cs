using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace CardForge.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string? ZipUrl, long Size);

/// <summary>Checks GitHub releases for a newer version and installs it: downloads the release zip, then a small script
/// waits for the app to exit, mirrors app/ backend/ examples/ over this install (data/ and ComfyUI are untouched)
/// and restarts the app.</summary>
public static class Updater
{
    public const string Repo = "Airmongsity/sts2-card-forge";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    public static Version Current { get; } = ParseVersion(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        ?? new Version(0, 0, 0);

    static HttpClient? _http;
    static Uri? _httpProxy;

    static async Task<HttpClient> Client()
    {
        var (proxy, _) = await NetProxy.Resolve();
        if (_http == null || _httpProxy != proxy)
        {
            // no auto-redirect: the fallback reads the tag from the /releases/latest redirect
            _http = new HttpClient(await NetProxy.Handler(allowRedirect: false)) { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("STS2CardForge/" + Current);
            _httpProxy = proxy;
        }
        return _http;
    }

    public static Version? ParseVersion(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        var end = s.IndexOfAny(['+', '-', ' ']);
        if (end >= 0) s = s[..end];
        // "v0.2" and "0.2.0" are the same version
        return Version.TryParse(s.Contains('.') ? s : s + ".0", out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>The release zip replaces app/ and backend/ wholesale, so only a release layout
    /// (&lt;root&gt;\app\CardForge.exe) can update itself; a source checkout just gets the notice.</summary>
    public static bool CanInstall =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), Path.Combine(AppPaths.Root, "app"), StringComparison.OrdinalIgnoreCase)
        && !Directory.Exists(Path.Combine(AppPaths.Root, ".git"));

    public static bool AutoCheck => AppPaths.LoadSettings()["update_check"]?.GetValue<bool>() ?? true;
    public static string Skipped => AppPaths.LoadSettings()["update_skip"]?.GetValue<string>() ?? "";

    /// <summary>The latest release if it is newer than this build, else null. Throws when GitHub is unreachable.</summary>
    public static async Task<UpdateInfo?> Check(CancellationToken ct = default)
    {
        // testing without publishing: CARDFORGE_UPDATE_ZIP=<local release zip> poses as the next version
        if (Environment.GetEnvironmentVariable("CARDFORGE_UPDATE_ZIP") is { Length: > 0 } local && File.Exists(local))
        {
            var next = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build) + 1);
            return new(next, "v" + next, "", ReleasesPage, local, new FileInfo(local).Length);
        }
        UpdateInfo? latest;
        try { latest = await FromApi(ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            latest = await FromRedirect(ct);   // api.github.com is rate limited / blocked more often than github.com
        }
        return latest != null && latest.Version > Current ? latest : null;
    }

    static async Task<UpdateInfo?> FromApi(CancellationToken ct)
    {
        using var resp = await (await Client()).GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;   // no release published yet
        resp.EnsureSuccessStatusCode();
        var rel = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        var tag = rel["tag_name"]?.GetValue<string>() ?? "";
        if (ParseVersion(tag) is not { } v) return null;
        var zip = rel["assets"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>().EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
        return new(v, tag, rel["body"]?.GetValue<string>() ?? "", rel["html_url"]?.GetValue<string>() ?? ReleasesPage,
            zip?["browser_download_url"]?.GetValue<string>(), zip?["size"]?.GetValue<long>() ?? -1);
    }

    static async Task<UpdateInfo?> FromRedirect(CancellationToken ct)
    {
        foreach (var url in Setup.GithubUrls($"https://github.com/{Repo}/releases/latest"))
        {
            try
            {
                using var resp = await (await Client()).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                var loc = resp.Headers.Location?.ToString() ?? "";
                var i = loc.IndexOf("/releases/tag/", StringComparison.Ordinal);
                if (i < 0)
                {
                    if ((int)resp.StatusCode < 500) return null;   // GitHub answered: no release published yet
                    continue;
                }
                var tag = Uri.UnescapeDataString(loc[(i + "/releases/tag/".Length)..]);
                if (ParseVersion(tag) is not { } v) return null;
                return new(v, tag, "", $"https://github.com/{Repo}/releases/tag/{tag}",
                    $"https://github.com/{Repo}/releases/download/{tag}/STS2CardForge.zip", -1);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        throw new Exception(L.Z("无法连接 GitHub", "GitHub is unreachable"));
    }

    static string UpdateDir => Path.Combine(AppPaths.Data, "update");

    /// <summary>Downloads and unpacks the release, returns the folder holding the new app/ and backend/.</summary>
    public static async Task<string> Download(UpdateInfo u, SetupStep progress, CancellationToken ct)
    {
        if (u.ZipUrl == null) throw new Exception(L.Z("这个版本没有附带安装包，请到发布页手动下载", "This release has no zip attached; download it from the release page"));
        Directory.CreateDirectory(UpdateDir);
        var zip = Path.Combine(UpdateDir, $"STS2CardForge-{u.Tag}.zip");
        if (File.Exists(u.ZipUrl)) File.Copy(u.ZipUrl, zip, true);
        else if (!File.Exists(zip)) await Setup.DownloadAny(Setup.GithubUrls(u.ZipUrl), zip, u.Size, null, progress, ct);

        progress.Report(L.Z("解压中…", "Extracting…"));
        var dir = Path.Combine(UpdateDir, "new");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        try { await Task.Run(() => ZipFile.ExtractToDirectory(zip, dir), ct); }
        catch
        {
            File.Delete(zip);
            throw new Exception(L.Z("安装包已损坏（已删除），请重试", "The download was damaged (deleted); try again"));
        }
        // the zip holds a single STS2CardForge\ folder
        var top = File.Exists(Path.Combine(dir, "app", "CardForge.exe")) ? dir
                  : Directory.GetDirectories(dir).FirstOrDefault(d => File.Exists(Path.Combine(d, "app", "CardForge.exe")))
                  ?? throw new Exception(L.Z("安装包里没有 CardForge.exe", "The release zip holds no CardForge.exe"));
        return top;
    }

    /// <summary>Starts the replace-and-restart script; the caller must exit the app right after.</summary>
    public static void Install(string newRoot)
    {
        var root = AppPaths.Root;
        var log = Path.Combine(AppPaths.Logs, "update.log");
        var bat = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("chcp 65001 >nul")
            .AppendLine($"set PID={Environment.ProcessId}")
            .AppendLine(":wait")
            .AppendLine("tasklist /FI \"PID eq %PID%\" /FO CSV /NH 2>nul | find /I \"CardForge\" >nul && (timeout /t 1 /nobreak >nul & goto wait)")
            .AppendLine("timeout /t 2 /nobreak >nul")   // let the backend and ComfyUI (same job object) finish exiting
            .AppendLine($"echo %date% %time% updating > \"{log}\"");
        foreach (var d in new[] { "app", "backend", "examples" })
            bat.AppendLine($"robocopy \"{Path.Combine(newRoot, d)}\" \"{Path.Combine(root, d)}\" /MIR /R:5 /W:2 /NFL /NDL /NP >> \"{log}\"")
               .AppendLine($"if errorlevel 8 goto failed");
        foreach (var f in new[] { "README.md", "NOTICE", "VERSION", "STS2 Card Forge.bat" })
            bat.AppendLine($"if exist \"{Path.Combine(newRoot, f)}\" copy /Y \"{Path.Combine(newRoot, f)}\" \"{Path.Combine(root, f)}\" >nul");
        bat.AppendLine($"start \"\" \"{Path.Combine(root, "app", "CardForge.exe")}\"")
           .AppendLine($"rmdir /s /q \"{Path.Combine(UpdateDir, "new")}\"")
           .AppendLine($"del /q \"{Path.Combine(UpdateDir, "*.zip")}\"")
           .AppendLine("exit /b 0")
           .AppendLine(":failed")
           .AppendLine($"echo update failed >> \"{log}\"")
           .AppendLine($"start \"\" \"{Path.Combine(root, "app", "CardForge.exe")}\"");

        Directory.CreateDirectory(AppPaths.Logs);
        // run the script from outside app\ so robocopy can replace everything in it
        var script = Path.Combine(UpdateDir, "apply_update.cmd");
        File.WriteAllText(script, bat.ToString(), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = UpdateDir,
        });
    }
}
