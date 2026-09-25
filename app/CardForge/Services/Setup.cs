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

/// <param name="OnModelScope">The same files (identical SHA-256) are mirrored under the same repo name on ModelScope.</param>
public record ModelFile(string Folder, string Name, string Repo, string RepoPath, long Size, string Sha256, bool OnModelScope = true)
{
    public string Target => Path.Combine(AppPaths.Models, Folder, Name);
    public bool Present => File.Exists(Target) && new FileInfo(Target).Length == Size;

    /// <summary>Download URLs in the order to try them for the current download source.</summary>
    public IEnumerable<string> Urls()
    {
        var hf = $"https://huggingface.co/{Repo}/resolve/main/{RepoPath}";
        var ms = OnModelScope ? $"https://modelscope.cn/models/{Repo}/resolve/master/{RepoPath}" : null;
        // hf-mirror.com only mirrors metadata: big files still redirect to HuggingFace's CDN, so it goes last
        var mirror = $"https://hf-mirror.com/{Repo}/resolve/main/{RepoPath}";
        string?[] order = Setup.China ? [ms, hf, mirror] : [hf, ms, mirror];
        return order.OfType<string>();
    }
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
        "af780e9a292b658ab896a9b3d9fffb44df7d1c04839189c1fe1fd7cfb3159636");   // ModelScope: tried, used once a mirror exists

    static HttpClient? _http;
    static Uri? _httpProxy;

    /// <summary>Download client, rebuilt when the proxy (NetProxy) changes.</summary>
    static async Task<HttpClient> Client()
    {
        var (proxy, _) = await NetProxy.Resolve();
        if (_http == null || _httpProxy != proxy)
        {
            _http = new HttpClient(await NetProxy.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("STS2CardForge/1.0");
            _httpProxy = proxy;
        }
        return _http;
    }

    // ---- download sources ----------------------------------------------------------------------

    /// <summary>auto | global (HuggingFace, GitHub, PyPI first) | china (ModelScope, PyPI mirrors first).
    /// Every download falls back to the other sources automatically.</summary>
    public static string Source => AppPaths.LoadSettings()["download_source"]?.GetValue<string>() switch
    {
        "global" => "global",
        "china" => "china",
        _ => "auto",
    };

    public static bool China => Source == "china" || (Source == "auto" && InChina());

    static bool InChina()
    {
        try
        {
            return System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName == "CN" ||
                   TimeZoneInfo.Local.Id == "China Standard Time";
        }
        catch { return false; }
    }

    /// <summary>Optional prefix for GitHub downloads (a GitHub download proxy), e.g. "https://ghfast.top/".</summary>
    public static string GithubProxy => AppPaths.LoadSettings()["github_proxy"]?.GetValue<string>()?.Trim() ?? "";

    public static IEnumerable<string> GithubUrls(string url)
    {
        if (GithubProxy.Length == 0) return [url];
        var proxied = GithubProxy.TrimEnd('/') + "/" + url;
        return China ? [proxied, url] : [url, proxied];
    }

    static readonly string[] PipIndexes = ["https://pypi.tuna.tsinghua.edu.cn/simple", "https://mirrors.aliyun.com/pypi/simple/"];

    static IEnumerable<string?> PipOrder() => China ? [.. PipIndexes, null] : [null, .. PipIndexes];   // null = pypi.org

    // ---- GPU ---------------------------------------------------------------------------------

    public static ModelFile RecommendedQuant =>
        Gpu.Package == "cpu" ? Quants[1].File : Quants.Last(q => q.MinVramMiB <= (Gpu.Info.VramMiB ?? 8192)).File;

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
            Description = L.Z("ComfyUI 便携版（自带 Python 与对应显卡的 PyTorch，约 2 GB 下载）。运行包按显卡自动选择，也可在上方手动指定。",
                              "ComfyUI portable (bundles Python and PyTorch for your GPU, ~2 GB download). The package follows your GPU; override it above."),
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
            Id = "gputest",
            Title = L.Z("显卡自检", "GPU self-test"),
            Description = L.Z("用 ComfyUI 的 PyTorch 在显卡上做一次小运算，确认驱动、运行包与显卡相互兼容。",
                              "Runs a small computation on the GPU with ComfyUI's PyTorch to confirm the driver, package and card work together."),
            Check = async () =>
            {
                if (!File.Exists(AppPaths.Python)) return false;
                if (AppPaths.LoadSettings()["gpu_test_ok"]?.GetValue<string>() == GpuTestKey) return true;
                try { await GpuSelfTest(null, default); return true; }   // first check on this runtime: just run it (a few seconds)
                catch { return false; }
            },
            Install = GpuSelfTest,
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
        if (!PackageFits(out var have))
        {
            problem = L.Z($"现有 ComfyUI 的 PyTorch 是 {have} 版本，不适用于所选运行包 {Gpu.Package}（安装将下载新的运行包）",
                          $"The existing ComfyUI has a {have} PyTorch, which does not fit the selected {Gpu.Package} package (install downloads the new one)");
            return false;
        }
        return true;
    }

    /// <summary>The package the current ComfyUI's PyTorch was built for, read from torch/version.py
    /// ("2.x+cu130" = nvidia, "+cu12x" = nvidia_cu126, "+rocm"/hip = amd, "+xpu" = intel, "+cpu" = cpu).</summary>
    static string? InstalledPackage
    {
        get
        {
            try
            {
                var v = File.ReadAllText(Path.Combine(AppPaths.ComfyRoot, "python_embeded", "Lib", "site-packages", "torch", "version.py"));
                var ver = System.Text.RegularExpressions.Regex.Match(v, @"__version__\s*=\s*'([^']*)'").Groups[1].Value;
                if (ver.Contains("+cu1") && !ver.Contains("+cu12") && !ver.Contains("+cu11")) return "nvidia";
                if (ver.Contains("+cu")) return "nvidia_cu126";
                if (ver.Contains("rocm") || System.Text.RegularExpressions.Regex.IsMatch(v, @"hip\s*[:=][^\n]*'\d")) return "amd";
                if (ver.Contains("xpu")) return "intel";
                if (ver.Contains("+cpu")) return "cpu";
            }
            catch { }
            return null;
        }
    }

    /// <summary>Whether the installed PyTorch can drive the selected package's GPU. A newer card on a CUDA 12 build is
    /// fine; a card older than sm_75 on the CUDA 13 build or a different vendor is not.</summary>
    static bool PackageFits(out string have)
    {
        have = InstalledPackage ?? "?";
        var want = Gpu.Package;
        if (InstalledPackage is null || want == "cpu" || have == want) return true;
        if (want.StartsWith("nvidia") && have.StartsWith("nvidia")) return !(have == "nvidia" && Gpu.Info.ComputeCap is < 7.5);
        return false;
    }

    static string GpuTestKey => $"{AppPaths.ComfyRoot}|{Gpu.Package}";

    // ---- GPU self-test ---------------------------------------------------------------------------

    const string GpuTestScript = """
        import sys, torch
        want = sys.argv[1]
        if want == "cpu":
            dev = "cpu"
        elif hasattr(torch, "xpu") and torch.xpu.is_available():
            dev = "xpu"
        elif torch.cuda.is_available():
            dev = "cuda"
        else:
            print("NO_GPU torch", torch.__version__, "cuda", torch.version.cuda, "hip", getattr(torch.version, "hip", None))
            sys.exit(3)
        x = torch.randn(512, 512, device=dev, dtype=torch.float16 if dev != "cpu" else torch.float32)
        v = (x @ x).float().sum().item()
        name = {"cuda": lambda: torch.cuda.get_device_name(0), "xpu": lambda: torch.xpu.get_device_name(0)}.get(dev, lambda: "CPU")()
        print("OK", dev, name, "torch", torch.__version__)
        """;

    static async Task GpuSelfTest(SetupStep? step, CancellationToken ct)
    {
        step?.Report(L.Z("加载 PyTorch 并在显卡上运算…", "Loading PyTorch and computing on the GPU…"));
        var script = Path.Combine(AppPaths.Data, "gpu_test.py");
        Directory.CreateDirectory(AppPaths.Data);
        await File.WriteAllTextAsync(script, GpuTestScript, ct);
        var (code, output) = await Run(AppPaths.Python, ["-s", script, Gpu.Package], AppPaths.ComfyRoot, null, ct);
        var ok = output.Split('\n').FirstOrDefault(l => l.StartsWith("OK "));
        if (code == 0 && ok != null)
        {
            AppPaths.SaveSettings(s => s["gpu_test_ok"] = GpuTestKey);
            step?.Report(ok[3..].Trim(), 100);
            return;
        }
        throw new Exception(GpuTestHint(output) + "\n" + Tail(output.Trim(), 300));
    }

    static string GpuTestHint(string output)
    {
        var o = output.ToLowerInvariant();
        var pkg = Gpu.Package;
        if (o.Contains("no kernel image") || o.Contains("not compatible with the current pytorch") || o.Contains("invalid device function")
            || o.Contains("hipErrorNoBinaryForGpu".ToLowerInvariant()))
            return pkg == "nvidia"
                ? L.Z("这张显卡太旧，CUDA 13 运行包不支持：请在上方把运行包改为“NVIDIA 旧卡”，再安装运行环境。",
                      "This card is too old for the CUDA 13 package: set the package above to \"NVIDIA legacy\" and install the runtime again.")
                : L.Z("PyTorch 不支持这张显卡的架构（AMD 需 RX 6000 及更新，Intel 需 Arc）。可改用“仅 CPU”运行包（很慢）。",
                      "PyTorch does not support this card's architecture (AMD needs RX 6000 or newer, Intel needs Arc). The CPU package works, slowly.");
        if (o.Contains("driver") && (o.Contains("insufficient") || o.Contains("too old") || o.Contains("older than")))
            return L.Z("显卡驱动过旧：请安装最新的显卡驱动后点“重新检查”。", "The graphics driver is too old: install the latest driver, then click Re-check.");
        if (o.Contains("out of memory"))
            return L.Z("显存已被其他程序占满：关闭游戏或其他 AI 程序后重试。", "VRAM is full: close games or other AI apps and retry.");
        if (o.Contains("no_gpu"))
            return pkg switch
            {
                "amd" => L.Z("PyTorch 找不到 AMD 显卡：需要 Windows 11、RX 6000 或更新的显卡，以及最新的 AMD Adrenalin 驱动。",
                             "PyTorch cannot see the AMD GPU: it needs Windows 11, an RX 6000 or newer card and the latest AMD Adrenalin driver."),
                "intel" => L.Z("PyTorch 找不到 Intel Arc 显卡：请安装最新的 Intel Arc 驱动。", "PyTorch cannot see the Intel Arc GPU: install the latest Intel Arc driver."),
                _ => L.Z("PyTorch 找不到 NVIDIA 显卡：请安装/更新 NVIDIA 驱动，并确认运行包与显卡品牌一致。",
                         "PyTorch cannot see an NVIDIA GPU: install or update the NVIDIA driver and make sure the package matches your GPU brand."),
            };
        return L.Z("显卡自检失败，请检查驱动与运行包是否匹配：", "The GPU self-test failed; check that the driver and package match:");
    }

    // ---- installers ----------------------------------------------------------------------------

    const string ComfyReleases = "https://github.com/Comfy-Org/ComfyUI/releases/latest/download/";

    static async Task InstallRuntime(SetupStep step, CancellationToken ct)
    {
        var package = Gpu.Package;
        if (File.Exists(AppPaths.Python) && File.Exists(Path.Combine(AppPaths.ComfyDir, "main.py")) && PackageFits(out _))
        {
            // present but outdated: run the portable package's own updater (needs GitHub)
            step.Report(L.Z("更新 ComfyUI…", "Updating ComfyUI…"));
            var updateDir = Path.Combine(AppPaths.ComfyRoot, "update");
            var (code, output) = await Run(AppPaths.Python, ["-s", "update.py", AppPaths.ComfyDir + Path.DirectorySeparatorChar], updateDir,
                line => step.Report(line), ct);
            if (code != 0)
                throw new Exception(L.Z("ComfyUI 更新失败（需要能访问 GitHub），也可以删除旧的 ComfyUI 文件夹后重新安装：",
                                        "ComfyUI update failed (needs GitHub access); you can also delete the old ComfyUI folder and install again: ") + Tail(output));
            return;
        }

        var root = AppPaths.Root;
        var name = Gpu.ArchiveName(package);
        var archive = Path.Combine(root, name);
        if (!File.Exists(archive))
            await DownloadAny(GithubUrls(ComfyReleases + name), archive, -1, null, step, ct);

        var sevenZip = Path.Combine(AppPaths.Tools, "7zr.exe");
        if (!File.Exists(sevenZip))
        {
            step.Report(L.Z("下载 7-Zip 解压工具…", "Downloading 7-Zip…"));
            await DownloadAny(["https://www.7-zip.org/a/7zr.exe", .. GithubUrls("https://github.com/ip7z/7zip/releases/latest/download/7zr.exe")],
                sevenZip, -1, null, step, ct);
        }

        // extract into a scratch folder first: packages differ in their top folder name, and a different
        // package must not be unpacked over an existing ComfyUI
        var tmp = Path.Combine(root, "_comfy_extract");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        step.Report(L.Z("解压中（约 1-3 分钟）…", "Extracting (1-3 minutes)…"), 0);
        var (xcode, xout) = await Run(sevenZip, ["x", archive, "-o" + tmp, "-y", "-bsp1"], root, line =>
        {
            var pct = line.Trim().Split('%')[0];
            if (int.TryParse(pct, out var p)) step.Report(L.Z("解压中… ", "Extracting… ") + p + "%", p);
        }, ct);
        if (xcode != 0)
        {
            File.Delete(archive);   // most likely a damaged download; fetch it again next time
            throw new Exception(L.Z("解压失败（压缩包已删除，请重新安装）：", "Extraction failed (the archive was deleted; install again): ") + Tail(xout));
        }
        var extracted = Directory.GetDirectories(tmp).FirstOrDefault(d => File.Exists(Path.Combine(d, "python_embeded", "python.exe")))
                        ?? throw new Exception(L.Z("压缩包里没有 ComfyUI 便携版", "The archive holds no ComfyUI portable folder"));
        var target = Path.Combine(root, "ComfyUI_windows_portable");
        if (Directory.Exists(target)) target += "_" + package;
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(extracted, target);
        Directory.Delete(tmp, true);
        File.Delete(archive);

        AppPaths.ComfyRoot = target;
        if (!RuntimeOk(out var problem)) throw new Exception(problem);
    }

    static async Task InstallGguf(SetupStep step, CancellationToken ct)
    {
        var customNodes = Path.Combine(AppPaths.ComfyDir, "custom_nodes");
        var target = Path.Combine(customNodes, "ComfyUI-GGUF");
        if (!File.Exists(Path.Combine(target, "nodes.py")))
        {
            var zip = Path.Combine(AppPaths.Data, "ComfyUI-GGUF.zip");
            if (File.Exists(zip)) File.Delete(zip);
            await DownloadAny(GithubUrls("https://github.com/city96/ComfyUI-GGUF/archive/refs/heads/main.zip"), zip, -1, null, step, ct);
            var tmp = Path.Combine(AppPaths.Data, "gguf_tmp");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zip, tmp);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(Directory.GetDirectories(tmp).Single(), target);
            Directory.Delete(tmp, true);
            File.Delete(zip);
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

    /// <summary>pip install, trying PyPI and its mirrors in the order that suits the download source.</summary>
    static async Task Pip(SetupStep step, CancellationToken ct, params string[] packages)
    {
        var errors = new List<string>();
        foreach (var index in PipOrder())
        {
            var args = new List<string> { "-s", "-m", "pip", "install", "--disable-pip-version-check", "--no-warn-script-location", "--timeout", "30" };
            if (index != null) args.AddRange(["-i", index, "--trusted-host", new Uri(index).Host]);
            args.AddRange(packages);
            var (code, output) = await Run(AppPaths.Python, args, AppPaths.ComfyRoot, line => step.Report(line), ct);
            if (code == 0) return;
            errors.Add($"[{index ?? "pypi.org"}] {Tail(output, 200)}");
            Log($"pip [{index ?? "pypi.org"}] failed: {Tail(output, 400)}");
            step.Report(L.Z("换一个 PyPI 源重试…", "Retrying with another PyPI index…"));
        }
        throw new Exception("pip: " + string.Join("\n", errors));
    }

    // ---- downloads -----------------------------------------------------------------------------

    public static Task Download(ModelFile f, SetupStep step, CancellationToken ct) =>
        DownloadAny(f.Urls(), f.Target, f.Size, f.Sha256, step, ct);

    /// <summary>Tries each source in turn. Sources serve identical bytes, so a partial download resumes on the next one.</summary>
    public static async Task DownloadAny(IEnumerable<string> urls, string dst, long size, string? sha256, SetupStep step, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var url in urls)
        {
            try
            {
                Log($"GET {url}");
                await DownloadFile(url, dst, size, sha256, step, ct);
                Log($"OK  {Path.GetFileName(dst)} from {new Uri(url).Host}");
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Log($"ERR {new Uri(url).Host}: {e.GetType().Name}: {e.Message}{(e.InnerException != null ? " / " + e.InnerException.Message : "")}");
                errors.Add($"{new Uri(url).Host}: {e.Message}");
                step.Report(L.Z($"{new Uri(url).Host} 不可用，切换下一个下载源…", $"{new Uri(url).Host} failed, trying the next source…"));
            }
        }
        throw new Exception(L.Z("所有下载源都失败了。可在上方切换下载源 / 填写 GitHub 代理，或“复制下载链接”手动下载：\n",
                                "Every download source failed. Switch the download source / set a GitHub proxy above, or use Copy download links:\n")
                            + string.Join("\n", errors));
    }

    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>data/logs/setup.log: every download attempt and failure, for users to send when downloads fail.</summary>
    public static string LogFile => Path.Combine(AppPaths.Logs, "setup.log");

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            lock (LogFile) File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { }
    }

    // testing a restricted network on an unrestricted machine: CARDFORGE_BLOCK_HOSTS=huggingface.co,github.com sends
    // direct requests for those hosts (and subdomains) to a black hole, so they hang like a DNS-poisoned or firewalled
    // host; requests through a proxy are left alone, as a real proxy would get around the block
    static readonly string[] SimulatedBlocked = (Environment.GetEnvironmentVariable("CARDFORGE_BLOCK_HOSTS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string Simulate(string url)
    {
        var host = new Uri(url).Host;
        return SimulatedBlocked.Any(b => host == b || host.EndsWith("." + b)) ? "http://10.255.255.1/" : url;
    }

    /// <summary>Resumable download to "&lt;dst&gt;.part", SHA-256 verified, then renamed into place.
    /// size &lt;= 0 means unknown: the size is taken from the response. Gives up after 30 s without data.</summary>
    public static async Task DownloadFile(string url, string dst, long size, string? sha256, SetupStep step, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var part = dst + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (size > 0 && have > size) { File.Delete(part); have = 0; }

        if (size <= 0 || have < size)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            try
            {
                var http = await Client();
                var req = new HttpRequestMessage(HttpMethod.Get, _httpProxy == null ? Simulate(url) : url);
                if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && size <= 0)
                    size = have;    // the .part file is already complete
                else
                {
                    resp.EnsureSuccessStatusCode();
                    if (have > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server ignored Range
                    if (size <= 0)
                        size = resp.Content.Headers.ContentRange?.Length ?? (resp.Content.Headers.ContentLength is long len ? have + len : -1);

                    await using var src = await resp.Content.ReadAsStreamAsync(stall.Token);
                    await using var fs = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    var buf = new byte[1 << 20];
                    long done = have, lastBytes = have;
                    var clock = Stopwatch.StartNew();
                    var lastReport = TimeSpan.Zero;
                    int n;
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout);
                        if ((n = await src.ReadAsync(buf, stall.Token)) <= 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(300))
                        {
                            var speed = (done - lastBytes) / (clock.Elapsed - lastReport).TotalSeconds;
                            lastReport = clock.Elapsed;
                            lastBytes = done;
                            var total = size > 0 ? $" / {size / 1e9:0.00}" : "";
                            step.Report($"{Path.GetFileName(dst)}  {done / 1e9:0.00}{total} GB  ·  {speed / 1e6:0.0} MB/s  ·  {new Uri(url).Host}",
                                size > 0 ? 100.0 * done / size : -1);
                        }
                    }
                    if (size <= 0) size = done;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(L.Z("30 秒没有收到数据", "no data for 30 seconds"));
            }
        }

        if (!File.Exists(part) || new FileInfo(part).Length != size)
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
        var name = Gpu.ArchiveName(Gpu.Package);
        sb.AppendLine(ComfyReleases + name).AppendLine(L.Z("    -> 用 7-Zip 解压，然后点“选择已有 ComfyUI…”", "    -> extract with 7-Zip, then use \"Use existing ComfyUI…\""));
        foreach (var f in new[] { CurrentQuant, TextEncoder, Vae, Lora })
        {
            foreach (var u in f.Urls()) sb.AppendLine(u);
            sb.AppendLine("    -> " + f.Target);
        }
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
        await NetProxy.Apply(psi);
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

    static string Tail(string s, int n = 600) => s.Length > n ? s[^n..] : s;
}
