using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameForge.Hardware;

/// <summary>
/// Documented Win32 probes only. No drivers, no injection, no plan changes.
/// All methods no-op / empty outside Windows.
/// </summary>
internal static class WindowsNative
{
    private const int EnumCurrentSettings = -1;
    private const int CchDeviceName = 32;
    private const int CchDevName = 128;

    public static List<string> TryEnumerateDisplayAdapters()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        for (uint i = 0; ; i++)
        {
            var dd = new DisplayDeviceW { cb = (uint)Marshal.SizeOf<DisplayDeviceW>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0))
            {
                break;
            }

            // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1; MIRRORING = 0x8
            var name = (dd.DeviceString ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if ((dd.StateFlags & 0x1) == 0 && (dd.StateFlags & 0x8) != 0)
            {
                continue; // skip pure mirrors when not attached
            }

            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(name);
            }
        }

        return list;
    }

    public static (string? Resolution, int? RefreshHz) TryGetPrimaryDisplayMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, null);
        }

        var mode = new DevModeW();
        mode.dmSize = (ushort)Marshal.SizeOf<DevModeW>();
        if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref mode))
        {
            return (null, null);
        }

        string? res = null;
        if (mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
        {
            res = $"{mode.dmPelsWidth}x{mode.dmPelsHeight}";
        }

        int? hz = mode.dmDisplayFrequency > 1 ? (int)mode.dmDisplayFrequency : null;
        return (res, hz);
    }

    public static bool? TryReadGameModeEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // Reflection so Linux builds need no Microsoft.Win32.Registry package.
        try
        {
            var registryType = Type.GetType("Microsoft.Win32.Registry, Microsoft.Win32.Registry")
                ?? Type.GetType("Microsoft.Win32.Registry, System.Windows.Extensions")
                ?? Type.GetType("Microsoft.Win32.Registry");
            if (registryType is null)
            {
                return null;
            }

            var currentUser = registryType.GetProperty("CurrentUser")?.GetValue(null);
            if (currentUser is null)
            {
                return null;
            }

            var open = currentUser.GetType().GetMethod("OpenSubKey", new[] { typeof(string) });
            using var key = open?.Invoke(currentUser, new object[] { @"Software\Microsoft\GameBar" }) as IDisposable;
            if (key is null)
            {
                return null;
            }

            var getValue = key.GetType().GetMethod("GetValue", new[] { typeof(string) });
            var v = getValue?.Invoke(key, new object[] { "AutoGameModeEnabled" })
                    ?? getValue?.Invoke(key, new object[] { "AllowAutoGameMode" });
            if (v is int i)
            {
                return i != 0;
            }

            if (v is long l)
            {
                return l != 0;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static string? TryReadActivePowerPlanName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // Read-only: powercfg /getactivescheme — never /setactive
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = "/getactivescheme",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            // e.g. "Power Scheme GUID: ...  (Balanced)"
            var open = output.LastIndexOf('(');
            var close = output.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                return output[(open + 1)..close].Trim();
            }

            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDeviceW lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(
        string? lpszDeviceName,
        int iModeNum,
        ref DevModeW lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceW
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevModeW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
