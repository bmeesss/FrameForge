using System.Diagnostics;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Benchmark;

/// <summary>
/// Locates the CS2 process via Process.GetProcesses — name/id/start/CPU time/working set only.
/// Never opens process memory, never injects.
/// </summary>
public sealed class Cs2ProcessMonitor : ICs2ProcessMonitor
{
    private static readonly string[] CandidateNames =
    [
        "cs2",
        "cs2.exe",
        "csgo",
        "csgo.exe"
    ];

    public Cs2ProcessInfo? TryGetCs2Process()
    {
        try
        {
            foreach (var name in CandidateNames.Select(n => n.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)).Distinct())
            {
                Process[] list;
                try
                {
                    list = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (var proc in list)
                {
                    try
                    {
                        using (proc)
                        {
                            if (proc.HasExited)
                            {
                                continue;
                            }

                            DateTimeOffset? start = null;
                            try
                            {
                                start = new DateTimeOffset(proc.StartTime.ToUniversalTime());
                            }
                            catch
                            {
                                // permission / platform
                            }

                            long ws = 0;
                            try { ws = proc.WorkingSet64; } catch { /* ignore */ }

                            TimeSpan cpu = TimeSpan.Zero;
                            try { cpu = proc.TotalProcessorTime; } catch { /* ignore */ }

                            return new Cs2ProcessInfo
                            {
                                ProcessId = proc.Id,
                                ProcessName = proc.ProcessName,
                                StartTime = start,
                                WorkingSetBytes = ws,
                                TotalProcessorTime = cpu
                            };
                        }
                    }
                    catch
                    {
                        // try next
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
