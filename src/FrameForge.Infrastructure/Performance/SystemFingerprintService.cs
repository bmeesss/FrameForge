using System.Security.Cryptography;
using System.Text;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Performance;

/// <summary>
/// Local system fingerprint from non-sensitive hardware/OS fields only.
/// Excludes username, email, Steam, IP, serials, MAC, filesystem secrets.
/// </summary>
public sealed class SystemFingerprintService : ISystemFingerprintService
{
    private readonly IHardwareInfoService _hardware;
    private SystemFingerprint? _cached;

    public SystemFingerprintService(IHardwareInfoService hardware)
    {
        _hardware = hardware;
    }

    public async Task<SystemFingerprint> GetFingerprintAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var hw = await _hardware.GetHardwareInfoAsync(cancellationToken).ConfigureAwait(false);
        _cached = FromHardware(hw);
        return _cached;
    }

    public SystemFingerprint FromHardware(HardwareInfo hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        var cpu = Normalize(hardware.CpuName);
        var gpu = Normalize(hardware.GpuName);
        var os = Normalize(
            string.IsNullOrWhiteSpace(hardware.OsDescription) || hardware.OsDescription == "Unknown"
                ? hardware.WindowsVersion
                : hardware.OsDescription);
        var arch = Normalize(hardware.Architecture);
        long? ram = hardware.TotalRamBytes > 0 ? hardware.TotalRamBytes : null;

        var id = ComputeId(cpu, gpu, ram, os, arch);
        return new SystemFingerprint
        {
            CpuModel = cpu,
            GpuModel = gpu,
            TotalRamBytes = ram,
            OsVersion = os,
            Architecture = arch,
            FingerprintId = id
        };
    }

    public static string ComputeId(
        string cpu,
        string gpu,
        long? ramBytes,
        string os,
        string arch)
    {
        var payload = string.Join('|',
            cpu.ToLowerInvariant(),
            gpu.ToLowerInvariant(),
            ramBytes?.ToString() ?? "unknown",
            os.ToLowerInvariant(),
            arch.ToLowerInvariant());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Rejects strings that look like sensitive identifiers (best-effort guard for tests/callers).
    /// </summary>
    public static bool LooksSensitive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim();
        // MAC-like
        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$"))
        {
            return true;
        }

        // IPv4
        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{1,3}(\.\d{1,3}){3}$"))
        {
            return true;
        }

        // Email
        if (v.Contains('@') && v.Contains('.'))
        {
            return true;
        }

        return false;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "unknown";
        }

        var trimmed = value.Trim();
        if (LooksSensitive(trimmed))
        {
            return "unknown";
        }

        // Strip path-like segments that could leak usernames
        if (trimmed.Contains('\\') || trimmed.Contains('/') && trimmed.Length > 32)
        {
            return "unknown";
        }

        return trimmed;
    }
}
