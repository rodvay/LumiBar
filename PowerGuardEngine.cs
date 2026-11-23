using System;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace LumiBar
{
    internal enum AwakeMode { Off = 0, ScreenAndPc = 1, PcOnly = 2 }
    internal enum PowerTimerAction { Sleep = 0, Shutdown = 1, MonitorsOff = 2 }

    internal sealed class PowerGuardStatus
    {
        public AwakeMode Awake = AwakeMode.Off;
        public int AwakeRemainingSec = -1; // -1 = unlimited
        public bool CountdownActive;
        public PowerTimerAction CountdownAction = PowerTimerAction.Sleep;
        public int CountdownRemainingSec;

        public bool SmartIdleEnabled;
        public PowerTimerAction SmartIdleAction = PowerTimerAction.Sleep;
        public int SmartIdleTargetSec = 300;
        public int SmartIdleElapsedSec;
        public double LiveDownKBps;
        public double LiveCpuPct;

        public bool WarningBannerActive;
        public int WarningSecondsLeft;
        public PowerTimerAction WarningAction = PowerTimerAction.Sleep;
        public string WarningReason = "";
    }

    internal sealed class PowerGuardEngine : IDisposable
    {
        public const double IdleDownThresholdKBps = 128.0; // 1 Mbps
        public const double IdleCpuThresholdPct = 8.0;
        public const int WarningGraceSeconds = 60;

        private readonly bool _demoMode;
        private readonly PowerGuardStatus _st = new PowerGuardStatus();

        private ulong _prevIdle, _prevKernel, _prevUser;
        private bool _hasCpuBase;
        private long _prevRx = -1;
        private readonly Stopwatch _netWatch = new Stopwatch();
        private bool _warningFromIdle;

        public PowerGuardStatus Status { get { return _st; } }

        public PowerGuardEngine(bool demoMode)
        {
            _demoMode = demoMode;
            if (demoMode)
            {
                _st.Awake = AwakeMode.ScreenAndPc;
                _st.AwakeRemainingSec = 7200; // 2h 00m
                _st.LiveDownKBps = 3420.0;
                _st.LiveCpuPct = 14.2;
                _st.SmartIdleTargetSec = 300;
            }
        }

        public void SetAwake(AwakeMode mode, int durationMinutes)
        {
            _st.Awake = mode;
            _st.AwakeRemainingSec = (mode == AwakeMode.Off || durationMinutes <= 0) ? -1 : durationMinutes * 60;
            ApplyExecutionState();
        }

        public void StartCountdown(PowerTimerAction action, int minutes)
        {
            _st.CountdownAction = action;
            _st.CountdownRemainingSec = Math.Max(60, minutes * 60);
            _st.CountdownActive = true;
            _st.WarningBannerActive = false;
            _warningFromIdle = false;
        }

        public void CancelCountdown()
        {
            _st.CountdownActive = false;
            _st.CountdownRemainingSec = 0;
            if (!_warningFromIdle) _st.WarningBannerActive = false;
        }

        public void SetSmartIdle(bool enabled, PowerTimerAction action, int minutes)
        {
            _st.SmartIdleEnabled = enabled;
            _st.SmartIdleAction = action;
            _st.SmartIdleTargetSec = Math.Max(60, minutes * 60);
            _st.SmartIdleElapsedSec = 0;
            if (!enabled && _warningFromIdle)
            {
                _st.WarningBannerActive = false;
                _warningFromIdle = false;
            }
        }

        public void CancelWarningBanner()
        {
            _st.WarningBannerActive = false;
            _st.WarningSecondsLeft = 0;
            if (_warningFromIdle)
            {
                _st.SmartIdleEnabled = false;
                _st.SmartIdleElapsedSec = 0;
                _warningFromIdle = false;
            }
            else
            {
                _st.CountdownActive = false;
                _st.CountdownRemainingSec = 0;
            }
        }

        public void ApplyExecutionState()
        {
            if (_demoMode) return;
            try
            {
                uint flags = WinNative.ES_CONTINUOUS;
                if (_st.Awake == AwakeMode.ScreenAndPc) flags |= WinNative.ES_SYSTEM_REQUIRED | WinNative.ES_DISPLAY_REQUIRED;
                else if (_st.Awake == AwakeMode.PcOnly) flags |= WinNative.ES_SYSTEM_REQUIRED;
                WinNative.SetThreadExecutionState(flags);
            }
            catch { }
        }

        // Called every 1 second; returns action to execute NOW only if grace countdown hits 0
        public PowerTimerAction? TickOneSecond(out bool executeNow)
        {
            executeNow = false;
            if (_demoMode) return null;

            SampleTelemetry();

            // 1. Keep-Awake auto-expiration
            if (_st.Awake != AwakeMode.Off && _st.AwakeRemainingSec > 0)
            {
                _st.AwakeRemainingSec--;
                if (_st.AwakeRemainingSec == 0)
                {
                    _st.Awake = AwakeMode.Off;
                    _st.AwakeRemainingSec = -1;
                    ApplyExecutionState();
                }
            }
            else if (_st.Awake != AwakeMode.Off)
            {
                ApplyExecutionState();
            }

            // 2. Active Warning Grace Banner (final 60s before Sleep / Shutdown)
            if (_st.WarningBannerActive)
            {
                _st.WarningSecondsLeft--;
                if (_st.WarningSecondsLeft <= 0)
                {
                    PowerTimerAction act = _st.WarningAction;
                    _st.WarningBannerActive = false;
                    _st.CountdownActive = false;
                    _st.SmartIdleEnabled = false;
                    _st.SmartIdleElapsedSec = 0;
                    _warningFromIdle = false;
                    executeNow = true;
                    return act;
                }
                return null;
            }

            // 3. Explicit Countdown Timer
            if (_st.CountdownActive)
            {
                _st.CountdownRemainingSec--;
                if (_st.CountdownAction == PowerTimerAction.MonitorsOff)
                {
                    if (_st.CountdownRemainingSec <= 0)
                    {
                        _st.CountdownActive = false;
                        executeNow = true;
                        return PowerTimerAction.MonitorsOff;
                    }
                }
                else if (_st.CountdownRemainingSec <= WarningGraceSeconds)
                {
                    _warningFromIdle = false;
                    _st.WarningBannerActive = true;
                    _st.WarningSecondsLeft = Math.Max(1, _st.CountdownRemainingSec);
                    _st.WarningAction = _st.CountdownAction;
                    _st.WarningReason = "timer";
                }
            }

            // 4. Smart Idle-Finished Watcher (Download < 1 Mbps & CPU < 8%)
            if (_st.SmartIdleEnabled)
            {
                bool isIdle = EvaluateSmartIdleTick(_st.LiveDownKBps, _st.LiveCpuPct, ref _st.SmartIdleElapsedSec, _st.SmartIdleTargetSec);
                if (isIdle)
                {
                    _warningFromIdle = true;
                    _st.WarningBannerActive = true;
                    _st.WarningSecondsLeft = WarningGraceSeconds;
                    _st.WarningAction = _st.SmartIdleAction;
                    _st.WarningReason = "idle";
                    _st.SmartIdleElapsedSec = 0;
                }
            }

            return null;
        }

        // Pure testable Smart-Idle transition rule
        public static bool EvaluateSmartIdleTick(double downKBps, double cpuPct, ref int elapsedSec, int targetSec)
        {
            if (double.IsNaN(downKBps) || double.IsInfinity(downKBps)) downKBps = 0;
            if (double.IsNaN(cpuPct) || double.IsInfinity(cpuPct)) cpuPct = 0;
            if (downKBps < IdleDownThresholdKBps && cpuPct < IdleCpuThresholdPct)
            {
                elapsedSec++;
                return elapsedSec >= Math.Max(1, targetSec);
            }
            elapsedSec = 0;
            return false;
        }

        public static void ExecutePowerAction(PowerTimerAction action)
        {
            if (action == PowerTimerAction.MonitorsOff)
            {
                DisplayEngine.TurnOffMonitorsNow();
                return;
            }
            // Clear Keep-Awake flag before sleeping/shutting down
            try { WinNative.SetThreadExecutionState(WinNative.ES_CONTINUOUS); } catch { }

            if (action == PowerTimerAction.Sleep)
            {
                try { WinNative.SetSuspendState(false, false, false); } catch { }
            }
            else if (action == PowerTimerAction.Shutdown)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("shutdown.exe", "/s /t 0");
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                }
                catch { }
            }
        }

        private void SampleTelemetry()
        {
            // CPU % via GetSystemTimes (zero WMI / zero PerfCounter overhead)
            WinNative.FILETIME ftIdle, ftKern, ftUser;
            if (WinNative.GetSystemTimes(out ftIdle, out ftKern, out ftUser))
            {
                ulong idle = ToU64(ftIdle);
                ulong kern = ToU64(ftKern);
                ulong user = ToU64(ftUser);
                if (_hasCpuBase)
                {
                    ulong dIdle = idle - _prevIdle;
                    ulong dTotal = (kern - _prevKernel) + (user - _prevUser);
                    if (dTotal > 0)
                    {
                        double busy = Math.Max(0.0, Math.Min(100.0, (1.0 - (double)dIdle / dTotal) * 100.0));
                        _st.LiveCpuPct = busy;
                    }
                }
                _prevIdle = idle; _prevKernel = kern; _prevUser = user;
                _hasCpuBase = true;
            }

            // Network download KB/s
            long curRx = 0;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string desc = (ni.Description ?? "").ToLowerInvariant();
                    if (desc.Contains("loopback") || desc.Contains("virtualbox") || desc.Contains("vmware") ||
                        desc.Contains("hyper-v") || desc.Contains("bluetooth")) continue;
                    try { curRx += ni.GetIPStatistics().BytesReceived; } catch { }
                }
            }
            catch { }

            if (!_netWatch.IsRunning)
            {
                _prevRx = curRx;
                _netWatch.Start();
            }
            else
            {
                double secs = _netWatch.Elapsed.TotalSeconds;
                if (secs >= 0.4)
                {
                    long dRx = Math.Max(0, curRx - _prevRx);
                    _st.LiveDownKBps = (dRx / 1024.0) / secs;
                    _prevRx = curRx;
                    _netWatch.Restart();
                }
            }
        }

        private static ulong ToU64(WinNative.FILETIME ft)
        {
            return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
        }

        public static string FormatDuration(int totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            int h = totalSeconds / 3600;
            int m = (totalSeconds % 3600) / 60;
            int s = totalSeconds % 60;
            if (h > 0) return Loc.F("{0:00}:{1:00}:{2:00}", h, m, s);
            return Loc.F("{0:00}:{1:00}", m, s);
        }

        public void Dispose()
        {
            if (!_demoMode)
            {
                try { WinNative.SetThreadExecutionState(WinNative.ES_CONTINUOUS); } catch { }
            }
        }
    }
}
