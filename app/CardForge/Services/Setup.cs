using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace CardForge.Services;

public enum StepState { Unknown, Ok, Missing, Working, Failed }

public class SetupStep : Observable
{
    StepState _state;
    string _detail = "";
    double _progress;
    bool _indeterminate;

    public required string Id { get; init; }
    public required string Title { get; init; }
    string _description = "";
    public required string Description { get => _description; set => Set(ref _description, value); }
    public required Func<Task<bool>> Check { get; init; }
    public bool Optional { get; init; }
    public required Func<SetupStep, CancellationToken, Task> Install { get; init; }

    public StepState State { get => _state; set { if (Set(ref _state, value)) { Raise(nameof(Glyph)); Raise(nameof(GlyphBrush)); Raise(nameof(Busy)); } } }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    public bool Busy => State == StepState.Working;

    public string Glyph => State switch
    {
        StepState.Ok => "",
        StepState.Missing when Optional => "",
        StepState.Missing => "",
        StepState.Failed => "",
        StepState.Working => "",
        _ => "",
    };

    public Brush GlyphBrush => new SolidColorBrush(State switch
    {
        StepState.Ok => ColorHelper.FromArgb(255, 80, 190, 110),
        StepState.Missing when Optional => ColorHelper.FromArgb(255, 120, 160, 220),
        StepState.Missing => ColorHelper.FromArgb(255, 230, 170, 60),
        StepState.Failed => ColorHelper.FromArgb(255, 230, 80, 70),
        _ => Colors.Gray,
    });

    /// <summary>Called from worker threads; marshals to the UI thread.</summary>
    public void Report(string detail, double progress = -1)
    {
        Setup.Ui?.TryEnqueue(() =>
        {
            Detail = detail;
            Indeterminate = progress < 0;
            if (progress >= 0) Progress = progress;
        });
    }
}

public record ModelFile(string Folder, string Name, string Repo, string RepoPath, long Size, string Sha256)
{
    public string Target => Path.Combine(AppPaths.Models, Folder, Name);
    public string Url(string hf) => $"{hf.TrimEnd('/')}/{Repo}/resolve/main/{RepoPath}";
    public bool Present => File.Exists(Target) && new FileInfo(Target).Length == Size;
}

public static class Setup
{
    public static Microsoft.UI.Dispatching.DispatcherQueue? Ui { get; set; }

    const string GgufRepo = "Abiray/Qwen-Image-2.1-GGUF";
    const string ComfyRepo = "Comfy-Org/Qwen-Image-2.1";
    const string LoraRepo = "Airmongsity/Qwen-Image-2.1-Sts2-Cards-Drawer";
    public const string LoraName = "deckbuilder_cardart_style_lora_v1_fp16.safetensors";

    /// <summary>GGUF quants of the image model, smallest first, with the VRAM (MiB) each is picked for.</summary>
    public static readonly (int MinVramMiB, ModelFile File)[] Quants =
    [
        (0,     new("diffusion_models", "qwen_image_2.1_Q3_K_M.gguf", GgufRepo, "qwen_image_2.1_Q3_K_M.gguf", 3185944736, "dc34f493dd56fdd665922163c94d51fbe28d2a1f61a941060e167b3a58b00167")),
        (5500,  new("diffusion_models", "qwen_image_2.1_Q4_K_M.gguf", GgufRepo, "qwen_image_2.1_Q4_K_M.gguf", 4189343904, "dc956c958fbfa1d5c64ec316d7e865283d17d97a9eb332a4a74a4d63afaae9a5")),
        (7000,  new("diffusion_models", "qwen_image_2.1_Q5_K_M.gguf", GgufRepo, "qwen_image_2.1_Q5_K_M.gguf", 5007397024, "97bc32c2c3d94b0dd88c8ccf41d7ac75897726424960b8236624f860cef5ad3b")),
        (10000, new("diffusion_models", "qwen_image_2.1_Q6_K.gguf", GgufRepo, "qwen_image_2.1_Q6_K.gguf", 5876578464, "9da289c709c32834c4aca2028a08f37fc866a5f9f11af8accdd93c0a806bbbad")),
        (14000, new("diffusion_models", "qwen_image_2.1_Q8_0.gguf", GgufRepo, "qwen_image_2.1_Q8_0.gguf", 7591579808, "cf1962b6c3ffd1d77ab8407f226f72073a333fc82ffe90e2337ad61a4053d47d")),
    ];

    public static readonly ModelFile TextEncoder = new("text_encoders", "qwen3vl_8b_w4a8.safetensors", ComfyRepo,
        "text_encoders/qwen3vl_8b_w4a8.safetensors", 6312105364, "7754425e55e7bea2bfde4dde59a4cc236cb44e5ee9c215ea66ef8d47012824eb");
    public static readonly ModelFile Vae = new("vae", "qwen_image_2.1_vae_bf16.safetensors", ComfyRepo,
        "vae/qwen_image_2.1_vae_bf16.safetensors", 675509688, "bb21f7473051e1ac368515dd3f2e15cd44d7a11748ee8823e1ddca3e4876b7c9");
    public static readonly ModelFile Lora = new("loras", LoraName, LoraRepo, LoraName, 100703552,
        "af780e9a292b658ab896a9b3d9fffb44df7d1c04839189c1fe1fd7cfb3159636");

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("STS2CardForge/1.0");
        return h;
    }

    public static string HfEndpoint => AppPaths.LoadSettings()["hf_endpoint"]?.GetValue<string>() is { Length: > 0 } e ? e : "https://huggingface.co";

    // ---- GPU ---------------------------------------------------------------------------------

    public static int? VramMiB { get; private set; }
    public static string GpuName { get; private set; } = "";

    public static async Task DetectGpu()
    {
        try
        {
            var (code, output) = await Run("nvidia-smi", ["--query-gpu=name,memory.total", "--format=csv,noheader,nounits"], null, null, default);
            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(',');
            if (code == 0 && first is { Length: 2 } && int.TryParse(first[1].Trim(), out var mib))
            {
                VramMiB = mib;
                GpuName = first[0].Trim();
            }
        }
        catch { VramMiB = null; }
    }

    public static ModelFile RecommendedQuant => Quants.Last(q => q.MinVramMiB <= (VramMiB ?? 8192)).File;

    /// <summary>The quant in use: the configured one if present, else any present quant, else the recommendation.</summary>
    public static ModelFile CurrentQuant
    {
        get
        {
            var configured = AppPaths.LoadSettings()["unet_file"]?.GetValue<string>();
            var match = Quants.Select(q => q.File).FirstOrDefault(f => f.Name == configured);
            if (match is { Present: true }) return match;
            var present = Quants.Select(q => q.File).Where(f => f.Present).ToList();
            if (present.Count > 0)
                return present.OrderBy(f => Math.Abs(f.Size - RecommendedQuant.Size)).First();
            return match ?? RecommendedQuant;
        }
    }

    public static void ChooseQuant(ModelFile f) => AppPaths.SaveSettings(s => s["unet_file"] = f.Name);

    // ---- steps -------------------------------------------------------------------------------

    public static List<SetupStep> CreateSteps() =>
    [
        new()
        {
            Id = "runtime",
            Title = L.Z("ComfyUI 运行环境", "ComfyUI runtime"),
            Description = L.Z("ComfyUI 便携版（自带 Python 与 CUDA 版 PyTorch，约 2 GB 下载）。已有 ComfyUI 可在下方选择其文件夹。",
                              "ComfyUI portable (bundles Python and CUDA PyTorch, ~2 GB download). Already have one? Pick its folder below."),
            Check = () => Task.FromResult(RuntimeOk(out _)),
            Install = InstallRuntime,
        },
        new()
        {
            Id = "gguf",
            Title = L.Z("ComfyUI-GGUF 节点", "ComfyUI-GGUF node"),
            Description = L.Z("加载 GGUF 量化模型所需的自定义节点。", "Custom node that loads the GGUF-quantized model."),
            Check = () => Task.FromResult(File.Exists(Path.Combine(AppPaths.ComfyDir, "custom_nodes", "ComfyUI-GGUF", "nodes.py"))),
            Install = InstallGguf,
        },
        new()
        {
            Id = "deps",
            Title = L.Z("后端依赖", "Backend packages"),
            Description = L.Z("安装到 ComfyUI 的 Python 中：anthropic（Claude 提示词生成）。", "Installed into ComfyUI's Python: anthropic (Claude prompt generation)."),
            Check = async () => File.Exists(AppPaths.Python) && (await Run(AppPaths.Python, ["-s", "-c", "import anthropic"], null, null, default)).Code == 0,
            Install = async (step, ct) =>
            {
                step.Report(L.Z("pip 安装中…", "pip installing…"));
                await Pip(step, ct, "-r", Path.Combine(AppPaths.Backend, "requirements.txt"));
            },
        },
        new()
        {
            Id = "unet",
            Title = L.Z("图像模型 Qwen-Image-2.1 (GGUF)", "Image model Qwen-Image-2.1 (GGUF)"),
            Description = "",
            Check = () =>
            {
                var q = CurrentQuant;
                if (q.Present) ChooseQuant(q);
                return Task.FromResult(q.Present);
            },
            Install = async (step, ct) =>
            {
                var q = CurrentQuant;
                await Download(q, step, ct);
                ChooseQuant(q);
            },
        },
        new()
        {
            Id = "te",
            Title = L.Z("文本编码器 Qwen3-VL 8B (w4a8)", "Text encoder Qwen3-VL 8B (w4a8)"),
            Description = $"{TextEncoder.Name} · {TextEncoder.Size / 1e9:0.0} GB",
            Check = () => Task.FromResult(TextEncoder.Present),
            Install = (step, ct) => Download(TextEncoder, step, ct),
        },
        new()
        {
            Id = "vae",
            Title = "VAE",
            Description = $"{Vae.Name} · {Vae.Size / 1e9:0.0} GB",
            Check = () => Task.FromResult(Vae.Present),
            Install = (step, ct) => Download(Vae, step, ct),
        },
        new()
        {
            Id = "lora",
            Title = L.Z("STS2 卡图风格 LoRA（可选，推荐）", "STS2 card-art style LoRA (optional, recommended)"),
            Description = L.Z($"{LoraRepo} · 100 MB。不安装则用基础模型出图。",
                              $"{LoraRepo} · 100 MB. Without it the base model is used."),
            Optional = true,
            Check = () => Task.FromResult(Lora.Present),
            Install = InstallLora,
        },
    ];

    public static bool RuntimeOk(out string problem)
    {
        problem = "";
        if (!File.Exists(AppPaths.Python) || !File.Exists(Path.Combine(AppPaths.ComfyDir, "main.py")))
        {
            problem = L.Z("未找到", "Not found") + ": " + AppPaths.ComfyRoot;
            return false;
        }
        var qwen = Path.Combine(AppPaths.ComfyDir, "comfy_extras", "nodes_qwen.py");
        if (!File.Exists(qwen) || !File.ReadAllText(qwen).Contains("TextEncodeQwenImage21"))
        {
            problem = L.Z("ComfyUI 版本过旧，不支持 Qwen-Image-2.1（安装将执行更新）", "ComfyUI is too old for Qwen-Image-2.1 (install will update it)");
            return false;
        }
        return true;
    }

    // ---- installers ----------------------------------------------------------------------------

    static async Task InstallRuntime(SetupStep step, CancellationToken ct)
    {
        if (File.Exists(AppPaths.Python) && File.Exists(Path.Combine(AppPaths.ComfyDir, "main.py")))
        {
            // present but outdated: run the portable package's own updater
            step.Report(L.Z("更新 ComfyUI…", "Updating ComfyUI…"));
            var updateDir = Path.Combine(AppPaths.ComfyRoot, "update");
            var (code, output) = await Run(AppPaths.Python, ["-s", "update.py", AppPaths.ComfyDir + Path.DirectorySeparatorChar], updateDir,
                line => step.Report(line), ct);
            if (code != 0) throw new Exception(L.Z("ComfyUI 更新失败：", "ComfyUI update failed: ") + Tail(output));
            return;
        }

        var root = AppPaths.Root;
        var archive = Path.Combine(root, "ComfyUI_windows_portable_nvidia.7z");
        if (!File.Exists(archive))
        {
            step.Report(L.Z("查询 ComfyUI 最新版本…", "Looking up the latest ComfyUI release…"));
            var rel = JsonNode.Parse(await Http.GetStringAsync("https://api.github.com/repos/comfyanonymous/ComfyUI/releases/latest", ct))!;
            var asset = rel["assets"]!.AsArray().FirstOrDefault(a => a!["name"]!.GetValue<string>() == "ComfyUI_windows_portable_nvidia.7z")
                        ?? throw new Exception("ComfyUI_windows_portable_nvidia.7z not found in the latest release");
            await DownloadFile(asset["browser_download_url"]!.GetValue<string>(), archive, asset["size"]!.GetValue<long>(), null, step, ct);
        }

        var sevenZip = Path.Combine(AppPaths.Tools, "7zr.exe");
        if (!File.Exists(sevenZip))
        {
            Directory.CreateDirectory(AppPaths.Tools);
            step.Report(L.Z("下载 7-Zip 解压工具…", "Downloading 7-Zip…"));
            await File.WriteAllBytesAsync(sevenZip, await Http.GetByteArrayAsync("https://www.7-zip.org/a/7zr.exe", ct), ct);
        }

        step.Report(L.Z("解压中（约 1-3 分钟）…", "Extracting (1-3 minutes)…"), 0);
        var (xcode, xout) = await Run(sevenZip, ["x", archive, "-o" + root, "-y", "-bsp1"], root, line =>
        {
            var pct = line.Trim().Split('%')[0];
            if (int.TryParse(pct, out var p)) step.Report(L.Z("解压中… ", "Extracting… ") + p + "%", p);
        }, ct);
        if (xcode != 0) throw new Exception(L.Z("解压失败：", "Extraction failed: ") + Tail(xout));
        AppPaths.ComfyRoot = Path.Combine(root, "ComfyUI_windows_portable");
        if (!RuntimeOk(out var problem)) throw new Exception(problem);
    }

    static async Task InstallGguf(SetupStep step, CancellationToken ct)
    {
        var customNodes = Path.Combine(AppPaths.ComfyDir, "custom_nodes");
        var target = Path.Combine(customNodes, "ComfyUI-GGUF");
        if (!File.Exists(Path.Combine(target, "nodes.py")))
        {
            step.Report(L.Z("下载 ComfyUI-GGUF…", "Downloading ComfyUI-GGUF…"));
            var zip = await Http.GetByteArrayAsync("https://github.com/city96/ComfyUI-GGUF/archive/refs/heads/main.zip", ct);
            var tmp = Path.Combine(AppPaths.Data, "gguf_tmp");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(new MemoryStream(zip), tmp);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(Directory.GetDirectories(tmp).Single(), target);
            Directory.Delete(tmp, true);
        }
        step.Report(L.Z("pip 安装 gguf…", "pip installing gguf…"));
        await Pip(step, ct, "gguf>=0.13.0");
    }

    static async Task InstallLora(SetupStep step, CancellationToken ct)
    {
        var bundled = Path.Combine(AppPaths.BundledLora, LoraName);
        if (File.Exists(bundled) && new FileInfo(bundled).Length == Lora.Size)
        {
            step.Report(L.Z("复制内置 LoRA…", "Copying bundled LoRA…"));
            Directory.CreateDirectory(Path.GetDirectoryName(Lora.Target)!);
            await Task.Run(() => File.Copy(bundled, Lora.Target, true), ct);
            return;
        }
        await Download(Lora, step, ct);
    }

    static async Task Pip(SetupStep step, CancellationToken ct, params string[] packages)
    {
        var args = new List<string> { "-s", "-m", "pip", "install", "--disable-pip-version-check", "--no-warn-script-location" };
        args.AddRange(packages);
        var (code, output) = await Run(AppPaths.Python, args, AppPaths.ComfyRoot, line => step.Report(line), ct);
        if (code != 0) throw new Exception("pip: " + Tail(output));
    }

    // ---- downloads -----------------------------------------------------------------------------

    public static Task Download(ModelFile f, SetupStep step, CancellationToken ct) =>
        DownloadFile(f.Url(HfEndpoint), f.Target, f.Size, f.Sha256, step, ct);

    /// <summary>Resumable download to "&lt;dst&gt;.part", SHA-256 verified, then renamed into place.</summary>
    public static async Task DownloadFile(string url, string dst, long size, string? sha256, SetupStep step, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var part = dst + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > size) { File.Delete(part); have = 0; }

        if (have < size)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (have > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server ignored Range

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var buf = new byte[1 << 20];
            long done = have, lastBytes = have;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(300))
                {
                    var speed = (done - lastBytes) / (clock.Elapsed - lastReport).TotalSeconds;
                    lastReport = clock.Elapsed;
                    lastBytes = done;
                    step.Report($"{Path.GetFileName(dst)}  {done / 1e9:0.00} / {size / 1e9:0.00} GB  ·  {speed / 1e6:0.0} MB/s", 100.0 * done / size);
                }
            }
        }

        if (new FileInfo(part).Length != size)
            throw new Exception(L.Z("下载不完整，请重试（会断点续传）", "Download incomplete; retry to resume"));
        if (sha256 != null)
        {
            step.Report(L.Z("校验 SHA-256…", "Verifying SHA-256…"));
            var actual = await Task.Run(() =>
            {
                using var s = File.OpenRead(part);
                return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            }, ct);
            if (actual != sha256)
            {
                File.Delete(part);
                throw new Exception(L.Z("校验失败，文件已删除，请重新下载", "Checksum mismatch; the file was deleted, download again"));
            }
        }
        File.Move(part, dst, true);
    }

    public static string ManualLinks()
    {
        var sb = new StringBuilder();
        foreach (var f in new[] { CurrentQuant, TextEncoder, Vae, Lora })
            sb.AppendLine(f.Url(HfEndpoint)).AppendLine("    -> " + f.Target);
        return sb.ToString();
    }

    // ---- processes -----------------------------------------------------------------------------

    public static async Task<(int Code, string Output)> Run(string file, IEnumerable<string> args, string? workDir,
        Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUTF8"] = "1";
        var output = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        void Handle(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (output) output.AppendLine(line);
            onLine?.Invoke(line.Trim());
        }
        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived += (_, e) => Handle(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { p.Kill(true); throw; }
        return (p.ExitCode, output.ToString());
    }

    static string Tail(string s) => s.Length > 600 ? s[^600..] : s;
}
