using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Benchmark;

/// <summary>
/// External OS sampling without third-party packages or CS2 injection:
/// - Linux: /proc/stat + /proc/meminfo
/// - Windows: GetSystemTimes (kernel32) + GlobalMemoryStatusEx when available
/// - Process CPU / working set via Process APIs
/// - GPU util / temps / frame-time: unavailable (honest nulls)
///
/// Expected overhead: short sample every ≥50 ms (default 100 ms). Avoid intervals &lt; 50 ms.
/// </summary>
public sealed class OsPerformanceSampler : IPerformanceSampler
{
    private readonly int _logicalProcessors;
    private DateTime _lastWallUtc = DateTime.UtcNow;
    private bool _hasSystemCpuBaseline;
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private TimeSpan _lastProcessCpu = TimeSpan.Zero;
    private int? _lastProcessId;
    private bool _hasProcessBaseline;
    private DateTime _lastProcessWall = DateTime.UtcNow;

    public OsPerformanceSampler()
    {
        _logicalProcessors = Math.Max(1, Environment.ProcessorCount);
    }

    public void Reset()
    {
        _hasSystemCpuBaseline = false;
        _hasProcessBaseline = false;
        _lastProcessId = null;
        _lastWallUtc = DateTime.UtcNow;
        _lastProcessWall = DateTime.UtcNow;
    }

    public BenchmarkSample TakeSample(Cs2ProcessInfo? cs2, bool isWarmup, double elapsedMs)
    {
        var now = DateTime.UtcNow;
        var systemCpu = TryReadSystemCpuPercent();
        var (procCpu, ws) = TryReadProcessMetrics(cs2, now);
        var (memUsed, memPct) = TryReadSystemMemory();

        return new BenchmarkSample
        {
            Timestamp = DateTimeOffset.UtcNow,
            ElapsedMs = elapsedMs,
            IsWarmup = isWarmup,
            SystemCpuPercent = systemCpu,
            ProcessCpuPercent = procCpu,
            ProcessWorkingSetBytes = ws,
            SystemMemoryUsedBytes = memUsed,
            SystemMemoryPercent = memPct,
            GpuUtilizationPercent = null,
            CpuTemperatureC = null,
            GpuTemperatureC = null,
            FrameTimeMs = null,
            Cs2ProcessId = cs2?.ProcessId,
            Cs2ProcessPresent = cs2 is not null
        };
    }

    private double? TryReadSystemCpuPercent()
    {
        if (OperatingSystem.IsWindows())
        {
            return TryWindowsSystemCpu();
        }

        if (OperatingSystem.IsLinux())
        {
            return TryLinuxProcStatCpu();
        }

        return null;
    }

    private double? TryWindowsSystemCpu()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var idleT = FileTimeToUlong(idle);
        var kernelT = FileTimeToUlong(kernel);
        var userT = FileTimeToUlong(user);

        if (!_hasSystemCpuBaseline)
        {
            _prevIdle = idleT;
            _prevKernel = kernelT;
            _prevUser = userT;
            _hasSystemCpuBaseline = true;
            return null;
        }

        var idleDelta = idleT - _prevIdle;
        var kernelDelta = kernelT - _prevKernel;
        var userDelta = userT - _prevUser;
        _prevIdle = idleT;
        _prevKernel = kernelT;
        _prevUser = userT;

        // kernel includes idle on Windows
        var total = kernelDelta + userDelta;
        if (total == 0)
        {
            return null;
        }

        var busy = total - idleDelta;
        var pct = 100.0 * busy / total;
        return Math.Clamp(pct, 0, 100);
    }

    private double? TryLinuxProcStatCpu()
    {
        try
        {
            if (!File.Exists("/proc/stat"))
            {
                return null;
            }

            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                return null;
            }

            var user = ulong.Parse(parts[1]);
            var nice = ulong.Parse(parts[2]);
            var system = ulong.Parse(parts[3]);
            var idle = ulong.Parse(parts[4]);
            var iowait = parts.Length > 5 ? ulong.Parse(parts[5]) : 0UL;
            var irq = parts.Length > 6 ? ulong.Parse(parts[6]) : 0UL;
            var softirq = parts.Length > 7 ? ulong.Parse(parts[7]) : 0UL;
            var steal = parts.Length > 8 ? ulong.Parse(parts[8]) : 0UL;

            var idleAll = idle + iowait;
            var nonIdle = user + nice + system + irq + softirq + steal;
            var total = idleAll + nonIdle;

            if (!_hasSystemCpuBaseline)
            {
                _prevIdle = idleAll;
                _prevKernel = total;
                _hasSystemCpuBaseline = true;
                return null;
            }

            var diffIdle = idleAll - _prevIdle;
            var diffTotal = total - _prevKernel;
            _prevIdle = idleAll;
            _prevKernel = total;

            if (diffTotal == 0)
            {
                return null;
            }

            return Math.Clamp(100.0 * (diffTotal - diffIdle) / diffTotal, 0, 100);
        }
        catch
        {
            return null;
        }
    }

    private (double? cpu, long? workingSet) TryReadProcessMetrics(Cs2ProcessInfo? info, DateTime now)
    {
        if (info is null)
        {
            _hasProcessBaseline = false;
            _lastProcessId = null;
            return (null, null);
        }

        long? ws = info.WorkingSetBytes > 0 ? info.WorkingSetBytes : null;
        double? cpu = null;

        try
        {
            using var proc = Process.GetProcessById(info.ProcessId);
            if (proc.HasExited)
            {
                _hasProcessBaseline = false;
                return (null, ws);
            }

            TimeSpan total;
            try { total = proc.TotalProcessorTime; }
            catch { return (null, ws); }

            try { ws = proc.WorkingSet64; } catch { /* keep */ }

            if (!_hasProcessBaseline || _lastProcessId != info.ProcessId)
            {
                _lastProcessCpu = total;
                _lastProcessId = info.ProcessId;
                _lastProcessWall = now;
                _hasProcessBaseline = true;
                return (null, ws);
            }

            var wallMs = (now - _lastProcessWall).TotalMilliseconds;
            var cpuDeltaMs = (total - _lastProcessCpu).TotalMilliseconds;
            _lastProcessCpu = total;
            _lastProcessWall = now;

            if (wallMs <= 0)
            {
                return (null, ws);
            }

            // Share of total machine capacity (0–100)
            var pct = 100.0 * cpuDeltaMs / (wallMs * _logicalProcessors);
            cpu = Math.Clamp(pct, 0, 100);
        }
        catch (ArgumentException)
        {
            _hasProcessBaseline = false;
            return (null, ws);
        }
        catch
        {
            return (null, ws);
        }

        return (cpu, ws);
    }

    private static (long? used, double? percent) TryReadSystemMemory()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            try
            {
                long total = 0, avail = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                    {
                        total = ParseKb(line) * 1024;
                    }
                    else if (line.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase))
                    {
                        avail = ParseKb(line) * 1024;
                    }
                }

                if (total > 0)
                {
                    var used = Math.Max(0, total - avail);
                    return (used, 100.0 * used / total);
                }
            }
            catch
            {
                // fall through
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
                {
                    var used = (long)(status.ullTotalPhys - status.ullAvailPhys);
                    var pct = 100.0 * used / status.ullTotalPhys;
                    return (used, pct);
                }
            }
            catch
            {
                // ignore
            }
        }

        return (null, null);
    }

    private static long ParseKb(string line)
    {
        var number = new string(line.Where(char.IsDigit).ToArray());
        return long.TryParse(number, out var v) ? v : 0;
    }

    private static ulong FileTimeToUlong(FILETIME ft) =>
        ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
