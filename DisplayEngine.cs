using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace LumiBar
{
    internal enum DisplayChannel { DdcCi, LaptopWmi, SoftwareGamma }

    internal sealed class DisplayMonitorItem
    {
        public int Index;
        public string DeviceName;       // e.g. \\.\DISPLAY1
        public string FriendlyName;     // e.g. LG UltraGear 27GP850
        public string Resolution;       // e.g. 2560×1440
        public bool IsPrimary;
        public DisplayChannel Channel;
        public int Brightness;          // 0..100
        public int Contrast;            // 0..100
        public bool SupportsContrast;
        public uint MinBr, MaxBr = 100;
        public uint MinCt, MaxCt = 100;

        // Hardware resources (owned by DisplayEngine)
        public IntPtr PhysicalHandle = IntPtr.Zero;
        public WinNative.PHYSICAL_MONITOR RawPhysical;
        public bool HasOriginalGamma;
        public WinNative.RAMP OriginalGamma;
    }

    internal sealed class DisplayEngine : IDisposable
    {
        private readonly List<DisplayMonitorItem> _monitors = new List<DisplayMonitorItem>();
        private readonly object _sync = new object();
        private readonly Dictionary<int, int> _pendingBrightness = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _pendingContrast = new Dictionary<int, int>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private Thread _worker;
        private volatile bool _stop;
        private int _warmTintPct;       // 0..80
        private bool _demoMode;
        private bool _gammaTouched;

        public IList<DisplayMonitorItem> Monitors { get { return _monitors; } }
        public int WarmTintPct { get { return _warmTintPct; } }

        public DisplayEngine(bool demoMode)
        {
            _demoMode = demoMode;
            if (!demoMode)
            {
                _worker = new Thread(WorkerLoop);
                _worker.IsBackground = true;
                _worker.Name = "LumiBar-DDCCI";
                _worker.Start();
            }
        }

        public void RefreshMonitors()
        {
            if (_demoMode)
            {
                _monitors.Clear();
                _monitors.AddRange(BuildDemoMonitors());
                return;
            }

            ReleaseHardwareHandles();
            _monitors.Clear();

            List<IntPtr> hMonitors = new List<IntPtr>();
            WinNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate(IntPtr hMon, IntPtr hdc, ref WinNative.RECT r, IntPtr data)
                {
                    hMonitors.Add(hMon);
                    return true;
                }, IntPtr.Zero);

            int wmiBrightness = ReadWmiLaptopBrightness();
            Dictionary<string, string> wmiNames = ReadWmiMonitorNames();
            bool wmiAssigned = false;

            for (int i = 0; i < hMonitors.Count; i++)
            {
                IntPtr hMon = hMonitors[i];
                WinNative.MONITORINFOEXW mi = new WinNative.MONITORINFOEXW();
                mi.cbSize = (uint)Marshal.SizeOf(typeof(WinNative.MONITORINFOEXW));
                if (!WinNative.GetMonitorInfoW(hMon, ref mi)) continue;

                string devName = (mi.szDevice ?? "").Trim();
                int w = Math.Max(0, mi.rcMonitor.Right - mi.rcMonitor.Left);
                int h = Math.Max(0, mi.rcMonitor.Bottom - mi.rcMonitor.Top);

                DisplayMonitorItem item = new DisplayMonitorItem();
                item.Index = _monitors.Count;
                item.DeviceName = devName;
                item.Resolution = w + "×" + h;
                item.IsPrimary = (mi.dwFlags & WinNative.MONITORINFOF_PRIMARY) != 0;
                item.FriendlyName = ResolveFriendlyName(devName, item.Index + 1, wmiNames);
                item.Brightness = 100;
                item.Contrast = 50;

                // Read & preserve exact original gamma ramp before any software adjustment
                WinNative.RAMP orig;
                if (ReadGammaRamp(devName, out orig))
                {
                    item.HasOriginalGamma = true;
                    item.OriginalGamma = orig;
                }
                else
                {
                    item.HasOriginalGamma = false;
                    item.OriginalGamma = WinNative.RAMP.CreateIdentity();
                }

                // 1) Try hardware DDC/CI via dxva2.dll
                bool ddcOk = TryInitDdcCi(hMon, item);

                // 2) Fallback to WMI laptop panel brightness (internal display)
                if (!ddcOk && wmiBrightness >= 0 && !wmiAssigned)
                {
                    item.Channel = DisplayChannel.LaptopWmi;
                    item.Brightness = Math.Max(0, Math.Min(100, wmiBrightness));
                    item.SupportsContrast = false;
                    wmiAssigned = true;
                }
                // 3) Final fallback: smooth reversible Software Gamma dimming
                else if (!ddcOk)
                {
                    item.Channel = DisplayChannel.SoftwareGamma;
                    item.Brightness = 100;
                    item.SupportsContrast = false;
                }

                _monitors.Add(item);
            }

            // Sort primary monitor first
            _monitors.Sort(delegate(DisplayMonitorItem a, DisplayMonitorItem b)
            {
                if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
                return string.Compare(a.DeviceName, b.DeviceName, StringComparison.OrdinalIgnoreCase);
            });
            for (int i = 0; i < _monitors.Count; i++) _monitors[i].Index = i;
        }

        private static bool TryInitDdcCi(IntPtr hMon, DisplayMonitorItem item)
        {
            try
            {
                uint count = 0;
                if (!WinNative.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0)
                    return false;

                WinNative.PHYSICAL_MONITOR[] arr = new WinNative.PHYSICAL_MONITOR[count];
                if (!WinNative.GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                    return false;

                // Keep first physical handle, destroy any extra handles immediately
                IntPtr hPhys = arr[0].hPhysicalMonitor;
                if (count > 1)
                {
                    WinNative.PHYSICAL_MONITOR[] extras = new WinNative.PHYSICAL_MONITOR[count - 1];
                    for (uint k = 1; k < count; k++) extras[k - 1] = arr[k];
                    WinNative.DestroyPhysicalMonitors(count - 1, extras);
                }

                uint minB, curB, maxB;
                if (WinNative.GetMonitorBrightness(hPhys, out minB, out curB, out maxB) && maxB > minB)
                {
                    item.Channel = DisplayChannel.DdcCi;
                    item.PhysicalHandle = hPhys;
                    item.RawPhysical = arr[0];
                    item.MinBr = minB;
                    item.MaxBr = maxB;
                    item.Brightness = ClampPct((int)Math.Round(((double)(curB - minB) / (maxB - minB)) * 100.0));

                    if (!string.IsNullOrEmpty(arr[0].szPhysicalMonitorDescription))
                    {
                        string desc = arr[0].szPhysicalMonitorDescription.Trim();
                        if (desc.Length >= 3 && !string.Equals(desc, "Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
                            item.FriendlyName = desc;
                    }

                    uint minC, curC, maxC;
                    if (WinNative.GetMonitorContrast(hPhys, out minC, out curC, out maxC) && maxC > minC)
                    {
                        item.SupportsContrast = true;
                        item.MinCt = minC;
                        item.MaxCt = maxC;
                        item.Contrast = ClampPct((int)Math.Round(((double)(curC - minC) / (maxC - minC)) * 100.0));
                    }
                    return true;
                }

                WinNative.DestroyPhysicalMonitors(1, new WinNative.PHYSICAL_MONITOR[] { arr[0] });
            }
            catch { }
            return false;
        }

        private static Dictionary<string, string> ReadWmiMonitorNames()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, ManufacturerName, UserFriendlyName FROM WmiMonitorID"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject obj in col)
                    {
                        using (obj)
                        {
                            string inst = obj["InstanceName"] as string;
                            string model = DecodeWmiU16(obj["UserFriendlyName"] as ushort[]);
                            string mfgCode = DecodeWmiU16(obj["ManufacturerName"] as ushort[]);
                            if (string.IsNullOrEmpty(inst) || string.IsNullOrEmpty(model)) continue;

                            // InstanceName format: DISPLAY\ACR074D\5&11aeaaec&0&UID772_0 -> extract PNP ID "ACR074D"
                            string[] parts = inst.Split('\\');
                            string pnpId = parts.Length >= 2 ? parts[1].Trim() : "";
                            if (string.IsNullOrEmpty(pnpId)) continue;

                            string brand = MapPnpBrand(mfgCode);
                            string full = (!string.IsNullOrEmpty(brand) && model.IndexOf(brand, StringComparison.OrdinalIgnoreCase) < 0)
                                ? (brand + " " + model) : model;
                            map[pnpId] = full.Trim();
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        private static string DecodeWmiU16(ushort[] arr)
        {
            if (arr == null || arr.Length == 0) return "";
            char[] chars = new char[arr.Length];
            int n = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] == 0) break;
                if (arr[i] >= 32 && arr[i] < 127) chars[n++] = (char)arr[i];
            }
            return new string(chars, 0, n).Trim();
        }

        private static string MapPnpBrand(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            string c = code.Trim().ToUpperInvariant();
            if (c == "ACR") return "Acer";
            if (c == "AUS" || c == "ACI") return "ASUS";
            if (c == "DEL") return "Dell";
            if (c == "GSM" || c == "LGD") return "LG";
            if (c == "SAM") return "Samsung";
            if (c == "MSI") return "MSI";
            if (c == "AOC") return "AOC";
            if (c == "BNQ") return "BenQ";
            if (c == "PHL") return "Philips";
            if (c == "HWP" || c == "HPN") return "HP";
            if (c == "LEN") return "Lenovo";
            if (c == "GBT") return "Gigabyte";
            if (c == "VSC") return "ViewSonic";
            if (c == "ENC") return "EIZO";
            if (c == "SNY") return "Sony";
            if (c == "APP") return "Apple";
            return "";
        }

        private static string ResolveFriendlyName(string deviceName, int ordinal, Dictionary<string, string> wmiNames)
        {
            try
            {
                WinNative.DISPLAY_DEVICEW dd = new WinNative.DISPLAY_DEVICEW();
                dd.cb = (uint)Marshal.SizeOf(typeof(WinNative.DISPLAY_DEVICEW));
                if (WinNative.EnumDisplayDevicesW(deviceName, 0, ref dd, 0))
                {
                    // DeviceID format: MONITOR\ACR074D\{4d36e96e-...}\0001
                    string devId = (dd.DeviceID ?? "").Trim();
                    string[] parts = devId.Split('\\');
                    string pnp = parts.Length >= 2 ? parts[1].Trim() : "";
                    string edidName;
                    if (!string.IsNullOrEmpty(pnp) && wmiNames != null && wmiNames.TryGetValue(pnp, out edidName) && !string.IsNullOrEmpty(edidName))
                        return edidName;

                    string s = (dd.DeviceString ?? "").Trim();
                    if (!string.IsNullOrEmpty(s) && !string.Equals(s, "Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
                        return s;
                }
            }
            catch { }
            return "Display " + ordinal;
        }

        private static int ReadWmiLaptopBrightness()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject obj in col)
                    {
                        using (obj)
                        {
                            object v = obj["CurrentBrightness"];
                            if (v != null) return Convert.ToInt32(v);
                        }
                    }
                }
            }
            catch { }
            return -1;
        }

        private static void SetWmiLaptopBrightness(int pct)
        {
            try
            {
                byte level = (byte)ClampPct(pct);
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject obj in col)
                    {
                        using (obj)
                        {
                            obj.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, level });
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        public void SetBrightness(int monitorIndex, int pct)
        {
            if (monitorIndex < 0 || monitorIndex >= _monitors.Count) return;
            DisplayMonitorItem m = _monitors[monitorIndex];
            int clamped = ClampPct(pct);
            if (m.Channel == DisplayChannel.SoftwareGamma && clamped < 20) clamped = 20; // Never black out software display
            m.Brightness = clamped;

            if (_demoMode) return;

            if (m.Channel == DisplayChannel.SoftwareGamma)
            {
                ApplyGammaToMonitor(m);
            }
            else
            {
                lock (_sync) { _pendingBrightness[monitorIndex] = clamped; }
                _signal.Set();
            }
        }

        public void SetAllBrightness(int pct)
        {
            for (int i = 0; i < _monitors.Count; i++) SetBrightness(i, pct);
        }

        public void SetContrast(int monitorIndex, int pct)
        {
            if (monitorIndex < 0 || monitorIndex >= _monitors.Count) return;
            DisplayMonitorItem m = _monitors[monitorIndex];
            if (!m.SupportsContrast) return;
            int clamped = ClampPct(pct);
            m.Contrast = clamped;
            if (_demoMode) return;

            lock (_sync) { _pendingContrast[monitorIndex] = clamped; }
            _signal.Set();
        }

        public void SetWarmTint(int pct)
        {
            _warmTintPct = Math.Max(0, Math.Min(80, pct));
            if (_demoMode) return;
            for (int i = 0; i < _monitors.Count; i++) ApplyGammaToMonitor(_monitors[i]);
        }

        public void ResetAllGammas()
        {
            _warmTintPct = 0;
            for (int i = 0; i < _monitors.Count; i++)
            {
                DisplayMonitorItem m = _monitors[i];
                if (m.Channel == DisplayChannel.SoftwareGamma) m.Brightness = 100;
            }
            if (_demoMode || !_gammaTouched) return;
            for (int i = 0; i < _monitors.Count; i++)
            {
                DisplayMonitorItem m = _monitors[i];
                if (m.HasOriginalGamma) WriteGammaRamp(m.DeviceName, m.OriginalGamma);
            }
            _gammaTouched = false;
        }

        private void ApplyGammaToMonitor(DisplayMonitorItem m)
        {
            if (m == null || string.IsNullOrEmpty(m.DeviceName)) return;
            int softBr = (m.Channel == DisplayChannel.SoftwareGamma) ? m.Brightness : 100;
            if (softBr >= 100 && _warmTintPct <= 0)
            {
                if (_gammaTouched && m.HasOriginalGamma) WriteGammaRamp(m.DeviceName, m.OriginalGamma);
                return;
            }
            WinNative.RAMP ramp = BuildGammaRamp(m.OriginalGamma, softBr, _warmTintPct);
            if (WriteGammaRamp(m.DeviceName, ramp)) _gammaTouched = true;
        }

        // Pure testable gamma math: exact identity at (100, 0), smooth warm color shift & safe dimming otherwise
        public static WinNative.RAMP BuildGammaRamp(WinNative.RAMP baseRamp, int softwareBrightnessPct, int warmTintPct)
        {
            WinNative.RAMP src = (baseRamp.Red != null && baseRamp.Red.Length == 256) ? baseRamp : WinNative.RAMP.CreateIdentity();
            int br = Math.Max(20, Math.Min(100, softwareBrightnessPct));
            int tint = Math.Max(0, Math.Min(80, warmTintPct));
            if (br == 100 && tint == 0) return src.Clone();

            double bScale = br / 100.0;
            double t = tint / 80.0; // 0.0 -> 6500K, 1.0 -> ~2700K
            double rMul = bScale * 1.0;
            double gMul = bScale * (1.0 - 0.22 * t);
            double blMul = bScale * (1.0 - 0.58 * t);

            WinNative.RAMP dst = new WinNative.RAMP();
            dst.Red = new ushort[256];
            dst.Green = new ushort[256];
            dst.Blue = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                dst.Red[i] = ClampU16(src.Red[i] * rMul);
                dst.Green[i] = ClampU16(src.Green[i] * gMul);
                dst.Blue[i] = ClampU16(src.Blue[i] * blMul);
            }
            return dst;
        }

        public static int WarmTintToKelvin(int warmTintPct)
        {
            int t = Math.Max(0, Math.Min(80, warmTintPct));
            return (int)Math.Round(6500.0 - (t / 80.0) * 3800.0);
        }

        private static ushort ClampU16(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return 0;
            if (v >= 65535.0) return 65535;
            return (ushort)Math.Round(v);
        }

        private static bool ReadGammaRamp(string deviceName, out WinNative.RAMP ramp)
        {
            ramp = WinNative.RAMP.CreateIdentity();
            IntPtr hdc = WinNative.CreateDCW("DISPLAY", deviceName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;
            try { return WinNative.GetDeviceGammaRamp(hdc, ref ramp); }
            finally { WinNative.DeleteDC(hdc); }
        }

        private static bool WriteGammaRamp(string deviceName, WinNative.RAMP ramp)
        {
            IntPtr hdc = WinNative.CreateDCW("DISPLAY", deviceName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;
            try { return WinNative.SetDeviceGammaRamp(hdc, ref ramp); }
            finally { WinNative.DeleteDC(hdc); }
        }

        public static void TurnOffMonitorsNow()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(400);
                try { WinNative.PostMessage(WinNative.HWND_BROADCAST, WinNative.WM_SYSCOMMAND, new IntPtr(WinNative.SC_MONITORPOWER), new IntPtr(2)); }
                catch { }
            });
        }

        private void WorkerLoop()
        {
            while (!_stop)
            {
                _signal.WaitOne(250);
                if (_stop) break;

                Dictionary<int, int> brBatch = null;
                Dictionary<int, int> ctBatch = null;
                lock (_sync)
                {
                    if (_pendingBrightness.Count > 0)
                    {
                        brBatch = new Dictionary<int, int>(_pendingBrightness);
                        _pendingBrightness.Clear();
                    }
                    if (_pendingContrast.Count > 0)
                    {
                        ctBatch = new Dictionary<int, int>(_pendingContrast);
                        _pendingContrast.Clear();
                    }
                }

                if (brBatch != null)
                {
                    foreach (KeyValuePair<int, int> kv in brBatch)
                    {
                        if (_stop) break;
                        DisplayMonitorItem m = kv.Key >= 0 && kv.Key < _monitors.Count ? _monitors[kv.Key] : null;
                        if (m == null) continue;
                        if (m.Channel == DisplayChannel.DdcCi && m.PhysicalHandle != IntPtr.Zero)
                        {
                            uint raw = m.MinBr + (uint)Math.Round(((double)kv.Value / 100.0) * (m.MaxBr - m.MinBr));
                            try { WinNative.SetMonitorBrightness(m.PhysicalHandle, raw); } catch { }
                        }
                        else if (m.Channel == DisplayChannel.LaptopWmi)
                        {
                            SetWmiLaptopBrightness(kv.Value);
                        }
                    }
                }

                if (ctBatch != null)
                {
                    foreach (KeyValuePair<int, int> kv in ctBatch)
                    {
                        if (_stop) break;
                        DisplayMonitorItem m = kv.Key >= 0 && kv.Key < _monitors.Count ? _monitors[kv.Key] : null;
                        if (m == null || !m.SupportsContrast || m.PhysicalHandle == IntPtr.Zero) continue;
                        uint raw = m.MinCt + (uint)Math.Round(((double)kv.Value / 100.0) * (m.MaxCt - m.MinCt));
                        try { WinNative.SetMonitorContrast(m.PhysicalHandle, raw); } catch { }
                    }
                }
            }
        }

        private void ReleaseHardwareHandles()
        {
            List<WinNative.PHYSICAL_MONITOR> toFree = new List<WinNative.PHYSICAL_MONITOR>();
            for (int i = 0; i < _monitors.Count; i++)
            {
                if (_monitors[i].PhysicalHandle != IntPtr.Zero)
                {
                    toFree.Add(_monitors[i].RawPhysical);
                    _monitors[i].PhysicalHandle = IntPtr.Zero;
                }
            }
            if (toFree.Count > 0)
            {
                try { WinNative.DestroyPhysicalMonitors((uint)toFree.Count, toFree.ToArray()); } catch { }
            }
        }

        private static int ClampPct(int v) { return Math.Max(0, Math.Min(100, v)); }

        public static List<DisplayMonitorItem> BuildDemoMonitors()
        {
            List<DisplayMonitorItem> list = new List<DisplayMonitorItem>();

            DisplayMonitorItem m1 = new DisplayMonitorItem();
            m1.Index = 0; m1.DeviceName = @"\\.\DISPLAY1"; m1.FriendlyName = "LG UltraGear 27GP850";
            m1.Resolution = "2560×1440"; m1.IsPrimary = true; m1.Channel = DisplayChannel.DdcCi;
            m1.Brightness = 72; m1.Contrast = 65; m1.SupportsContrast = true;
            m1.OriginalGamma = WinNative.RAMP.CreateIdentity();
            list.Add(m1);

            DisplayMonitorItem m2 = new DisplayMonitorItem();
            m2.Index = 1; m2.DeviceName = @"\\.\DISPLAY2"; m2.FriendlyName = "DELL U2723QE 4K";
            m2.Resolution = "1920×1080"; m2.IsPrimary = false; m2.Channel = DisplayChannel.DdcCi;
            m2.Brightness = 55; m2.Contrast = 60; m2.SupportsContrast = true;
            m2.OriginalGamma = WinNative.RAMP.CreateIdentity();
            list.Add(m2);

            return list;
        }

        public void Dispose()
        {
            _stop = true;
            try { _signal.Set(); } catch { }
            if (_worker != null && _worker.IsAlive) { try { _worker.Join(800); } catch { } }
            ResetAllGammas();
            ReleaseHardwareHandles();
            _signal.Dispose();
        }
    }
}
