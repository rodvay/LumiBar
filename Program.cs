using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;

namespace LumiBar
{
    internal sealed class LaunchConfig
    {
        public bool DemoMode;
        public string ForceLang;
        public int ForceTab = -1;
        public bool StartMinimized;

        public static LaunchConfig Parse(string[] args)
        {
            LaunchConfig c = new LaunchConfig();
            if (args == null) return c;
            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();
                if (string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase)) c.DemoMode = true;
                else if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)) c.StartMinimized = true;
                else if (a.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase)) c.ForceLang = a.Substring(7).Trim().ToLowerInvariant();
                else if (a.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase))
                {
                    int tv;
                    if (int.TryParse(a.Substring(6).Trim(), out tv) && tv >= 0 && tv <= 1) c.ForceTab = tv;
                }
            }
            return c;
        }
    }

    internal static class UiTheme
    {
        public static readonly Color Bg = Color.FromArgb(11, 15, 22);
        public static readonly Color Surface = Color.FromArgb(17, 23, 33);
        public static readonly Color SurfaceAlt = Color.FromArgb(22, 30, 44);
        public static readonly Color Hover = Color.FromArgb(29, 40, 58);
        public static readonly Color Border = Color.FromArgb(36, 48, 68);
        public static readonly Color BorderLight = Color.FromArgb(56, 72, 100);
        public static readonly Color Text = Color.FromArgb(241, 245, 249);
        public static readonly Color Muted = Color.FromArgb(148, 163, 184);
        public static readonly Color Subtle = Color.FromArgb(100, 116, 139);
        public static readonly Color Amber = Color.FromArgb(251, 191, 36);
        public static readonly Color Orange = Color.FromArgb(249, 115, 22);
        public static readonly Color Cyan = Color.FromArgb(56, 189, 248);
        public static readonly Color Emerald = Color.FromArgb(52, 211, 153);
        public static readonly Color Violet = Color.FromArgb(167, 139, 250);
        public static readonly Color Coral = Color.FromArgb(248, 113, 113);

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        public static void FillRounded(Graphics g, RectangleF r, float rad, Color fill)
        {
            using (GraphicsPath p = RoundedRect(r, rad))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
        }

        public static void DrawRounded(Graphics g, RectangleF r, float rad, Color stroke, float width)
        {
            using (GraphicsPath p = RoundedRect(r, rad))
            using (Pen pen = new Pen(stroke, width))
                g.DrawPath(pen, p);
        }
    }

    internal sealed class SliderSlot
    {
        public string Id;
        public Rectangle HitRect;
        public Rectangle TrackRect;
        public int MinVal;
        public int MaxVal;
        public int MonitorIndex;
        public string Kind; // "br", "ct", "tint"
    }

    internal sealed class MainForm : Form
    {
        private const string AppVersion = "1.0.1";

        private readonly LaunchConfig _launch;
        private readonly LumiBarSettings _cfg;
        private readonly DisplayEngine _display;
        private readonly PowerGuardEngine _power;

        private string _lang = "en";
        private int _tab = 0; // 0=Displays & Tint, 1=Awake & Timer
        private int _awakeDurMin = 0; // 0=Unlimited, 30, 60, 120, 240
        private PowerTimerAction _timerAction = PowerTimerAction.Sleep;
        private int _timerSelectedMin = 60;
        private int _scrollMonitors;
        private int _maxScrollMonitors;

        private float _scale = 1.0f;
        private string _hoverId = "";
        private SliderSlot _activeSlider;
        private readonly Dictionary<string, Rectangle> _hitRects = new Dictionary<string, Rectangle>();
        private readonly List<SliderSlot> _sliders = new List<SliderSlot>();

        private bool _allowRealClose;
        private bool _startupCached;
        private string _toastText = "";
        private DateTime _toastUntil = DateTime.MinValue;

        private System.Windows.Forms.Timer _tickTimer;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;

        // Tracked GDI Fonts (zero leaks)
        private Font _fTitle;
        private Font _fVer;
        private Font _fSub;
        private Font _fSection;
        private Font _fCardTitle;
        private Font _fBody;
        private Font _fSmall;
        private Font _fPill;
        private Font _fBigNum;
        private Font _fMono;

        public MainForm(LaunchConfig launch)
        {
            _launch = launch ?? new LaunchConfig();
            _cfg = LumiBarSettings.Load();

            if (!string.IsNullOrEmpty(_launch.ForceLang) && (_launch.ForceLang == "en" || _launch.ForceLang == "tr" || _launch.ForceLang == "zh"))
                _lang = _launch.ForceLang;
            else
                _lang = _cfg.Language;

            if (_launch.ForceTab >= 0 && _launch.ForceTab <= 1) _tab = _launch.ForceTab;

            _display = new DisplayEngine(_launch.DemoMode);
            _display.RefreshMonitors();
            _power = new PowerGuardEngine(_launch.DemoMode);

            if (_launch.DemoMode)
            {
                _display.SetWarmTint(30);
                _awakeDurMin = 120;
                _power.SetSmartIdle(true, PowerTimerAction.Sleep, 5);
            }
            else
            {
                if (_cfg.WarmTint > 0) _display.SetWarmTint(_cfg.WarmTint);
                if (_cfg.AwakeMode > 0) _power.SetAwake((AwakeMode)_cfg.AwakeMode, 0);
                _power.SetSmartIdle(false, (PowerTimerAction)_cfg.IdleAction, _cfg.IdleMinutes);
            }
            _startupCached = WinNative.IsStartupEnabled();

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Text = "LumiBar v" + AppVersion;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Text;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _scale = ComputeScale();
            RebuildFonts();
            ClientSize = new Size(S(840), S(606));
            MinimumSize = new Size(S(800), S(590));

            InitTray();

            _tickTimer = new System.Windows.Forms.Timer();
            _tickTimer.Interval = 1000;
            _tickTimer.Tick += OnPowerTick;
            _tickTimer.Start();
        }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        private float ComputeScale()
        {
            try
            {
                if (IsHandleCreated)
                {
                    uint d = WinNative.GetDpiForWindow(Handle);
                    if (d >= 72 && d <= 480) return d / 96f;
                }
            }
            catch { }
            try
            {
                using (Graphics g = CreateGraphics())
                    if (g.DpiX >= 72f) return g.DpiX / 96f;
            }
            catch { }
            return 1.0f;
        }

        private void RebuildFonts()
        {
            string fam = Loc.FontFamily(_lang);
            float px = (96f / 72f) * _scale;
            Font oTitle = _fTitle, oVer = _fVer, oSub = _fSub, oSec = _fSection, oCard = _fCardTitle;
            Font oBody = _fBody, oSmall = _fSmall, oPill = _fPill, oBig = _fBigNum, oMono = _fMono;

            _fTitle = new Font("Segoe UI", 14.0f * px, FontStyle.Bold, GraphicsUnit.Pixel);
            _fVer = new Font("Consolas", 8.0f * px, FontStyle.Bold, GraphicsUnit.Pixel);
            _fSub = new Font(fam, 8.0f * px, FontStyle.Regular, GraphicsUnit.Pixel);
            _fSection = new Font(fam, 8.2f * px, FontStyle.Bold, GraphicsUnit.Pixel);
            _fCardTitle = new Font(fam, 9.8f * px, FontStyle.Bold, GraphicsUnit.Pixel);
            _fBody = new Font(fam, 8.6f * px, FontStyle.Regular, GraphicsUnit.Pixel);
            _fSmall = new Font(fam, 7.6f * px, FontStyle.Regular, GraphicsUnit.Pixel);
            _fPill = new Font(fam, 8.1f * px, FontStyle.Bold, GraphicsUnit.Pixel);
            _fBigNum = new Font("Consolas", 13.5f * px, FontStyle.Bold, GraphicsUnit.Pixel);
            _fMono = new Font("Consolas", 9.0f * px, FontStyle.Bold, GraphicsUnit.Pixel);

            if (oTitle != null) oTitle.Dispose();
            if (oVer != null) oVer.Dispose();
            if (oSub != null) oSub.Dispose();
            if (oSec != null) oSec.Dispose();
            if (oCard != null) oCard.Dispose();
            if (oBody != null) oBody.Dispose();
            if (oSmall != null) oSmall.Dispose();
            if (oPill != null) oPill.Dispose();
            if (oBig != null) oBig.Dispose();
            if (oMono != null) oMono.Dispose();
        }

        private void DisposeFonts()
        {
            if (_fTitle != null) { _fTitle.Dispose(); _fTitle = null; }
            if (_fVer != null) { _fVer.Dispose(); _fVer = null; }
            if (_fSub != null) { _fSub.Dispose(); _fSub = null; }
            if (_fSection != null) { _fSection.Dispose(); _fSection = null; }
            if (_fCardTitle != null) { _fCardTitle.Dispose(); _fCardTitle = null; }
            if (_fBody != null) { _fBody.Dispose(); _fBody = null; }
            if (_fSmall != null) { _fSmall.Dispose(); _fSmall = null; }
            if (_fPill != null) { _fPill.Dispose(); _fPill = null; }
            if (_fBigNum != null) { _fBigNum.Dispose(); _fBigNum = null; }
            if (_fMono != null) { _fMono.Dispose(); _fMono = null; }
        }

        private void InitTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = Icon ?? SystemIcons.Application;
            _trayIcon.Text = "LumiBar v" + AppVersion;
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Visible = !_launch.DemoMode;
            _trayIcon.DoubleClick += delegate { RestoreFromTray(); };
            RebuildTrayMenu();
        }

        private void RebuildTrayMenu()
        {
            if (_trayMenu == null) return;
            _trayMenu.Items.Clear();
            _trayMenu.Items.Add(Loc.T(_lang, "Open LumiBar", "LumiBar'ı Aç", "打开 LumiBar"), null, delegate { RestoreFromTray(); });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(Loc.T(_lang, "Brightness: 25% (Night)", "Parlaklık: %25 (Gece)", "亮度：25%（夜间）"), null, delegate { ApplyPresetAll(25); });
            _trayMenu.Items.Add(Loc.T(_lang, "Brightness: 50% (Soft)", "Parlaklık: %50 (Yumuşak)", "亮度：50%（柔和）"), null, delegate { ApplyPresetAll(50); });
            _trayMenu.Items.Add(Loc.T(_lang, "Brightness: 75% (Work)", "Parlaklık: %75 (Çalışma)", "亮度：75%（办公）"), null, delegate { ApplyPresetAll(75); });
            _trayMenu.Items.Add(Loc.T(_lang, "Brightness: 100% (Full)", "Parlaklık: %100 (Tam)", "亮度：100%（最亮）"), null, delegate { ApplyPresetAll(100); });
            _trayMenu.Items.Add(new ToolStripSeparator());
            bool awakeOn = _power.Status.Awake != AwakeMode.Off;
            _trayMenu.Items.Add(awakeOn
                ? Loc.T(_lang, "Keep-Awake: ON (Click to Turn Off)", "Uyanık Tut: AÇIK (Kapat)", "防休眠：已开启（点击关闭）")
                : Loc.T(_lang, "Keep-Awake: OFF (Click to Enable)", "Uyanık Tut: KAPALI (Aç)", "防休眠：已关闭（点击开启）"),
                null, delegate
                {
                    _awakeDurMin = 0;
                    _power.SetAwake(awakeOn ? AwakeMode.Off : AwakeMode.ScreenAndPc, 0);
                    _cfg.AwakeMode = (int)_power.Status.Awake;
                    if (!_launch.DemoMode) _cfg.Save();
                    RebuildTrayMenu();
                    Invalidate();
                });
            _trayMenu.Items.Add(Loc.T(_lang, "Turn Off Monitors Now", "Ekranları Şimdi Kapat", "立即关闭显示器"), null, delegate { TriggerMonitorsOff(); });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(Loc.T(_lang, "Exit (Restore Gamma)", "Çıkış (Gammayı Sıfırla)", "退出（自动恢复色温）"), null, delegate { ExitAppCleanly(); });
        }

        private void OnPowerTick(object sender, EventArgs e)
        {
            bool toastExpired = false;
            if (!string.IsNullOrEmpty(_toastText) && DateTime.UtcNow >= _toastUntil)
            {
                _toastText = "";
                toastExpired = true;
            }

            if (_launch.DemoMode)
            {
                if (toastExpired) Invalidate();
                return;
            }

            bool wasWarn = _power.Status.WarningBannerActive;
            bool execNow;
            PowerTimerAction? act = _power.TickOneSecond(out execNow);

            if (!wasWarn && _power.Status.WarningBannerActive)
            {
                RestoreFromTray();
            }

            if (execNow && act.HasValue)
            {
                PowerGuardEngine.ExecutePowerAction(act.Value);
            }

            if (toastExpired || _tab == 1 || _power.Status.WarningBannerActive || _power.Status.CountdownActive || _power.Status.Awake != AwakeMode.Off)
                Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && _power.Status.WarningBannerActive)
            {
                _power.CancelWarningBanner();
                ShowToast(Loc.T(_lang, "Scheduled power action cancelled.", "Zamanlanmış güç işlemi iptal edildi.", "已取消定时电源操作。"));
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F5)
            {
                _display.RefreshMonitors();
                if (_cfg.WarmTint > 0) _display.SetWarmTint(_cfg.WarmTint);
                ShowToast(Loc.T(_lang,
                    Loc.F("Refreshed: {0} display(s).", _display.Monitors.Count),
                    Loc.F("Yenilendi: {0} monitör bulundu.", _display.Monitors.Count),
                    Loc.F("已刷新：检测到 {0} 台显示器。", _display.Monitors.Count)));
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.D1) { _tab = 0; Invalidate(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.D2) { _tab = 1; Invalidate(); e.Handled = true; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _scale = ComputeScale();
            RebuildFonts();
            MinimumSize = new Size(S(800), S(590));
            WinNative.ApplyDarkTitleBar(Handle, UiTheme.Bg, UiTheme.Text);
            WinNative.AllowWakeMessage(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_launch.StartMinimized && !_launch.DemoMode)
            {
                Hide();
                ShowInTaskbar = false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WinNative.WM_DPICHANGED)
            {
                int newDpi = m.WParam.ToInt32() & 0xFFFF;
                if (newDpi >= 72 && newDpi <= 480)
                {
                    _scale = newDpi / 96f;
                    RebuildFonts();
                    MinimumSize = new Size(S(800), S(590));
                    Invalidate();
                }
            }
            else if (WinNative.WM_SHOW_EXISTING != 0 && (uint)m.Msg == WinNative.WM_SHOW_EXISTING)
            {
                RestoreFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowRealClose && !_launch.DemoMode && _cfg.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_tickTimer != null) { _tickTimer.Stop(); _tickTimer.Dispose(); _tickTimer = null; }
                if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
                if (_trayMenu != null) { _trayMenu.Dispose(); _trayMenu = null; }
                _power.Dispose();
                _display.Dispose();
            }
            base.Dispose(disposing);
            if (disposing) DisposeFonts();
        }

        private void ShowToast(string msg)
        {
            _toastText = msg ?? "";
            _toastUntil = DateTime.UtcNow.AddSeconds(4.0);
            Invalidate();
        }

        private void SwitchLanguage(string lang)
        {
            if (_lang == lang) return;
            _lang = lang;
            _cfg.Language = lang;
            if (!_launch.DemoMode) _cfg.Save();
            RebuildFonts();
            RebuildTrayMenu();
            Invalidate();
        }

        private void ApplyPresetAll(int pct)
        {
            _display.SetAllBrightness(pct);
            ShowToast(Loc.T(_lang,
                Loc.F("All monitors set to {0}% brightness.", pct),
                Loc.F("Tüm monitörler %{0} parlaklığa ayarlandı.", pct),
                Loc.F("所有显示器亮度已设为 {0}%。", pct)));
        }

        private void TriggerMonitorsOff()
        {
            if (_launch.DemoMode)
            {
                ShowToast(Loc.T(_lang, "Demo: Monitors would turn off (move mouse to wake).",
                    "Demo: Monitörler kapatılır (uyandırmak için fareyi oynatın).",
                    "演示：将关闭显示器（移动鼠标唤醒）。"));
                return;
            }
            ShowToast(Loc.T(_lang, "Turning off monitors...", "Monitörler kapatılıyor...", "正在关闭显示器..."));
            DisplayEngine.TurnOffMonitorsNow();
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ExitAppCleanly()
        {
            _allowRealClose = true;
            Close();
        }

        private int MeasurePillW(Graphics g, string text, Font f, int padX)
        {
            return (int)Math.Ceiling(g.MeasureString(text, f).Width) + S(padX * 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(UiTheme.Bg);
            _hitRects.Clear();
            _sliders.Clear();

            int pad = S(16);
            int right = ClientSize.Width - pad;

            using (StringFormat sfNear = new StringFormat(StringFormatFlags.NoWrap))
            using (StringFormat sfCenter = new StringFormat(StringFormatFlags.NoWrap))
            using (StringFormat sfFar = new StringFormat(StringFormatFlags.NoWrap))
            {
                sfNear.LineAlignment = StringAlignment.Center;
                sfNear.Trimming = StringTrimming.EllipsisCharacter;
                sfCenter.Alignment = StringAlignment.Center;
                sfCenter.LineAlignment = StringAlignment.Center;
                sfFar.Alignment = StringAlignment.Far;
                sfFar.LineAlignment = StringAlignment.Center;

                // ── 1. HEADER BAR ──
                int hdrH = S(56);
                using (SolidBrush bHdr = new SolidBrush(UiTheme.Surface))
                using (Pen pB = new Pen(UiTheme.Border, 1f))
                {
                    g.FillRectangle(bHdr, 0, 0, ClientSize.Width, hdrH);
                    g.DrawLine(pB, 0, hdrH - 1, ClientSize.Width, hdrH - 1);
                }

                // Sun emblem
                RectangleF icoRc = new RectangleF(pad, S(13), S(30), S(30));
                UiTheme.FillRounded(g, icoRc, S(8), Color.FromArgb(30, 26, 40));
                UiTheme.DrawRounded(g, icoRc, S(8), UiTheme.Amber, 1.3f);
                DrawSunIcon(g, icoRc.X + icoRc.Width / 2f, icoRc.Y + icoRc.Height / 2f, S(4.6f), S(7.2f), S(10.0f), UiTheme.Amber);

                int titleX = pad + S(38);
                SizeF tSz = g.MeasureString("LumiBar", _fTitle);
                using (SolidBrush bt = new SolidBrush(UiTheme.Text))
                    g.DrawString("LumiBar", _fTitle, bt, new RectangleF(titleX, S(6), tSz.Width + S(8), S(26)), sfNear);

                string verTxt = "v" + AppVersion;
                float vw = g.MeasureString(verTxt, _fVer).Width + S(10);
                RectangleF verRc = new RectangleF(titleX + tSz.Width + S(4), S(11), vw, S(17));
                UiTheme.FillRounded(g, verRc, S(5), Color.FromArgb(30, UiTheme.Amber));
                using (SolidBrush bv = new SolidBrush(UiTheme.Amber))
                    g.DrawString(verTxt, _fVer, bv, verRc, sfCenter);

                // Right-to-left: Language pill + 2 Tab buttons FIRST so subtitle never overlaps
                int rx = right;
                int btnH = S(28);
                int btnY = (hdrH - btnH) / 2;

                int lW1 = S(32), lW2 = S(32), lW3 = S(40);
                int langW = lW1 + lW2 + lW3;
                Rectangle langOuter = new Rectangle(rx - langW, btnY, langW, btnH);
                UiTheme.FillRounded(g, langOuter, S(7), UiTheme.SurfaceAlt);
                UiTheme.DrawRounded(g, langOuter, S(7), UiTheme.Border, 1f);
                DrawLangSeg(g, new Rectangle(langOuter.X, btnY, lW1, btnH), "EN", "en", sfCenter);
                DrawLangSeg(g, new Rectangle(langOuter.X + lW1, btnY, lW2, btnH), "TR", "tr", sfCenter);
                DrawLangSeg(g, new Rectangle(langOuter.X + lW1 + lW2, btnY, lW3, btnH), "中文", "zh", sfCenter);
                rx = langOuter.Left - S(10);

                string tTab1 = Loc.T(_lang, "Awake & Timer", "Uyanık & Zamanlayıcı", "防休眠与定时");
                string tTab0 = Loc.T(_lang, "Displays & Tint", "Ekranlar & Ton", "显示器与色温");
                int wT1 = MeasurePillW(g, tTab1, _fPill, 11);
                int wT0 = MeasurePillW(g, tTab0, _fPill, 11);
                Rectangle tab1Rc = new Rectangle(rx - wT1, btnY, wT1, btnH);
                Rectangle tab0Rc = new Rectangle(tab1Rc.Left - S(6) - wT0, btnY, wT0, btnH);
                DrawPill(g, "tab_0", tab0Rc, tTab0, UiTheme.Amber, _tab == 0, sfCenter);
                DrawPill(g, "tab_1", tab1Rc, tTab1, UiTheme.Cyan, _tab == 1, sfCenter);

                string subTxt = Loc.T(_lang, "DDC/CI Brightness · Night Tint · Power Guard",
                    "DDC/CI Parlaklık · Gece Tonu · Güç Koruma",
                    "DDC/CI 硬件亮度 · 护眼色温 · 防休眠定时");
                int subMaxW = Math.Max(S(80), tab0Rc.Left - titleX - S(10));
                using (SolidBrush bs = new SolidBrush(UiTheme.Muted))
                    g.DrawString(subTxt, _fSub, bs, new RectangleF(titleX, S(31), subMaxW, S(18)), sfNear);

                int contentTop = hdrH + S(12);
                int footerH = S(44);
                int contentBottom = ClientSize.Height - footerH - S(6);

                // Optional 60s Warning Banner
                if (_power.Status.WarningBannerActive)
                {
                    int warnH = S(44);
                    Rectangle warnRc = new Rectangle(pad, contentTop, ClientSize.Width - pad * 2, warnH);
                    UiTheme.FillRounded(g, warnRc, S(9), Color.FromArgb(42, UiTheme.Coral));
                    UiTheme.DrawRounded(g, warnRc, S(9), UiTheme.Coral, 1.5f);

                    string actLbl = _power.Status.WarningAction == PowerTimerAction.Shutdown
                        ? Loc.T(_lang, "Shutdown", "Kapatma", "关机")
                        : Loc.T(_lang, "Sleep", "Uyku", "睡眠");
                    string warnMsg = Loc.T(_lang,
                        Loc.F("WARNING: System {0} in {1}s!", actLbl, _power.Status.WarningSecondsLeft),
                        Loc.F("UYARI: Sistem {1} sn içinde {0} moduna geçecek!", actLbl, _power.Status.WarningSecondsLeft),
                        Loc.F("警告：系统将在 {1} 秒后执行{0}！", actLbl, _power.Status.WarningSecondsLeft));

                    string cancelTxt = Loc.T(_lang, "CANCEL NOW", "HEMEN İPTAL ET", "立即取消");
                    int cw = MeasurePillW(g, cancelTxt, _fPill, 12);
                    Rectangle cancelRc = new Rectangle(warnRc.Right - cw - S(10), warnRc.Y + S(7), cw, S(30));
                    _hitRects["warn_cancel"] = cancelRc;
                    UiTheme.FillRounded(g, cancelRc, S(7), UiTheme.Coral);
                    using (SolidBrush bc = new SolidBrush(Color.FromArgb(15, 23, 42)))
                        g.DrawString(cancelTxt, _fPill, bc, cancelRc, sfCenter);

                    using (SolidBrush bw = new SolidBrush(UiTheme.Text))
                        g.DrawString(warnMsg, _fCardTitle, bw, new RectangleF(warnRc.X + S(14), warnRc.Y, cancelRc.Left - warnRc.X - S(22), warnRc.Height), sfNear);

                    contentTop += warnH + S(10);
                }

                if (_tab == 0)
                    PaintDisplaysTab(g, pad, contentTop, ClientSize.Width - pad * 2, contentBottom - contentTop, sfNear, sfCenter, sfFar);
                else
                    PaintPowerTab(g, pad, contentTop, ClientSize.Width - pad * 2, contentBottom - contentTop, sfNear, sfCenter, sfFar);

                // ── FOOTER BAR ──
                PaintFooter(g, pad, ClientSize.Height - footerH, ClientSize.Width - pad * 2, footerH, sfNear, sfCenter);
            }
        }

        private void PaintDisplaysTab(Graphics g, int x, int y, int w, int h, StringFormat sfNear, StringFormat sfCenter, StringFormat sfFar)
        {
            // 1. Quick Preset & Link Strip
            int stripH = S(34);
            int px = x;
            string[,] presets = new string[,]
            {
                { "20", Loc.T(_lang, "Night 20%", "Gece %20", "夜间 20%") },
                { "45", Loc.T(_lang, "Soft 45%", "Yumuşak %45", "柔和 45%") },
                { "70", Loc.T(_lang, "Work 70%", "Çalışma %70", "办公 70%") },
                { "100", Loc.T(_lang, "Full 100%", "Tam %100", "全亮 100%") }
            };
            for (int i = 0; i < presets.GetLength(0); i++)
            {
                int pw = MeasurePillW(g, presets[i, 1], _fPill, 9);
                Rectangle pr = new Rectangle(px, y + S(2), pw, S(28));
                DrawPill(g, "pre_" + presets[i, 0], pr, presets[i, 1], UiTheme.Amber, false, sfCenter);
                px = pr.Right + S(6);
            }

            // Right: Link Monitors + Monitors Off
            int rx = x + w;
            string offTxt = Loc.T(_lang, "Monitors Off", "Ekranları Kapat", "关闭显示器");
            int offW = MeasurePillW(g, offTxt, _fPill, 10);
            Rectangle offRc = new Rectangle(rx - offW, y + S(2), offW, S(28));
            DrawPill(g, "act_mon_off", offRc, offTxt, UiTheme.Violet, false, sfCenter);
            rx = offRc.Left - S(8);

            string linkTxt = Loc.T(_lang, "Link All", "Tümünü Bağla", "同步联动") + ": " +
                (_cfg.LinkMonitors ? Loc.T(_lang, "ON", "AÇIK", "开") : Loc.T(_lang, "OFF", "KAPALI", "关"));
            int linkW = MeasurePillW(g, linkTxt, _fPill, 9);
            Rectangle linkRc = new Rectangle(rx - linkW, y + S(2), linkW, S(28));
            DrawPill(g, "act_link", linkRc, linkTxt, _cfg.LinkMonitors ? UiTheme.Emerald : UiTheme.Muted, _cfg.LinkMonitors, sfCenter);

            // 2. Reserve Bottom Warm Night Tint Card
            int tintCardH = S(104);
            int tintCardY = y + h - tintCardH;

            // 3. Monitor Cards List (scrollable if >2 monitors)
            int listTop = y + stripH + S(8);
            int listH = Math.Max(S(110), tintCardY - listTop - S(10));
            Rectangle listArea = new Rectangle(x, listTop, w, listH);

            int totalMonH = 0;
            for (int i = 0; i < _display.Monitors.Count; i++)
                totalMonH += (_display.Monitors[i].SupportsContrast ? S(130) : S(92)) + S(10);
            if (totalMonH > 0) totalMonH -= S(10);
            _maxScrollMonitors = Math.Max(0, totalMonH - listH);
            if (_scrollMonitors > _maxScrollMonitors) _scrollMonitors = _maxScrollMonitors;
            if (_scrollMonitors < 0) _scrollMonitors = 0;

            GraphicsState st = g.Save();
            g.SetClip(listArea);

            if (_display.Monitors.Count == 0)
            {
                using (SolidBrush be = new SolidBrush(UiTheme.Muted))
                    g.DrawString(Loc.T(_lang, "No active displays detected. Click Refresh.", "Aktif monitör algılanmadı. Yenile'ye tıklayın.", "未检测到活动显示器，请点击刷新。"),
                        _fBody, be, listArea, sfCenter);
            }
            else
            {
                int cy = listTop - _scrollMonitors;
                int cardW = _maxScrollMonitors > 0 ? w - S(10) : w;
                for (int i = 0; i < _display.Monitors.Count; i++)
                {
                    DisplayMonitorItem m = _display.Monitors[i];
                    int cardH = m.SupportsContrast ? S(130) : S(92);
                    Rectangle card = new Rectangle(x, cy, cardW, cardH);

                    if (card.Bottom >= listTop && card.Top <= listTop + listH)
                    {
                        PaintMonitorCard(g, card, listArea, m, sfNear, sfCenter, sfFar);
                    }
                    cy += cardH + S(10);
                }
            }
            g.Restore(st);

            // Scroll indicator if needed
            if (_maxScrollMonitors > 0)
            {
                int trackH = Math.Max(S(24), listH - S(8));
                int thumbH = Math.Max(S(28), (int)Math.Round((double)listH / totalMonH * trackH));
                int thumbY = listTop + S(4) + (int)Math.Round((double)_scrollMonitors / _maxScrollMonitors * (trackH - thumbH));
                UiTheme.FillRounded(g, new Rectangle(x + w - S(6), thumbY, S(5), thumbH), S(3), Color.FromArgb(72, 92, 120));
            }

            // 4. Warm Night Tint Card
            Rectangle tintRc = new Rectangle(x, tintCardY, w, tintCardH);
            PaintWarmTintCard(g, tintRc, sfNear, sfCenter, sfFar);
        }

        private void PaintMonitorCard(Graphics g, Rectangle rc, Rectangle clipArea, DisplayMonitorItem m, StringFormat sfNear, StringFormat sfCenter, StringFormat sfFar)
        {
            UiTheme.FillRounded(g, rc, S(10), UiTheme.Surface);
            UiTheme.DrawRounded(g, rc, S(10), UiTheme.Border, 1f);

            // Left accent stripe
            Color chCol = m.Channel == DisplayChannel.DdcCi ? UiTheme.Emerald
                : (m.Channel == DisplayChannel.LaptopWmi ? UiTheme.Cyan : UiTheme.Violet);
            UiTheme.FillRounded(g, new RectangleF(rc.X + S(1), rc.Y + S(12), S(3.5f), rc.Height - S(24)), S(2), chCol);

            int ix = rc.X + S(16);
            int headerY = rc.Y + S(10);

            // Monitor badge (#1, #2)
            RectangleF numRc = new RectangleF(ix, headerY + S(1), S(24), S(22));
            UiTheme.FillRounded(g, numRc, S(6), Color.FromArgb(30, UiTheme.Amber));
            using (SolidBrush bn = new SolidBrush(UiTheme.Amber))
                g.DrawString((m.Index + 1).ToString(), _fPill, bn, numRc, sfCenter);

            // Right badges: Channel + Resolution
            int rx = rc.Right - S(14);
            string chLbl = m.Channel == DisplayChannel.DdcCi ? "DDC/CI"
                : (m.Channel == DisplayChannel.LaptopWmi ? "WMI" : Loc.T(_lang, "Software", "Yazılım", "软件调光"));
            int chW = MeasurePillW(g, chLbl, _fSmall, 8);
            RectangleF chRc = new RectangleF(rx - chW, headerY + S(2), chW, S(20));
            UiTheme.FillRounded(g, chRc, S(5), Color.FromArgb(28, chCol));
            UiTheme.DrawRounded(g, chRc, S(5), Color.FromArgb(100, chCol), 1f);
            using (SolidBrush bch = new SolidBrush(chCol))
                g.DrawString(chLbl, _fSmall, bch, chRc, sfCenter);
            rx = (int)chRc.Left - S(8);

            if (!string.IsNullOrEmpty(m.Resolution))
            {
                int resW = MeasurePillW(g, m.Resolution, _fSmall, 7);
                RectangleF resRc = new RectangleF(rx - resW, headerY + S(2), resW, S(20));
                UiTheme.FillRounded(g, resRc, S(5), UiTheme.SurfaceAlt);
                using (SolidBrush bres = new SolidBrush(UiTheme.Muted))
                    g.DrawString(m.Resolution, _fSmall, bres, resRc, sfCenter);
                rx = (int)resRc.Left - S(8);
            }

            // Friendly Name + Primary tag
            int nameX = ix + S(32);
            SizeF nameSz = g.MeasureString(m.FriendlyName, _fCardTitle);
            int nameMaxW = Math.Max(S(100), rx - nameX - (m.IsPrimary ? S(72) : S(8)));
            using (SolidBrush bm = new SolidBrush(UiTheme.Text))
                g.DrawString(m.FriendlyName, _fCardTitle, bm, new RectangleF(nameX, headerY, nameMaxW, S(24)), sfNear);

            if (m.IsPrimary)
            {
                string priTxt = Loc.T(_lang, "PRIMARY", "ANA EKRAN", "主屏").ToUpper(Loc.Culture(_lang));
                int priW = MeasurePillW(g, priTxt, _fSmall, 6);
                float pxPos = nameX + Math.Min(nameMaxW, nameSz.Width) + S(8);
                if (pxPos + priW < rx)
                {
                    RectangleF pRc = new RectangleF(pxPos, headerY + S(3), priW, S(18));
                    UiTheme.FillRounded(g, pRc, S(4), Color.FromArgb(30, UiTheme.Cyan));
                    using (SolidBrush bp = new SolidBrush(UiTheme.Cyan))
                        g.DrawString(priTxt, _fSmall, bp, pRc, sfCenter);
                }
            }

            // Brightness slider row
            int row1Y = rc.Y + S(46);
            int minBr = m.Channel == DisplayChannel.SoftwareGamma ? 20 : 0;
            DrawLabeledSlider(g, "br_" + m.Index, "br", m.Index,
                ix, row1Y, rc.Width - S(30),
                Loc.T(_lang, "Brightness", "Parlaklık", "亮度"),
                m.Brightness, minBr, 100, m.Brightness + "%",
                UiTheme.Amber, UiTheme.Orange, clipArea, sfNear, sfFar);

            // Contrast slider row (when supported by DDC/CI)
            if (m.SupportsContrast)
            {
                int row2Y = rc.Y + S(84);
                DrawLabeledSlider(g, "ct_" + m.Index, "ct", m.Index,
                    ix, row2Y, rc.Width - S(30),
                    Loc.T(_lang, "Contrast", "Kontrast", "对比度"),
                    m.Contrast, 0, 100, m.Contrast + "%",
                    UiTheme.Cyan, UiTheme.Violet, clipArea, sfNear, sfFar);
            }
        }

        private void PaintWarmTintCard(Graphics g, Rectangle rc, StringFormat sfNear, StringFormat sfCenter, StringFormat sfFar)
        {
            UiTheme.FillRounded(g, rc, S(10), UiTheme.Surface);
            UiTheme.DrawRounded(g, rc, S(10), _display.WarmTintPct > 0 ? Color.FromArgb(140, UiTheme.Orange) : UiTheme.Border, 1f);
            UiTheme.FillRounded(g, new RectangleF(rc.X + S(1), rc.Y + S(12), S(3.5f), rc.Height - S(24)), S(2), UiTheme.Orange);

            int ix = rc.X + S(16);
            int topY = rc.Y + S(10);

            string title = Loc.T(_lang, "Warm Night Tint (Eye Care)", "Sıcak Gece Tonu (Göz Koruma)", "夜间护眼暖色温");
            int kelvin = DisplayEngine.WarmTintToKelvin(_display.WarmTintPct);
            string kBadge = kelvin + "K";

            // Right quick pills: Off / Warm / Night / Reset (measure with _fPill to match DrawPill!)
            int rx = rc.Right - S(14);
            string[,] tPills = new string[,]
            {
                { "reset", Loc.T(_lang, "Reset", "Sıfırla", "重置") },
                { "65", Loc.T(_lang, "Deep 65%", "Derin %65", "深暖 65%") },
                { "35", Loc.T(_lang, "Warm 35%", "Ilık %35", "暖色 35%") },
                { "0", Loc.T(_lang, "Off", "Kapalı", "关闭") }
            };
            for (int i = 0; i < tPills.GetLength(0); i++)
            {
                string pid = tPills[i, 0];
                string plbl = tPills[i, 1];
                int pw = MeasurePillW(g, plbl, _fPill, 8);
                Rectangle pr = new Rectangle(rx - pw, topY + S(1), pw, S(22));
                bool act = (pid == "0" && _display.WarmTintPct == 0) ||
                           (pid == "35" && _display.WarmTintPct == 35) ||
                           (pid == "65" && _display.WarmTintPct == 65);
                DrawPill(g, "tint_" + pid, pr, plbl, pid == "reset" ? UiTheme.Cyan : UiTheme.Orange, act, sfCenter);
                rx = pr.Left - S(5);
            }

            // Kelvin pill
            int kw = MeasurePillW(g, kBadge, _fMono, 7);
            RectangleF kRc = new RectangleF(rx - kw - S(4), topY + S(1), kw, S(22));
            UiTheme.FillRounded(g, kRc, S(5), Color.FromArgb(30, UiTheme.Orange));
            using (SolidBrush bk = new SolidBrush(UiTheme.Amber))
                g.DrawString(kBadge, _fSmall, bk, kRc, sfCenter);

            // Title clamped cleanly against Kelvin pill
            int tintTitleW = Math.Max(S(100), (int)kRc.Left - ix - S(10));
            using (SolidBrush bt = new SolidBrush(UiTheme.Text))
                g.DrawString(title, _fCardTitle, bt, new RectangleF(ix, topY, tintTitleW, S(24)), sfNear);

            DrawLabeledSlider(g, "warm_tint", "tint", -1,
                ix, rc.Y + S(48), rc.Width - S(30),
                Loc.T(_lang, "Warmth", "Sıcaklık", "暖度"),
                _display.WarmTintPct, 0, 80, _display.WarmTintPct + "%",
                UiTheme.Amber, UiTheme.Orange, rc, sfNear, sfFar);

            string hint = Loc.T(_lang,
                "Reversible hardware gamma curve — original monitor color profile is restored automatically on exit.",
                "Geri alınabilir gamma eğrisi — uygulamadan çıkınca orijinal ekran renk profili otomatik geri yüklenir.",
                "可逆硬件 Gamma 曲线 — 退出 LumiBar 时将自动恢复显示器原始色温配置。");
            using (SolidBrush bh = new SolidBrush(UiTheme.Subtle))
                g.DrawString(hint, _fSmall, bh, new RectangleF(ix, rc.Y + S(80), rc.Width - S(32), S(16)), sfNear);
        }

        private void DrawLabeledSlider(Graphics g, string id, string kind, int monitorIndex,
            int x, int y, int w, string label, int value, int minVal, int maxVal, string readout,
            Color colLeft, Color colRight, Rectangle clipArea, StringFormat sfNear, StringFormat sfFar)
        {
            int lblW = S(86);
            int valW = S(48);
            int trackX = x + lblW;
            int trackW = Math.Max(S(120), w - lblW - valW - S(12));
            int rowH = S(28);
            int trackH = S(8);
            int trackY = y + (rowH - trackH) / 2;

            using (SolidBrush bl = new SolidBrush(UiTheme.Muted))
                g.DrawString(label, _fBody, bl, new RectangleF(x, y, lblW, rowH), sfNear);

            Rectangle trackRc = new Rectangle(trackX, trackY, trackW, trackH);
            Rectangle hitRc = new Rectangle(trackX - S(8), y, trackW + S(16), rowH);
            Rectangle clippedHit = Rectangle.Intersect(hitRc, clipArea);
            if (clippedHit.Width >= S(20) && clippedHit.Height >= S(8))
            {
                SliderSlot slot = new SliderSlot();
                slot.Id = id; slot.Kind = kind; slot.MonitorIndex = monitorIndex;
                slot.MinVal = minVal; slot.MaxVal = maxVal;
                slot.TrackRect = trackRc; slot.HitRect = clippedHit;
                _sliders.Add(slot);
            }

            // Track background
            UiTheme.FillRounded(g, trackRc, trackH / 2f, UiTheme.SurfaceAlt);
            UiTheme.DrawRounded(g, trackRc, trackH / 2f, UiTheme.Border, 1f);

            float frac = maxVal > minVal ? (float)(value - minVal) / (maxVal - minVal) : 0f;
            frac = Math.Max(0f, Math.Min(1f, frac));
            int fillW = Math.Max(trackH, (int)Math.Round(frac * trackW));

            if (frac > 0.005f)
            {
                Rectangle fillRc = new Rectangle(trackRc.X, trackRc.Y, fillW, trackH);
                using (GraphicsPath fp = UiTheme.RoundedRect(fillRc, trackH / 2f))
                using (LinearGradientBrush lgb = new LinearGradientBrush(
                    new Point(trackRc.X, trackRc.Y), new Point(trackRc.Right, trackRc.Y), colLeft, colRight))
                {
                    g.FillPath(lgb, fp);
                }
            }

            // Thumb knob
            bool hot = (_hoverId == id || (_activeSlider != null && _activeSlider.Id == id));
            int thumbSz = hot ? S(18) : S(16);
            int thumbCx = trackRc.X + (int)Math.Round(frac * trackW);
            int thumbCy = trackRc.Y + trackH / 2;
            Rectangle thumbRc = new Rectangle(thumbCx - thumbSz / 2, thumbCy - thumbSz / 2, thumbSz, thumbSz);
            using (SolidBrush bHalo = new SolidBrush(Color.FromArgb(hot ? 65 : 30, colLeft)))
                g.FillEllipse(bHalo, thumbRc.X - S(3), thumbRc.Y - S(3), thumbRc.Width + S(6), thumbRc.Height + S(6));
            using (SolidBrush bThumb = new SolidBrush(UiTheme.Text))
            using (Pen pThumb = new Pen(colLeft, 2f))
            {
                g.FillEllipse(bThumb, thumbRc);
                g.DrawEllipse(pThumb, thumbRc);
            }

            using (SolidBrush bv = new SolidBrush(UiTheme.Text))
                g.DrawString(readout, _fMono, bv, new RectangleF(trackRc.Right + S(8), y, valW, rowH), sfFar);
        }

        private void PaintPowerTab(Graphics g, int x, int y, int w, int h, StringFormat sfNear, StringFormat sfCenter, StringFormat sfFar)
        {
            PowerGuardStatus st = _power.Status;
            bool compact = (h < S(455));
            int gap = compact ? S(8) : S(12);

            // Adaptive vertical heights so Card 3 NEVER overflows Footer even with 60s Warning Banner at MinimumSize
            int c1H = compact ? Math.Max(S(136), (int)Math.Round(h * 0.355)) : S(162);
            int c2H = compact ? Math.Max(S(106), (int)Math.Round(h * 0.280)) : S(130);
            int c3H = Math.Max(S(112), h - c1H - c2H - gap * 2);

            // ── CARD 1: KEEP-AWAKE ──
            Rectangle c1 = new Rectangle(x, y, w, c1H);
            UiTheme.FillRounded(g, c1, S(10), UiTheme.Surface);
            UiTheme.DrawRounded(g, c1, S(10), st.Awake != AwakeMode.Off ? UiTheme.Emerald : UiTheme.Border, 1f);
            UiTheme.FillRounded(g, new RectangleF(c1.X + S(1), c1.Y + S(12), S(3.5f), c1.Height - S(24)), S(2), UiTheme.Emerald);

            int ix = c1.X + S(16);
            string awakeStat = st.Awake == AwakeMode.Off
                ? Loc.T(_lang, "Inactive", "Devre Dışı", "未开启")
                : (st.AwakeRemainingSec > 0
                    ? Loc.T(_lang, "Active · ", "Aktif · ", "运行中 · ") + PowerGuardEngine.FormatDuration(st.AwakeRemainingSec)
                    : Loc.T(_lang, "Active · Unlimited", "Aktif · Süresiz", "运行中 · 持续"));
            int asW = MeasurePillW(g, awakeStat, _fSmall, 9);
            RectangleF asRc = new RectangleF(c1.Right - asW - S(14), c1.Y + (compact ? S(8) : S(12)), asW, S(22));
            Color asCol = st.Awake != AwakeMode.Off ? UiTheme.Emerald : UiTheme.Muted;
            UiTheme.FillRounded(g, asRc, S(6), Color.FromArgb(28, asCol));
            using (SolidBrush bas = new SolidBrush(asCol))
                g.DrawString(awakeStat, _fSmall, bas, asRc, sfCenter);

            using (SolidBrush bt = new SolidBrush(UiTheme.Text))
                g.DrawString(Loc.T(_lang, "Keep-Awake Guard (No Sleep)", "Uyanık Tutma Koruması (Uyku Engelle)", "防休眠守护（替代 Caffeine）"),
                    _fCardTitle, bt, new RectangleF(ix, c1.Y + (compact ? S(7) : S(10)), Math.Max(S(120), (int)asRc.Left - ix - S(10)), S(24)), sfNear);

            // 3 Mode Cards
            int mGap = S(10);
            int mW = (w - S(32) - mGap * 2) / 3;
            int mY = c1.Y + (compact ? S(36) : S(44));
            int mH = compact ? Math.Max(S(52), c1H - S(76)) : S(66);
            DrawAwakeModeCard(g, "awake_0", new Rectangle(ix, mY, mW, mH),
                Loc.T(_lang, "Off", "Kapalı", "关闭"),
                Loc.T(_lang, "Allow normal Windows sleep", "Normal Windows uyku planı", "遵循系统默认睡眠计划"),
                st.Awake == AwakeMode.Off, UiTheme.Muted, sfNear);
            DrawAwakeModeCard(g, "awake_1", new Rectangle(ix + mW + mGap, mY, mW, mH),
                Loc.T(_lang, "PC & Display Awake", "PC ve Ekran Uyanık", "系统与屏幕常亮"),
                Loc.T(_lang, "Keep screen on & prevent sleep", "Ekran kapanmaz, PC uyumaz", "阻止息屏与系统睡眠"),
                st.Awake == AwakeMode.ScreenAndPc, UiTheme.Emerald, sfNear);
            DrawAwakeModeCard(g, "awake_2", new Rectangle(ix + (mW + mGap) * 2, mY, mW, mH),
                Loc.T(_lang, "PC Awake Only", "Sadece PC Uyanık", "仅系统不休眠"),
                Loc.T(_lang, "Display sleeps, tasks keep running", "Ekran kapanır, indirme sürer", "允许息屏，后台任务不中断"),
                st.Awake == AwakeMode.PcOnly, UiTheme.Cyan, sfNear);

            // Duration strip
            int dY = c1.Bottom - S(34);
            using (SolidBrush bdl = new SolidBrush(UiTheme.Muted))
                g.DrawString(Loc.T(_lang, "Duration:", "Süre:", "持续时间:"), _fBody, bdl, new RectangleF(ix, dY, S(78), S(26)), sfNear);
            int dx = ix + S(80);
            int[] durs = new int[] { 0, 30, 60, 120, 240 };
            string[] durLbls = new string[]
            {
                Loc.T(_lang, "Unlimited", "Süresiz", "不限时"),
                Loc.T(_lang, "30m", "30 dk", "30分钟"),
                Loc.T(_lang, "1h", "1 sa", "1小时"),
                Loc.T(_lang, "2h", "2 sa", "2小时"),
                Loc.T(_lang, "4h", "4 sa", "4小时")
            };
            for (int i = 0; i < durs.Length; i++)
            {
                int dw = MeasurePillW(g, durLbls[i], _fPill, 9);
                Rectangle dr = new Rectangle(dx, dY, dw, S(26));
                DrawPill(g, "adur_" + durs[i], dr, durLbls[i], UiTheme.Emerald, _awakeDurMin == durs[i], sfCenter);
                dx = dr.Right + S(6);
            }

            // ── CARD 2: POWER COUNTDOWN TIMER ──
            int c2Y = c1.Bottom + gap;
            Rectangle c2 = new Rectangle(x, c2Y, w, c2H);
            UiTheme.FillRounded(g, c2, S(10), UiTheme.Surface);
            UiTheme.DrawRounded(g, c2, S(10), st.CountdownActive ? UiTheme.Cyan : UiTheme.Border, 1f);
            UiTheme.FillRounded(g, new RectangleF(c2.X + S(1), c2.Y + S(12), S(3.5f), c2.Height - S(24)), S(2), UiTheme.Cyan);

            string cdTxt = st.CountdownActive
                ? PowerGuardEngine.FormatDuration(st.CountdownRemainingSec)
                : Loc.T(_lang, "Ready", "Hazır", "就绪");
            using (SolidBrush bcd = new SolidBrush(st.CountdownActive ? UiTheme.Cyan : UiTheme.Subtle))
                g.DrawString(cdTxt, st.CountdownActive ? _fBigNum : _fCardTitle, bcd, new RectangleF(c2.Right - S(150), c2.Y + (compact ? S(7) : S(10)), S(136), S(26)), sfFar);

            using (SolidBrush bt = new SolidBrush(UiTheme.Text))
                g.DrawString(Loc.T(_lang, "Power Countdown Timer", "Geri Sayım Güç Zamanlayıcısı", "倒计时电源任务"),
                    _fCardTitle, bt, new RectangleF(ix, c2.Y + (compact ? S(7) : S(10)), Math.Max(S(140), c2.Right - S(162) - ix), S(24)), sfNear);

            // Action selector pills + duration pills + Start/Cancel button
            int tr1Y = c2.Y + (compact ? S(38) : S(46));
            int ax = ix;
            string[,] tActs = new string[,]
            {
                { "0", Loc.T(_lang, "Sleep", "Uyku", "睡眠") },
                { "1", Loc.T(_lang, "Shutdown", "Kapat", "关机") },
                { "2", Loc.T(_lang, "Monitors Off", "Ekran Kapat", "关闭屏幕") }
            };
            for (int i = 0; i < tActs.GetLength(0); i++)
            {
                int aw = MeasurePillW(g, tActs[i, 1], _fPill, 8);
                Rectangle ar = new Rectangle(ax, tr1Y, aw, S(28));
                bool act = ((int)_timerAction == i);
                DrawPill(g, "tact_" + i, ar, tActs[i, 1], i == 1 ? UiTheme.Coral : UiTheme.Cyan, act, sfCenter);
                ax = ar.Right + S(5);
            }

            ax += S(6);
            int[] tMins = new int[] { 15, 30, 60, 120 };
            string[] tMinLbls = new string[]
            {
                Loc.T(_lang, "15m", "15dk", "15分"),
                Loc.T(_lang, "30m", "30dk", "30分"),
                Loc.T(_lang, "1h", "1sa", "1时"),
                Loc.T(_lang, "2h", "2sa", "2时")
            };
            for (int i = 0; i < tMins.Length; i++)
            {
                int mw = MeasurePillW(g, tMinLbls[i], _fPill, 8);
                Rectangle mr = new Rectangle(ax, tr1Y, mw, S(28));
                DrawPill(g, "tmin_" + tMins[i], mr, tMinLbls[i], UiTheme.Violet, _timerSelectedMin == tMins[i], sfCenter);
                ax = mr.Right + S(5);
            }

            string startTxt = st.CountdownActive
                ? Loc.T(_lang, "Cancel Timer", "İptal Et", "取消定时")
                : Loc.T(_lang, "Start Timer", "Başlat", "开始计时");
            int sw = MeasurePillW(g, startTxt, _fPill, 11);
            Rectangle startRc = new Rectangle(c2.Right - sw - S(14), tr1Y, sw, S(28));
            DrawPill(g, "timer_toggle", startRc, startTxt, st.CountdownActive ? UiTheme.Coral : UiTheme.Cyan, true, sfCenter);

            string c2Hint = Loc.T(_lang,
                "A 60-second cancellable warning banner appears before Sleep or Shutdown executes.",
                "Uyku veya Kapatma öncesinde 60 saniyelik iptal edilebilir uyarı çubuğu açılır.",
                "执行睡眠或关机前会弹出 60 秒可随时取消的安全倒计时提醒。");
            using (SolidBrush bh = new SolidBrush(UiTheme.Subtle))
                g.DrawString(c2Hint, _fSmall, bh, new RectangleF(ix, c2.Bottom - S(28), w - S(32), S(20)), sfNear);

            // ── CARD 3: SMART IDLE AUTO-SLEEP / SHUTDOWN ──
            int c3Y = c2.Bottom + gap;
            Rectangle c3 = new Rectangle(x, c3Y, w, c3H);
            UiTheme.FillRounded(g, c3, S(10), UiTheme.Surface);
            UiTheme.DrawRounded(g, c3, S(10), st.SmartIdleEnabled ? UiTheme.Violet : UiTheme.Border, 1f);
            UiTheme.FillRounded(g, new RectangleF(c3.X + S(1), c3.Y + S(12), S(3.5f), c3.Height - S(24)), S(2), UiTheme.Violet);

            string idleBtn = st.SmartIdleEnabled
                ? Loc.T(_lang, "Watcher: ON", "İzleyici: AÇIK", "监控中：开")
                : Loc.T(_lang, "Watcher: OFF", "İzleyici: KAPALI", "监控：关");
            int ibw = MeasurePillW(g, idleBtn, _fPill, 11);
            Rectangle ibRc = new Rectangle(c3.Right - ibw - S(14), c3.Y + (compact ? S(7) : S(10)), ibw, S(26));
            DrawPill(g, "idle_toggle", ibRc, idleBtn, st.SmartIdleEnabled ? UiTheme.Violet : UiTheme.Muted, st.SmartIdleEnabled, sfCenter);

            using (SolidBrush bt = new SolidBrush(UiTheme.Text))
                g.DrawString(Loc.T(_lang, "Smart Idle Auto-Power (After Downloads / Renders)",
                    "Akıllı Boşta Otomatik Güç (İndirme / Render Sonrası)",
                    "智能空闲自动关机/睡眠（下载或渲染完成后）"),
                    _fCardTitle, bt, new RectangleF(ix, c3.Y + (compact ? S(7) : S(10)), Math.Max(S(140), ibRc.Left - ix - S(10)), S(24)), sfNear);

            // Controls row: Action + Hold minutes
            int irY = c3.Y + (compact ? S(38) : S(46));
            int ix2 = ix;
            string[] iActs = new string[] { Loc.T(_lang, "Sleep", "Uyku", "睡眠"), Loc.T(_lang, "Shutdown", "Kapat", "关机") };
            for (int i = 0; i < 2; i++)
            {
                int aw = MeasurePillW(g, iActs[i], _fPill, 9);
                Rectangle ar = new Rectangle(ix2, irY, aw, S(26));
                DrawPill(g, "iact_" + i, ar, iActs[i], i == 1 ? UiTheme.Coral : UiTheme.Violet, _cfg.IdleAction == i, sfCenter);
                ix2 = ar.Right + S(6);
            }
            ix2 += S(12);
            using (SolidBrush bhl = new SolidBrush(UiTheme.Muted))
                g.DrawString(Loc.T(_lang, "Idle for:", "Boşta süre:", "持续空闲:"), _fBody, bhl, new RectangleF(ix2, irY, S(74), S(26)), sfNear);
            ix2 += S(76);
            int[] iMins = new int[] { 3, 5, 10 };
            for (int i = 0; i < iMins.Length; i++)
            {
                string ml = Loc.T(_lang, iMins[i] + "m", iMins[i] + " dk", iMins[i] + " 分钟");
                int mw = MeasurePillW(g, ml, _fPill, 8);
                Rectangle mr = new Rectangle(ix2, irY, mw, S(26));
                DrawPill(g, "imin_" + iMins[i], mr, ml, UiTheme.Violet, _cfg.IdleMinutes == iMins[i], sfCenter);
                ix2 = mr.Right + S(6);
            }

            // Live Telemetry Bar anchored safely inside c3
            int telH = compact ? S(32) : S(38);
            int telY = c3.Bottom - telH - S(10);
            Rectangle telBox = new Rectangle(ix, telY, w - S(32), telH);
            UiTheme.FillRounded(g, telBox, S(7), UiTheme.SurfaceAlt);
            UiTheme.DrawRounded(g, telBox, S(7), UiTheme.Border, 1f);

            double frac = st.SmartIdleTargetSec > 0 ? Math.Min(1.0, (double)st.SmartIdleElapsedSec / st.SmartIdleTargetSec) : 0.0;
            if (st.SmartIdleEnabled && frac > 0.01)
            {
                UiTheme.FillRounded(g, new RectangleF(telBox.X + 1, telBox.Y + 1, (float)((telBox.Width - 2) * frac), telBox.Height - 2),
                    S(6), Color.FromArgb(40, UiTheme.Violet));
            }

            string speedStr = st.LiveDownKBps >= 1024.0 ? Loc.F("{0:0.00} MB/s", st.LiveDownKBps / 1024.0) : Loc.F("{0:0.0} KB/s", st.LiveDownKBps);
            string telTxt = Loc.T(_lang,
                Loc.F("Trigger: Download < 1 Mbps & CPU < 8%   ·   Live: ↓ {0}  |  CPU {1:0.0}%  |  Idle {2} / {3}",
                    speedStr, st.LiveCpuPct, PowerGuardEngine.FormatDuration(st.SmartIdleElapsedSec), PowerGuardEngine.FormatDuration(st.SmartIdleTargetSec)),
                Loc.F("Koşul: İndirme < 1 Mbps & CPU < %8   ·   Anlık: ↓ {0}  |  CPU %{1:0.0}  |  Boşta {2} / {3}",
                    speedStr, st.LiveCpuPct, PowerGuardEngine.FormatDuration(st.SmartIdleElapsedSec), PowerGuardEngine.FormatDuration(st.SmartIdleTargetSec)),
                Loc.F("触发条件：下载 < 1 Mbps 且 CPU < 8%   ·   实时：↓ {0}  |  CPU {1:0.0}%  |  空闲 {2} / {3}",
                    speedStr, st.LiveCpuPct, PowerGuardEngine.FormatDuration(st.SmartIdleElapsedSec), PowerGuardEngine.FormatDuration(st.SmartIdleTargetSec)));
            using (SolidBrush btel = new SolidBrush(UiTheme.Muted))
                g.DrawString(telTxt, _fSmall, btel, new RectangleF(telBox.X + S(12), telBox.Y, telBox.Width - S(24), telBox.Height), sfNear);
        }

        private void DrawAwakeModeCard(Graphics g, string id, Rectangle rc, string title, string sub, bool selected, Color accent, StringFormat sfNear)
        {
            _hitRects[id] = rc;
            bool hov = _hoverId == id;
            UiTheme.FillRounded(g, rc, S(8), selected ? Color.FromArgb(26, accent) : (hov ? UiTheme.Hover : UiTheme.SurfaceAlt));
            UiTheme.DrawRounded(g, rc, S(8), selected ? accent : (hov ? UiTheme.BorderLight : UiTheme.Border), selected ? 1.4f : 1f);

            using (SolidBrush bt = new SolidBrush(selected ? UiTheme.Text : UiTheme.Muted))
            using (SolidBrush bs = new SolidBrush(UiTheme.Subtle))
            {
                g.DrawString(title, _fPill, bt, new RectangleF(rc.X + S(12), rc.Y + S(10), rc.Width - S(20), S(20)), sfNear);
                g.DrawString(sub, _fSmall, bs, new RectangleF(rc.X + S(12), rc.Y + S(32), rc.Width - S(20), S(24)), sfNear);
            }
        }

        private void PaintFooter(Graphics g, int x, int y, int w, int h, StringFormat sfNear, StringFormat sfCenter)
        {
            using (Pen pTop = new Pen(UiTheme.Border, 1f))
                g.DrawLine(pTop, 0, y, ClientSize.Width, y);

            int rx = x + w;
            int by = y + S(9);
            int bh = S(26);

            string refTxt = Loc.T(_lang, "Refresh", "Yenile", "刷新显示器");
            int refW = MeasurePillW(g, refTxt, _fPill, 9);
            Rectangle refRc = new Rectangle(rx - refW, by, refW, bh);
            DrawPill(g, "ft_refresh", refRc, refTxt, UiTheme.Cyan, false, sfCenter);
            rx = refRc.Left - S(6);

            string trayTxt = Loc.T(_lang, "Tray", "Tepsi", "托盘") + ": " +
                (_cfg.MinimizeToTray ? Loc.T(_lang, "ON", "AÇIK", "开") : Loc.T(_lang, "OFF", "KAPALI", "关"));
            int trayW = MeasurePillW(g, trayTxt, _fPill, 9);
            Rectangle trayRc = new Rectangle(rx - trayW, by, trayW, bh);
            DrawPill(g, "ft_tray", trayRc, trayTxt, _cfg.MinimizeToTray ? UiTheme.Emerald : UiTheme.Muted, _cfg.MinimizeToTray, sfCenter);
            rx = trayRc.Left - S(6);

            string startTxt = Loc.T(_lang, "Startup", "Başlangıç", "开机自启") + ": " +
                (_startupCached ? Loc.T(_lang, "ON", "AÇIK", "开") : Loc.T(_lang, "OFF", "KAPALI", "关"));
            int startW = MeasurePillW(g, startTxt, _fPill, 9);
            Rectangle startRc = new Rectangle(rx - startW, by, startW, bh);
            DrawPill(g, "ft_startup", startRc, startTxt, _startupCached ? UiTheme.Amber : UiTheme.Muted, _startupCached, sfCenter);
            rx = startRc.Left - S(10);

            bool hasToast = !string.IsNullOrEmpty(_toastText) && DateTime.UtcNow < _toastUntil;
            string leftTxt = hasToast ? _toastText
                : Loc.T(_lang,
                    Loc.F("{0} display(s) detected · Scroll wheel on sliders for ±5%", _display.Monitors.Count),
                    Loc.F("{0} monitör algılandı · Kaydırıcıda fare tekeri ±%5 değiştirir", _display.Monitors.Count),
                    Loc.F("已识别 {0} 台显示器 · 在滑块上滚动滚轮可 ±5% 微调", _display.Monitors.Count));
            using (SolidBrush bf = new SolidBrush(hasToast ? UiTheme.Emerald : UiTheme.Muted))
                g.DrawString(leftTxt, _fSmall, bf, new RectangleF(x, y, Math.Max(S(100), rx - x), h), sfNear);
        }

        private void DrawLangSeg(Graphics g, Rectangle rc, string label, string code, StringFormat sfCenter)
        {
            string id = "lang_" + code;
            _hitRects[id] = rc;
            bool active = string.Equals(_lang, code, StringComparison.OrdinalIgnoreCase);
            bool hov = _hoverId == id;
            if (active)
                UiTheme.FillRounded(g, new Rectangle(rc.X + 2, rc.Y + 2, rc.Width - 4, rc.Height - 4), S(5), UiTheme.Amber);
            else if (hov)
                UiTheme.FillRounded(g, new Rectangle(rc.X + 2, rc.Y + 2, rc.Width - 4, rc.Height - 4), S(5), UiTheme.Hover);
            using (SolidBrush b = new SolidBrush(active ? Color.FromArgb(15, 23, 42) : (hov ? UiTheme.Text : UiTheme.Muted)))
                g.DrawString(label, _fPill, b, rc, sfCenter);
        }

        private void DrawPill(Graphics g, string id, Rectangle rc, string text, Color accent, bool active, StringFormat sfCenter)
        {
            _hitRects[id] = rc;
            bool hov = _hoverId == id;
            Color fill = active ? Color.FromArgb(34, accent) : (hov ? UiTheme.Hover : UiTheme.SurfaceAlt);
            Color stroke = active ? accent : (hov ? UiTheme.BorderLight : UiTheme.Border);
            Color fg = active ? accent : (hov ? UiTheme.Text : UiTheme.Muted);
            UiTheme.FillRounded(g, rc, S(7), fill);
            UiTheme.DrawRounded(g, rc, S(7), stroke, 1f);
            using (SolidBrush bt = new SolidBrush(fg))
                g.DrawString(text, _fPill, bt, rc, sfCenter);
        }

        private static void DrawSunIcon(Graphics g, float cx, float cy, float rCore, float rIn, float rOut, Color col)
        {
            using (Pen p = new Pen(col, 1.6f))
            using (SolidBrush b = new SolidBrush(col))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4.0;
                    g.DrawLine(p, (float)(cx + Math.Cos(a) * rIn), (float)(cy + Math.Sin(a) * rIn),
                                  (float)(cx + Math.Cos(a) * rOut), (float)(cy + Math.Sin(a) * rOut));
                }
                g.FillEllipse(b, cx - rCore, cy - rCore, rCore * 2f, rCore * 2f);
            }
        }

        private SliderSlot FindSliderAt(Point pt)
        {
            for (int i = 0; i < _sliders.Count; i++)
                if (_sliders[i].HitRect.Contains(pt)) return _sliders[i];
            return null;
        }

        private string HitTestId(Point pt)
        {
            SliderSlot sl = FindSliderAt(pt);
            if (sl != null) return sl.Id;
            foreach (KeyValuePair<string, Rectangle> kv in _hitRects)
                if (kv.Value.Contains(pt)) return kv.Key;
            return "";
        }

        private void ApplySliderPoint(SliderSlot sl, int mouseX)
        {
            if (sl == null || sl.TrackRect.Width <= 0) return;
            double frac = (double)(mouseX - sl.TrackRect.X) / sl.TrackRect.Width;
            frac = Math.Max(0.0, Math.Min(1.0, frac));
            int val = sl.MinVal + (int)Math.Round(frac * (sl.MaxVal - sl.MinVal));
            ApplySliderValue(sl, val);
        }

        private void ApplySliderValue(SliderSlot sl, int val)
        {
            val = Math.Max(sl.MinVal, Math.Min(sl.MaxVal, val));
            if (sl.Kind == "br")
            {
                if (_cfg.LinkMonitors) _display.SetAllBrightness(val);
                else _display.SetBrightness(sl.MonitorIndex, val);
            }
            else if (sl.Kind == "ct")
            {
                _display.SetContrast(sl.MonitorIndex, val);
            }
            else if (sl.Kind == "tint")
            {
                _display.SetWarmTint(val);
                _cfg.WarmTint = val;
            }
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            SliderSlot sl = FindSliderAt(e.Location);
            if (sl != null)
            {
                int delta = e.Delta > 0 ? 5 : -5;
                int cur = 0;
                if (sl.Kind == "br" && sl.MonitorIndex >= 0 && sl.MonitorIndex < _display.Monitors.Count) cur = _display.Monitors[sl.MonitorIndex].Brightness;
                else if (sl.Kind == "ct" && sl.MonitorIndex >= 0 && sl.MonitorIndex < _display.Monitors.Count) cur = _display.Monitors[sl.MonitorIndex].Contrast;
                else if (sl.Kind == "tint") cur = _display.WarmTintPct;
                ApplySliderValue(sl, cur + delta);
                if (sl.Kind == "tint" && !_launch.DemoMode) _cfg.Save();
                return;
            }
            if (_tab == 0 && _maxScrollMonitors > 0)
            {
                _scrollMonitors += e.Delta > 0 ? -S(36) : S(36);
                if (_scrollMonitors < 0) _scrollMonitors = 0;
                if (_scrollMonitors > _maxScrollMonitors) _scrollMonitors = _maxScrollMonitors;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_activeSlider != null)
            {
                ApplySliderPoint(_activeSlider, e.X);
                return;
            }
            string h = HitTestId(e.Location);
            if (h != _hoverId)
            {
                _hoverId = h;
                Cursor = string.IsNullOrEmpty(h) ? Cursors.Default : Cursors.Hand;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_activeSlider == null && !string.IsNullOrEmpty(_hoverId))
            {
                _hoverId = "";
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            SliderSlot sl = FindSliderAt(e.Location);
            if (sl != null)
            {
                _activeSlider = sl;
                Capture = true;
                ApplySliderPoint(sl, e.X);
                return;
            }

            string id = HitTestId(e.Location);
            if (string.IsNullOrEmpty(id)) return;

            if (id == "lang_en") SwitchLanguage("en");
            else if (id == "lang_tr") SwitchLanguage("tr");
            else if (id == "lang_zh") SwitchLanguage("zh");
            else if (id == "tab_0") { _tab = 0; Invalidate(); }
            else if (id == "tab_1") { _tab = 1; Invalidate(); }
            else if (id == "warn_cancel")
            {
                _power.CancelWarningBanner();
                ShowToast(Loc.T(_lang, "Scheduled power action cancelled.", "Zamanlanmış güç işlemi iptal edildi.", "已取消定时电源操作。"));
            }
            else if (id.StartsWith("pre_"))
            {
                int pct;
                if (int.TryParse(id.Substring(4), out pct)) ApplyPresetAll(pct);
            }
            else if (id == "act_link")
            {
                _cfg.LinkMonitors = !_cfg.LinkMonitors;
                if (!_launch.DemoMode) _cfg.Save();
                Invalidate();
            }
            else if (id == "act_mon_off") TriggerMonitorsOff();
            else if (id == "tint_reset")
            {
                _display.ResetAllGammas();
                _cfg.WarmTint = 0;
                if (!_launch.DemoMode) _cfg.Save();
                ShowToast(Loc.T(_lang, "Original monitor gamma restored.", "Orijinal monitör gamması geri yüklendi.", "已恢复显示器原始色温。"));
            }
            else if (id.StartsWith("tint_"))
            {
                int tv;
                if (int.TryParse(id.Substring(5), out tv))
                {
                    _display.SetWarmTint(tv);
                    _cfg.WarmTint = tv;
                    if (!_launch.DemoMode) _cfg.Save();
                    Invalidate();
                }
            }
            else if (id.StartsWith("awake_"))
            {
                int am;
                if (int.TryParse(id.Substring(6), out am))
                {
                    _power.SetAwake((AwakeMode)am, _awakeDurMin);
                    _cfg.AwakeMode = am;
                    if (!_launch.DemoMode) _cfg.Save();
                    RebuildTrayMenu();
                    Invalidate();
                }
            }
            else if (id.StartsWith("adur_"))
            {
                int dm;
                if (int.TryParse(id.Substring(5), out dm))
                {
                    _awakeDurMin = dm;
                    if (_power.Status.Awake != AwakeMode.Off)
                        _power.SetAwake(_power.Status.Awake, _awakeDurMin);
                    Invalidate();
                }
            }
            else if (id.StartsWith("tact_"))
            {
                int ta;
                if (int.TryParse(id.Substring(5), out ta)) { _timerAction = (PowerTimerAction)ta; Invalidate(); }
            }
            else if (id.StartsWith("tmin_"))
            {
                int tm;
                if (int.TryParse(id.Substring(5), out tm)) { _timerSelectedMin = tm; Invalidate(); }
            }
            else if (id == "timer_toggle")
            {
                if (_power.Status.CountdownActive)
                {
                    _power.CancelCountdown();
                    ShowToast(Loc.T(_lang, "Countdown cancelled.", "Geri sayım iptal edildi.", "倒计时已取消。"));
                }
                else
                {
                    _power.StartCountdown(_timerAction, _timerSelectedMin);
                    ShowToast(Loc.T(_lang,
                        Loc.F("Countdown started: {0} min.", _timerSelectedMin),
                        Loc.F("Geri sayım başladı: {0} dk.", _timerSelectedMin),
                        Loc.F("已启动 {0} 分钟倒计时。", _timerSelectedMin)));
                }
            }
            else if (id == "idle_toggle")
            {
                bool next = !_power.Status.SmartIdleEnabled;
                _power.SetSmartIdle(next, (PowerTimerAction)_cfg.IdleAction, _cfg.IdleMinutes);
                Invalidate();
            }
            else if (id.StartsWith("iact_"))
            {
                int ia;
                if (int.TryParse(id.Substring(5), out ia))
                {
                    _cfg.IdleAction = ia;
                    if (!_launch.DemoMode) _cfg.Save();
                    _power.SetSmartIdle(_power.Status.SmartIdleEnabled, (PowerTimerAction)_cfg.IdleAction, _cfg.IdleMinutes);
                    Invalidate();
                }
            }
            else if (id.StartsWith("imin_"))
            {
                int im;
                if (int.TryParse(id.Substring(5), out im))
                {
                    _cfg.IdleMinutes = im;
                    if (!_launch.DemoMode) _cfg.Save();
                    _power.SetSmartIdle(_power.Status.SmartIdleEnabled, (PowerTimerAction)_cfg.IdleAction, _cfg.IdleMinutes);
                    Invalidate();
                }
            }
            else if (id == "ft_tray")
            {
                _cfg.MinimizeToTray = !_cfg.MinimizeToTray;
                if (!_launch.DemoMode) _cfg.Save();
                Invalidate();
            }
            else if (id == "ft_startup")
            {
                if (_launch.DemoMode) { _startupCached = !_startupCached; Invalidate(); return; }
                bool next = !_startupCached;
                if (WinNative.SetStartupEnabled(next))
                {
                    _startupCached = next;
                    ShowToast(next
                        ? Loc.T(_lang, "Added to Windows startup (minimized).", "Windows başlangıcına eklendi.", "已开启开机自启（托盘静默）。")
                        : Loc.T(_lang, "Removed from Windows startup.", "Windows başlangıcından kaldırıldı.", "已关闭开机自启。"));
                }
            }
            else if (id == "ft_refresh")
            {
                _display.RefreshMonitors();
                if (_cfg.WarmTint > 0) _display.SetWarmTint(_cfg.WarmTint);
                ShowToast(Loc.T(_lang,
                    Loc.F("Refreshed: {0} display(s).", _display.Monitors.Count),
                    Loc.F("Yenilendi: {0} monitör bulundu.", _display.Monitors.Count),
                    Loc.F("已刷新：检测到 {0} 台显示器。", _display.Monitors.Count)));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_activeSlider != null)
            {
                bool wasTint = _activeSlider.Kind == "tint";
                _activeSlider = null;
                Capture = false;
                if (wasTint && !_launch.DemoMode) _cfg.Save();
                Invalidate();
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            WinNative.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LaunchConfig launch = LaunchConfig.Parse(args);
            Mutex mutex = null;
            bool ownsMutex = false;

            if (!launch.DemoMode)
            {
                try
                {
                    mutex = new Mutex(true, @"Local\LumiBar_SingleInstance_v1", out ownsMutex);
                    if (!ownsMutex)
                    {
                        if (WinNative.WM_SHOW_EXISTING != 0)
                            WinNative.PostMessage(WinNative.HWND_BROADCAST, WinNative.WM_SHOW_EXISTING, IntPtr.Zero, IntPtr.Zero);
                        return;
                    }
                }
                catch { }
            }

            try
            {
                Application.Run(new MainForm(launch));
            }
            finally
            {
                if (mutex != null)
                {
                    if (ownsMutex) { try { mutex.ReleaseMutex(); } catch { } }
                    mutex.Dispose();
                }
            }
        }
    }
}
