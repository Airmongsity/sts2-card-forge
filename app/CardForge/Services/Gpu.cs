using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CardForge.Services;

public enum GpuVendor { None, Nvidia, Amd, Intel, Other }

public sealed record GpuInfo(GpuVendor Vendor, string Name, int? VramMiB, double? ComputeCap, double? Driver);

/// <summary>Detects the graphics card and picks the matching ComfyUI portable package:
/// nvidia (CUDA 13, RTX 20 / GTX 16 series and newer), nvidia_cu126 (GTX 10 series and older),
/// amd (ROCm, RDNA 2 and newer), intel (Arc) or cpu (anything else; works, but extremely slowly).</summary>
public static class Gpu
{
    public static GpuInfo Info { get; private set; } = new(GpuVendor.None, "", null, null, null);

    // CUDA 13.0 needs an R580+ driver; the CUDA 12.x build runs on R528+ (minor-version compatibility)
    const double MinDriverCu130 = 580, MinDriverCu126 = 528;

    public static readonly string[] Packages = ["nvidia", "nvidia_cu126", "amd", "intel", "cpu"];

    public static async Task Detect()
    {
        try
        {
            var (code, output) = await Setup.Run("nvidia-smi",
                ["--query-gpu=name,memory.total,compute_cap,driver_version", "--format=csv,noheader,nounits"], null, null, default);
            var f = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(',').Select(x => x.Trim()).ToArray();
            if (code == 0 && f is { Length: 4 })
            {
                Info = new(GpuVendor.Nvidia, f[0], int.TryParse(f[1], out var mib) ? mib : null,
                    double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cc) ? cc : null,
                    double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var drv) ? drv : null);
                return;
            }
        }
        catch { /* no nvidia-smi: not an NVIDIA machine, or no driver */ }

        var adapters = Adapters();
        Info = adapters.OrderByDescending(Rank).ThenByDescending(a => a.VramMiB ?? 0).FirstOrDefault() ?? Info;
    }

    static int Rank(GpuInfo a) => a.Vendor switch
    {
        GpuVendor.Nvidia => 4,
        GpuVendor.Amd when AmdSupported(a.Name) => 3,
        GpuVendor.Intel when IsArc(a.Name) => 2,
        GpuVendor.Amd or GpuVendor.Intel => 1,
        _ => 0,
    };

    /// <summary>Display adapters from the registry (works for every vendor, including VRAM above 4 GB).</summary>
    static List<GpuInfo> Adapters()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k == null) continue;
                    var name = k.GetValue("DriverDesc") as string ?? "";
                    var dev = (k.GetValue("MatchingDeviceId") as string ?? "").ToLowerInvariant();
                    var vendor = dev.Contains("ven_10de") ? GpuVendor.Nvidia : dev.Contains("ven_1002") ? GpuVendor.Amd
                               : dev.Contains("ven_8086") ? GpuVendor.Intel : GpuVendor.Other;
                    long bytes = k.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => l,
                        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b),
                        _ => 0,
                    };
                    if (bytes == 0)
                        bytes = k.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i => (uint)i,
                            long l => l,
                            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b),
                            _ => 0,
                        };
                    if (name.Length > 0 && vendor != GpuVendor.Other)
                        list.Add(new(vendor, name, bytes > 0 ? (int)(bytes / 1048576) : null, null, null));
                }
                catch { /* unreadable adapter key */ }
            }
        }
        catch { }
        return list;
    }

    static bool IsArc(string name) => name.Contains("Arc", StringComparison.OrdinalIgnoreCase);

    /// <summary>ROCm on Windows supports RDNA 2 and newer (RX 6000 / 7000 / 9000, Radeon AI PRO, Ryzen AI iGPUs).</summary>
    static bool AmdSupported(string name) =>
        Regex.IsMatch(name, @"RX\s*(6[4-9]\d\d|7\d{3}|9\d{3})|AI\s*PRO|PRO\s*W[67]\d{3}|\b(8060S|8050S|890M|880M|780M|760M|740M|680M|660M)\b",
            RegexOptions.IgnoreCase);

    public static string AutoPackage => Info.Vendor switch
    {
        GpuVendor.Nvidia when Info.ComputeCap is < 5.0 => "cpu",
        GpuVendor.Nvidia => Info.ComputeCap is < 7.5 ? "nvidia_cu126" : "nvidia",
        GpuVendor.Amd when AmdSupported(Info.Name) => "amd",
        GpuVendor.Intel when IsArc(Info.Name) => "intel",
        _ => "cpu",
    };

    /// <summary>The package in use: the user's choice in Setup, or the automatic one.</summary>
    public static string Package =>
        AppPaths.LoadSettings()["gpu_package_override"]?.GetValue<string>() is { } p && Packages.Contains(p) ? p : AutoPackage;

    public static string ArchiveName(string package) =>
        $"ComfyUI_windows_portable_{(package == "cpu" ? "nvidia" : package)}.7z";

    public static string PackageLabel(string package) => package switch
    {
        "nvidia" => L.Z("NVIDIA（CUDA 13，RTX 20 / GTX 16 系及更新）", "NVIDIA (CUDA 13, RTX 20 / GTX 16 series and newer)"),
        "nvidia_cu126" => L.Z("NVIDIA 旧卡（CUDA 12.6，GTX 10 系及更早）", "NVIDIA legacy (CUDA 12.6, GTX 10 series and older)"),
        "amd" => L.Z("AMD（ROCm，RX 6000/7000/9000 等 RDNA 2+）", "AMD (ROCm, RDNA 2+: RX 6000/7000/9000)"),
        "intel" => L.Z("Intel Arc（XPU）", "Intel Arc (XPU)"),
        _ => L.Z("仅 CPU（极慢，一张图可能要数小时）", "CPU only (extremely slow, hours per image)"),
    };

    /// <summary>Problems with this machine for the chosen package. Severe ones mean images will likely not generate
    /// (or only on the CPU), so Setup asks before downloading anything big.</summary>
    public static List<(string Text, bool Severe)> Warnings(string package)
    {
        var w = new List<(string, bool)>();
        var i = Info;
        void Add(bool severe, string zh, string en) => w.Add((L.Z(zh, en), severe));

        var vendorOfPackage = package switch
        {
            "nvidia" or "nvidia_cu126" => GpuVendor.Nvidia,
            "amd" => GpuVendor.Amd,
            "intel" => GpuVendor.Intel,
            _ => GpuVendor.None,
        };
        if (vendorOfPackage != GpuVendor.None && i.Vendor != vendorOfPackage)
            Add(true, $"所选运行包与检测到的显卡（{(i.Vendor == GpuVendor.None ? "无" : i.Name)}）品牌不符。",
                      $"The selected package does not match the detected GPU ({(i.Vendor == GpuVendor.None ? "none" : i.Name)}).");
        if (package == "nvidia" && i.Vendor == GpuVendor.Nvidia && i.Driver is < MinDriverCu130)
            Add(true, $"NVIDIA 驱动 {i.Driver} 过旧：CUDA 13 需要 580 或更新版本，请先到 nvidia.cn 更新驱动。",
                      $"NVIDIA driver {i.Driver} is too old: CUDA 13 needs 580 or newer. Update it from nvidia.com first.");
        if (package == "nvidia_cu126" && i.Vendor == GpuVendor.Nvidia && i.Driver is < MinDriverCu126)
            Add(true, $"NVIDIA 驱动 {i.Driver} 过旧：请先更新到 528 或更新版本。", $"NVIDIA driver {i.Driver} is too old: update to 528 or newer first.");
        if (package == "nvidia" && i.ComputeCap is < 7.5)
            Add(true, "这张显卡（GTX 10 系或更早）不被 CUDA 13 包支持，请选“NVIDIA 旧卡”运行包。",
                      "This card (GTX 10 series or older) is not supported by the CUDA 13 package; choose \"NVIDIA legacy\".");
        if (package == "nvidia_cu126" && i.ComputeCap is >= 7.5)
            Add(false, "RTX 20 / GTX 16 系及更新的显卡请使用标准 NVIDIA 包（ComfyUI 官方不建议在新卡上用 CUDA 12.6 包）。",
                       "RTX 20 / GTX 16 series and newer should use the standard NVIDIA package (ComfyUI advises against CUDA 12.6 on them).");
        if (i.Vendor == GpuVendor.Nvidia && i.Driver == null && package.StartsWith("nvidia"))
            Add(true, "检测到 NVIDIA 显卡但没有 nvidia-smi：请先安装 NVIDIA 显卡驱动。", "NVIDIA card found but no nvidia-smi: install the NVIDIA driver first.");
        if (package == "amd")
        {
            if (Environment.OSVersion.Version.Build < 22000)
                Add(true, "ComfyUI 的 AMD（ROCm）包需要 Windows 11。", "ComfyUI's AMD (ROCm) package needs Windows 11.");
            if (i.Vendor == GpuVendor.Amd && !AmdSupported(i.Name))
                Add(true, "这张 AMD 显卡（RDNA 1 及更早，如 RX 5000/500/Vega）不受 ROCm 支持。", "This AMD card (RDNA 1 or older, e.g. RX 5000/500/Vega) is not supported by ROCm.");
            Add(false, "AMD 下无法读取 GPU 温度，过热保护不会生效。", "GPU temperature is not readable on AMD, so overheat protection is inactive.");
        }
        if (package == "intel")
        {
            if (!IsArc(i.Name))
                Add(true, "Intel 核显（UHD / Iris）性能不足，只有 Arc 独显受支持。", "Intel integrated graphics (UHD / Iris) are too weak; only Arc GPUs are supported.");
            Add(false, "Intel 下无法读取 GPU 温度，过热保护不会生效。", "GPU temperature is not readable on Intel, so overheat protection is inactive.");
        }
        if (package == "cpu")
            w.Add((CpuReason() + L.Z("将使用 CPU 运行：极慢（一张图可能要数小时），并占满 CPU。",
                                     "It will run on the CPU: extremely slow (hours per image) and it keeps the CPU fully busy."), true));
        else if (i.VramMiB is < 5500)
            Add(false, "显存不足 6 GB：会自动使用最小的模型和最低性能档位，出图较慢。", "Less than 6 GB of VRAM: the smallest model and lowest profile are used; generation is slow.");
        if (RamMiB is > 0 and < 12000)
            Add(true, $"内存只有 {RamMiB / 1024.0:0} GB：模型运行至少需要约 12 GB 内存（建议 16 GB），很可能无法出图。",
                      $"Only {RamMiB / 1024.0:0} GB of RAM: the models need about 12 GB (16 GB recommended); generation will likely fail.");
        return w;
    }

    /// <summary>Why this machine ends up on the CPU: no GPU at all, or a GPU that PyTorch cannot use.</summary>
    public static string CpuReason()
    {
        var i = Info;
        if (AutoPackage != "cpu")
            return L.Z($"已手动选择 CPU 运行包（显卡 {i.Name} 本可使用）。", $"The CPU package was chosen manually ({i.Name} could be used). ");
        return i.Vendor switch
        {
            GpuVendor.None => L.Z("未检测到可用于出图的显卡。", "No GPU usable for generation was found. "),
            GpuVendor.Nvidia => L.Z($"{i.Name} 太旧（sm_{i.ComputeCap * 10:0}），PyTorch 已不支持。", $"{i.Name} is too old (sm_{i.ComputeCap * 10:0}) for PyTorch. "),
            GpuVendor.Amd => L.Z($"{i.Name} 不受 ROCm 支持（需 RX 6000 或更新）。", $"{i.Name} is not supported by ROCm (needs RX 6000 or newer). "),
            GpuVendor.Intel => L.Z($"{i.Name} 是核显，性能不足（只支持 Intel Arc）。", $"{i.Name} is integrated graphics, too weak (only Intel Arc is supported). "),
            _ => L.Z($"{i.Name} 不受支持。", $"{i.Name} is not supported. "),
        };
    }

    public static bool IsCpu => Package == "cpu";

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    public static long RamMiB
    {
        get
        {
            var m = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref m) ? (long)(m.TotalPhys / 1048576) : 0;
        }
    }

    public static string Summary()
    {
        var i = Info;
        if (i.Vendor == GpuVendor.None) return L.Z("未检测到独立显卡", "No dedicated GPU detected");
        var parts = new List<string> { i.Name };
        if (i.VramMiB is int v) parts.Add($"{v / 1024.0:0.#} GB VRAM");
        if (i.ComputeCap is double c) parts.Add($"sm_{c * 10:0}");
        if (i.Driver is double d) parts.Add(L.Z("驱动 ", "driver ") + d.ToString(CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    /// <summary>Lets the backend pick a performance profile and CPU mode without nvidia-smi.</summary>
    public static void Publish() => AppPaths.SaveSettings(s =>
    {
        if (Info.VramMiB is int v) s["gpu_vram_mib"] = v;
        s["gpu_package"] = Package;
    });
}
