using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LumiBar
{
    internal static class Loc
    {
        public static string DetectSystemLanguage()
        {
            try
            {
                string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                if (string.Equals(two, "tr", StringComparison.OrdinalIgnoreCase)) return "tr";
                if (string.Equals(two, "zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            }
            catch { }
            return "en";
        }

        public static string T(string lang, string en, string tr, string zh)
        {
            if (string.Equals(lang, "tr", StringComparison.OrdinalIgnoreCase)) return tr;
            if (string.Equals(lang, "zh", StringComparison.OrdinalIgnoreCase)) return zh;
            return en;
        }

        public static string FontFamily(string lang)
        {
            return string.Equals(lang, "zh", StringComparison.OrdinalIgnoreCase) ? "Microsoft YaHei UI" : "Segoe UI";
        }

        public static CultureInfo Culture(string lang)
        {
            try
            {
                if (string.Equals(lang, "tr", StringComparison.OrdinalIgnoreCase)) return CultureInfo.GetCultureInfo("tr-TR");
                if (string.Equals(lang, "zh", StringComparison.OrdinalIgnoreCase)) return CultureInfo.GetCultureInfo("zh-CN");
                return CultureInfo.GetCultureInfo("en-US");
            }
            catch { return CultureInfo.InvariantCulture; }
        }

        public static string F(string fmt, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, fmt, args);
        }
    }

    internal sealed class LumiBarSettings
    {
        public string Language = Loc.DetectSystemLanguage();
        public bool MinimizeToTray = true;
        public bool LinkMonitors = false;
        public int WarmTint = 0;         // 0..80 (%)
        public int AwakeMode = 0;        // 0=Off, 1=Screen+PC, 2=PC Only
        public int IdleAction = 0;       // 0=Sleep, 1=Shutdown
        public int IdleMinutes = 5;      // 3, 5, 10

        public static string GetSettingsPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(Path.Combine(appData, "LumiBar"), "settings.ini");
        }

        public static LumiBarSettings Load()
        {
            LumiBarSettings s = new LumiBarSettings();
            try
            {
                string p = GetSettingsPath();
                if (!File.Exists(p)) return s;
                foreach (string raw in File.ReadAllLines(p, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim();
                    string v = raw.Substring(eq + 1).Trim();
                    int iv;
                    if (k == "Language" && (v == "tr" || v == "en" || v == "zh")) s.Language = v;
                    else if (k == "MinimizeToTray") s.MinimizeToTray = (v == "1");
                    else if (k == "LinkMonitors") s.LinkMonitors = (v == "1");
                    else if (k == "WarmTint" && int.TryParse(v, out iv)) s.WarmTint = Math.Max(0, Math.Min(80, iv));
                    else if (k == "AwakeMode" && int.TryParse(v, out iv)) s.AwakeMode = Math.Max(0, Math.Min(2, iv));
                    else if (k == "IdleAction" && int.TryParse(v, out iv)) s.IdleAction = Math.Max(0, Math.Min(1, iv));
                    else if (k == "IdleMinutes" && int.TryParse(v, out iv)) s.IdleMinutes = Math.Max(1, Math.Min(60, iv));
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                string p = GetSettingsPath();
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Language=" + (Language ?? "en"));
                sb.AppendLine("MinimizeToTray=" + (MinimizeToTray ? "1" : "0"));
                sb.AppendLine("LinkMonitors=" + (LinkMonitors ? "1" : "0"));
                sb.AppendLine("WarmTint=" + WarmTint);
                sb.AppendLine("AwakeMode=" + AwakeMode);
                sb.AppendLine("IdleAction=" + IdleAction);
                sb.AppendLine("IdleMinutes=" + IdleMinutes);
                File.WriteAllText(p, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class WinNative
    {
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MONITORPOWER = 0xF170;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWA_TEXT_COLOR = 36;
        public const uint MSGFLT_ALLOW = 1;
        public const uint MONITORINFOF_PRIMARY = 1;

        public const uint ES_CONTINUOUS = 0x80000000u;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001u;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002u;

        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public static readonly uint WM_SHOW_EXISTING = RegisterWindowMessage("LumiBar_ShowExistingWindow_v1");
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        private const string StartupRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "LumiBar";

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEXW
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICEW
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
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAMP
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Blue;

            public static RAMP CreateIdentity()
            {
                RAMP r = new RAMP();
                r.Red = new ushort[256];
                r.Green = new ushort[256];
                r.Blue = new ushort[256];
                for (int i = 0; i < 256; i++)
                {
                    ushort v = (ushort)(i * 257);
                    r.Red[i] = v; r.Green[i] = v; r.Blue[i] = v;
                }
                return r;
            }

            public RAMP Clone()
            {
                RAMP r = new RAMP();
                r.Red = Red != null ? (ushort[])Red.Clone() : CreateIdentity().Red;
                r.Green = Green != null ? (ushort[])Green.Clone() : CreateIdentity().Green;
                r.Blue = Blue != null ? (ushort[])Blue.Clone() : CreateIdentity().Blue;
                return r;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorContrast(IntPtr hMonitor, out uint pdwMinimumContrast, out uint pdwCurrentContrast, out uint pdwMaximumContrast);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetMonitorContrast(IntPtr hMonitor, uint dwNewContrast);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateDCW(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public static void ApplyDarkTitleBar(IntPtr hwnd, Color captionColor, Color textColor)
        {
            try
            {
                int en = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref en, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref en, sizeof(int));
                int cap = captionColor.R | (captionColor.G << 8) | (captionColor.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref cap, sizeof(int));
                int txt = textColor.R | (textColor.G << 8) | (textColor.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref txt, sizeof(int));
            }
            catch { }
        }

        public static void AllowWakeMessage(IntPtr hwnd)
        {
            try
            {
                if (hwnd != IntPtr.Zero && WM_SHOW_EXISTING != 0)
                    ChangeWindowMessageFilterEx(hwnd, WM_SHOW_EXISTING, MSGFLT_ALLOW, IntPtr.Zero);
            }
            catch { }
        }

        public static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(StartupRunKey, false))
                {
                    if (k == null) return false;
                    string val = k.GetValue(StartupValueName) as string;
                    return !string.IsNullOrEmpty(val);
                }
            }
            catch { return false; }
        }

        public static bool SetStartupEnabled(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(StartupRunKey))
                {
                    if (k == null) return false;
                    if (enable) k.SetValue(StartupValueName, "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue(StartupValueName, false);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
