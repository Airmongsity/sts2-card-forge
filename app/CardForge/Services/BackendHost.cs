using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CardForge.Services;

/// <summary>Starts backend/server.py on ComfyUI's embedded Python. The process is placed in a job object so it
/// (and the ComfyUI it spawns) dies with the app, even on a crash.</summary>
public static class BackendHost
{
    static Process? _proc;
    static readonly StringBuilder Log = new();
    public static string LogText { get { lock (Log) return Log.ToString(); } }
    public static bool CanStart => File.Exists(AppPaths.Python) && File.Exists(Path.Combine(AppPaths.Backend, "server.py"));

    public static async Task<bool> EnsureRunning()
    {
        if (await Api.Healthy()) return true;
        if (!CanStart) return false;
        if (_proc is { HasExited: false }) _proc.Kill(true);

        var psi = new ProcessStartInfo(AppPaths.Python)
        {
            WorkingDirectory = AppPaths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(Path.Combine(AppPaths.Backend, "server.py"));
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(Api.Port.ToString());
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        _proc = Process.Start(psi)!;
        JobObject.Assign(_proc);
        _proc.OutputDataReceived += (_, e) => Append(e.Data);
        _proc.ErrorDataReceived += (_, e) => Append(e.Data);
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        for (int i = 0; i < 60; i++)
        {
            if (_proc.HasExited) return false;
            if (await Api.Healthy()) return true;
            await Task.Delay(500);
        }
        return false;
    }

    static void Append(string? line)
    {
        if (line == null) return;
        lock (Log)
        {
            Log.AppendLine(line);
            if (Log.Length > 200_000) Log.Remove(0, Log.Length - 150_000);
        }
    }

    public static async Task Stop()
    {
        try { await Api.Post("/api/shutdown"); } catch { }
        if (_proc is { HasExited: false })
        {
            if (!_proc.WaitForExit(5000)) _proc.Kill(true);
        }
    }
}

static class JobObject
{
    static readonly IntPtr Handle = Create();

    static IntPtr Create()
    {
        var h = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(len);
        Marshal.StructureToPtr(info, ptr, false);
        SetInformationJobObject(h, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)len);
        Marshal.FreeHGlobal(ptr);
        return h;
    }

    public static void Assign(Process p) => AssignProcessToJobObject(Handle, p.Handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}
