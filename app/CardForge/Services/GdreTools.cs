using System.IO.Compression;
using System.Text.Json.Nodes;

namespace CardForge.Services;

/// <summary>Installs the latest stable official GDRE Tools Windows release into CardForge's private tools folder.</summary>
public static class GdreTools
{
    const string Repo = "GDRETools/gdsdecomp";
    static string Root => Path.Combine(AppPaths.Tools, "gdre");

    public static string? InstalledPath()
    {
        if (!Directory.Exists(Root)) return null;
        return Directory.EnumerateFiles(Root, "gdre_tools.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    static async Task<(string Tag, string Url, long Size, string? Sha256)> Latest(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(await NetProxy.Handler()) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("STS2CardForge/1.0");
            using var response = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            response.EnsureSuccessStatusCode();
            var release = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            var tag = release["tag_name"]?.GetValue<string>() ?? throw new Exception("Missing release tag");
            var asset = release["assets"]?.AsArray().FirstOrDefault(node =>
                node?["name"]?.GetValue<string>().EndsWith("-windows.zip", StringComparison.OrdinalIgnoreCase) == true)
                ?? throw new Exception("The release has no Windows package");
            var digest = asset["digest"]?.GetValue<string>();
            return (tag, asset["browser_download_url"]?.GetValue<string>() ?? throw new Exception("Missing asset URL"),
                    asset["size"]?.GetValue<long>() ?? -1,
                    digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // GitHub's API is commonly rate-limited. Resolve the stable release tag from the normal redirect instead.
            using var http = new HttpClient(await NetProxy.Handler(allowRedirect: false)) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("STS2CardForge/1.0");
            foreach (var latest in Setup.GithubUrls($"https://github.com/{Repo}/releases/latest"))
            {
                try
                {
                    using var response = await http.GetAsync(latest, HttpCompletionOption.ResponseHeadersRead, ct);
                    var location = response.Headers.Location?.ToString() ?? "";
                    var marker = "/releases/tag/";
                    var at = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;
                    var tag = Uri.UnescapeDataString(location[(at + marker.Length)..]);
                    var url = $"https://github.com/{Repo}/releases/download/{tag}/GDRE_tools-{tag}-windows.zip";
                    return (tag, url, -1, null);
                }
                catch (Exception inner) when (inner is not OperationCanceledException || !ct.IsCancellationRequested) { }
            }
            throw new Exception(L.Z("无法查询 GDRE Tools 的最新稳定版", "Could not find the latest stable GDRE Tools release"));
        }
    }

    public static async Task<string> Install(SetupStep progress, CancellationToken ct = default)
    {
        if (InstalledPath() is { } installed) return installed;
        progress.Report(L.Z("正在查询 GDRE Tools 稳定版…", "Finding the stable GDRE Tools release…"));
        var release = await Latest(ct);
        var versionDir = Path.Combine(Root, release.Tag);
        var zip = Path.Combine(Root, $"GDRE_tools-{release.Tag}-windows.zip");
        Directory.CreateDirectory(Root);
        if (!File.Exists(zip))
            await Setup.DownloadAny(Setup.GithubUrls(release.Url), zip, release.Size, release.Sha256, progress, ct);

        Setup.Log($"GDRE extracting {Path.GetFileName(zip)} -> {versionDir}");
        progress.Report(L.Z("正在解压 GDRE Tools… 0%", "Extracting GDRE Tools… 0%"), 75);
        var staging = Path.Combine(Root, release.Tag + ".installing");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zip);
                var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                long total = Math.Max(1, archive.Entries.Sum(e => e.Length));
                long done = 0;
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsafe path in GDRE archive");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                        Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, true);
                    }
                    done += entry.Length;
                    var pct = Math.Min(94, 75 + 19.0 * done / total);
                    progress.Report(L.Z($"正在解压 GDRE Tools… {done * 100 / total}%",
                                        $"Extracting GDRE Tools… {done * 100 / total}%"), pct);
                }
            }, ct);
            var exe = Directory.EnumerateFiles(staging, "gdre_tools.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new Exception(L.Z("安装包中没有 gdre_tools.exe", "The package contains no gdre_tools.exe"));
            progress.Report(L.Z("正在验证 GDRE Tools…", "Verifying GDRE Tools…"), 95);
            Setup.Log($"GDRE self-check {exe} --headless --version");
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            (int Code, string Output) check;
            try { check = await Setup.Run(exe, ["--headless", "--version"], staging, null, checkTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(L.Z("GDRE Tools 自检超过 20 秒，安装已停止；详情见日志。",
                                                "GDRE Tools self-check exceeded 20 seconds; installation stopped. See the log."));
            }
            // Some Windows GDRE release builds print a valid version and still return a non-zero Godot exit code
            // when stdout is redirected. Reaching the CLI and reporting its version is the self-check we need.
            var validVersion = check.Output.Contains("Godot RE Tools v", StringComparison.OrdinalIgnoreCase);
            if (check.Code != 0 && !validVersion)
                throw new Exception(L.Z("GDRE Tools 自检失败：", "GDRE Tools self-check failed: ") + check.Output);
            Setup.Log($"GDRE self-check OK (exit {check.Code}): {check.Output.Trim()}");
            if (Directory.Exists(versionDir)) Directory.Delete(versionDir, true);
            Directory.Move(staging, versionDir);
            File.Delete(zip);
            progress.Report(L.Z("GDRE Tools 安装完成。", "GDRE Tools installed."), 100);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
        return Directory.EnumerateFiles(versionDir, "gdre_tools.exe", SearchOption.AllDirectories).Single();
    }
}
