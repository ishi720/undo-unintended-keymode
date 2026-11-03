using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UndoUnintendedKeymode
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "UndoUnintendedKeymode_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Undo Unintended Keymode はすでに起動しています。\nタスクバー右下（通知領域）のアイコンを確認してください。",
                        "Undo Unintended Keymode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    class KeyState
    {
        public bool Caps;
        public bool ImeKnown;
        public bool ImeOpen;
        public int ConversionMode;

        public static readonly KeyState Default = MakeDefault();
        static KeyState MakeDefault()
        {
            var k = new KeyState();
            k.Caps = false;
            k.ImeKnown = true;
            k.ImeOpen = true;
            k.ConversionMode = 0x1 | 0x8; // IME_CMODE_NATIVE | IME_CMODE_FULLSHAPE（ひらがな）
            k.KanaKnown = true;
            k.Kana = false;
            return k;
        }

        // NATIVE | KATAKANA | FULLSHAPE
        public const int ModeMask = 0x1 | 0x2 | 0x8;
        // 新しい Microsoft IME では変換モードに反映されないため、レジストリ (KanaInput) から読む
        public bool KanaKnown;
        public bool Kana;

        public static string ModeName(int mode)
        {
            const int NATIVE = 0x1, KATAKANA = 0x2, FULLSHAPE = 0x8;
            bool native = (mode & NATIVE) != 0, kata = (mode & KATAKANA) != 0, full = (mode & FULLSHAPE) != 0;
            if (native && !kata) return "ひらがな";
            if (native && kata) return full ? "全角カタカナ" : "半角カタカナ";
            return full ? "全角英数" : "半角英数";
        }

        public string CapsText { get { return Caps ? "オン（大文字）" : "オフ"; } }
        public string ImeText { get { return !ImeKnown ? "取得できません" : (ImeOpen ? "オン" : "オフ"); } }
        public string KanaText { get { return !KanaKnown ? "取得できません" : (Kana ? "かな入力" : "ローマ字入力"); } }
        public string ModeText
        {
            get
            {
                if (!ImeKnown) return "－";
                return ImeOpen ? ModeName(ConversionMode) : "半角英数（IME オフ）";
            }
        }
    }

    class Settings
    {
        public bool Notify = true;
        public RestoreParts AutoParts = 0;

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UndoUnintendedKeymode"); }
        }
        public static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = new Dictionary<string, string>();
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                s.Notify = GetBool(d, "Notify", s.Notify);
                int ap;
                if (int.TryParse(Get(d, "AutoParts", ""), out ap)) s.AutoParts = (RestoreParts)(ap & (int)RestoreParts.All);

            }
            catch { }
            return s;
        }

        static string Get(Dictionary<string, string> d, string key, string def)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : def;
        }
        static bool GetBool(Dictionary<string, string> d, string key, bool def)
        {
            string v;
            return d.TryGetValue(key, out v) ? v == "1" : def;
        }
        static string B(bool b) { return b ? "1" : "0"; }

        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string> {
                    "Notify=" + B(Notify),
                    "AutoParts=" + (int)AutoParts,
                };
                File.WriteAllLines(FilePath, lines.ToArray(), Encoding.UTF8);
                return true;
            }
            catch { return false; }
        }
    }

    static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public const uint LLKHF_INJECTED = 0x10;

        public const byte VK_SHIFT = 0x10;
        public const byte VK_CAPITAL = 0x14;
        public const byte VK_CONVERT = 0x1C;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        public const uint WM_IME_CONTROL = 0x0283;
        public const int IMC_GETCONVERSIONMODE = 0x0001;
        public const int IMC_SETCONVERSIONMODE = 0x0002;
        public const int IMC_GETOPENSTATUS = 0x0005;
        public const int IMC_SETOPENSTATUS = 0x0006;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("imm32.dll")]
        public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    // Microsoft IME のかな入力設定。kanaMd: 1 = かな入力, 2 = ローマ字入力（書き換えると IME に即時反映される）
    static class KanaInput
    {
        const string KeyPath = @"Software\AppDataLow\Software\Microsoft\IME\15.0\IMEJP\MSIME";

        public static bool? IsKana()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (k == null) return null;
                    object v = k.GetValue("kanaMd");
                    if (!(v is int)) return null;
                    int n = (int)v;
                    if (n == 1) return true;
                    if (n == 2) return false;
                    return null;
                }
            }
            catch { return null; }
        }

        public static bool Set(bool kana)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (k == null) return false;
                    k.SetValue("kanaMd", kana ? 1 : 2, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch { return false; }
        }
    }

    class ImeState
    {
        public IntPtr ImeWnd;
        public bool Open;
        public int ConversionMode;
    }

    static class Ime
    {
        public static IntPtr GetImeWndFor(IntPtr top)
        {
            if (top == IntPtr.Zero || !Native.IsWindow(top)) return IntPtr.Zero;
            uint pid;
            uint tid = Native.GetWindowThreadProcessId(top, out pid);
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            IntPtr target = top;
            if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero) target = gti.hwndFocus;
            return Native.ImmGetDefaultIMEWnd(target);
        }

        static bool Send(IntPtr imeWnd, int cmd, int value, out int result)
        {
            IntPtr r;
            IntPtr ok = Native.SendMessageTimeout(imeWnd, Native.WM_IME_CONTROL, (IntPtr)cmd, (IntPtr)value,
                Native.SMTO_ABORTIFHUNG, 50, out r);
            result = (int)r.ToInt64();
            return ok != IntPtr.Zero;
        }

        public static ImeState ReadFrom(IntPtr w)
        {
            if (w == IntPtr.Zero || !Native.IsWindow(w)) return null;
            int open, conv;
            if (!Send(w, Native.IMC_GETOPENSTATUS, 0, out open)) return null;
            if (!Send(w, Native.IMC_GETCONVERSIONMODE, 0, out conv)) return null;
            var s = new ImeState();
            s.ImeWnd = w;
            s.Open = open != 0;
            s.ConversionMode = conv;
            return s;
        }

        public static void SetOpen(IntPtr w, bool open)
        {
            int dummy;
            Send(w, Native.IMC_SETOPENSTATUS, open ? 1 : 0, out dummy);
        }

        public static void SetConversionMode(IntPtr w, int mode)
        {
            int dummy;
            Send(w, Native.IMC_SETCONVERSIONMODE, mode, out dummy);
        }
    }

    [Flags]
    enum RestoreParts { Caps = 1, Ime = 2, Mode = 4, Kana = 8, ImeAll = Ime | Mode | Kana, All = Caps | Ime | Mode | Kana }

    static class UI
    {
        public static readonly Color DiffColor = Color.FromArgb(255, 230, 200);

        public static void SetupForm(Form f, string title)
        {
            f.Text = title;
            f.Font = new Font("Yu Gothic UI", 10f);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = true;
            f.ShowInTaskbar = true;
            f.StartPosition = FormStartPosition.Manual;
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.TopMost = true;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        public static TableLayoutPanel Root(Form f)
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.ColumnCount = 1;
            root.AutoSize = true;
            f.Controls.Add(root);
            return root;
        }

        public static Label Section(string t)
        {
            return new Label { Text = t, AutoSize = true, Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(32, 90, 160), Margin = new Padding(0, 0, 0, 4) };
        }
        public static Label Head(string t)
        {
            return new Label { Text = t, Dock = DockStyle.Fill, Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold),
                BackColor = Color.FromArgb(235, 242, 250), Padding = new Padding(6, 4, 6, 4), AutoSize = true };
        }
        public static Label Cell(string t)
        {
            return new Label { Text = t, Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 4), AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft };
        }
        public static Button Btn(string t)
        {
            return new Button { Text = t, AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        }

        public static void ShowNearTray(Form f, int offsetX)
        {
            if (!f.Visible)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                f.Show();
                f.Location = new Point(Math.Max(wa.Left, wa.Right - f.Width - 12 - offsetX), Math.Max(wa.Top, wa.Bottom - f.Height - 12));
            }
            if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
            f.Activate();
        }
    }

    class MainForm : Form
    {
        readonly TrayApp app;
        readonly Label lblTarget;
        readonly Label curCaps, curIme, curMode, curKana;
        readonly Button rowCaps, rowIme, rowMode, rowKana;
        readonly CheckBox autoCaps, autoIme, autoMode, autoKana;
        readonly CheckBox optNotify, optStartup;
        readonly ToolTip tip = new ToolTip();
        readonly System.Windows.Forms.Timer refresh;
        bool syncing = false;

        public MainForm(TrayApp app)
        {
            this.app = app;
            UI.SetupForm(this, "Undo Unintended Keymode");
            var root = UI.Root(this);

            root.Controls.Add(UI.Section("キーの状態"));

            lblTarget = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 8) };
            root.Controls.Add(lblTarget);

            var d = KeyState.Default;
            var grid = new TableLayoutPanel();
            grid.ColumnCount = 5;
            grid.RowCount = 5;
            grid.AutoSize = true;
            grid.CellBorderStyle = TableLayoutPanelCellBorderStyle.Single;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.Controls.Add(UI.Head("項目"), 0, 0);
            grid.Controls.Add(UI.Head("現在の状態"), 1, 0);
            grid.Controls.Add(UI.Head("デフォルト"), 2, 0);
            grid.Controls.Add(UI.Head("手動"), 3, 0);
            grid.Controls.Add(UI.Head("自動で戻す"), 4, 0);
            grid.Controls.Add(UI.Cell("Caps Lock"), 0, 1);
            grid.Controls.Add(UI.Cell("日本語入力 (IME)"), 0, 2);
            grid.Controls.Add(UI.Cell("入力モード"), 0, 3);
            grid.Controls.Add(UI.Cell("入力方式"), 0, 4);
            curCaps = UI.Cell(""); curIme = UI.Cell(""); curMode = UI.Cell(""); curKana = UI.Cell("");
            rowCaps = RowButton(RestoreParts.Caps);
            rowIme = RowButton(RestoreParts.Ime);
            rowMode = RowButton(RestoreParts.Mode);
            rowKana = RowButton(RestoreParts.Kana);
            autoCaps = AutoBox(RestoreParts.Caps);
            autoIme = AutoBox(RestoreParts.Ime);
            autoMode = AutoBox(RestoreParts.Mode);
            autoKana = AutoBox(RestoreParts.Kana);
            grid.Controls.Add(curCaps, 1, 1); grid.Controls.Add(UI.Cell(d.CapsText), 2, 1); grid.Controls.Add(rowCaps, 3, 1); grid.Controls.Add(autoCaps, 4, 1);
            grid.Controls.Add(curIme, 1, 2);  grid.Controls.Add(UI.Cell(d.ImeText), 2, 2);  grid.Controls.Add(rowIme, 3, 2);  grid.Controls.Add(autoIme, 4, 2);
            grid.Controls.Add(curMode, 1, 3); grid.Controls.Add(UI.Cell(d.ModeText), 2, 3); grid.Controls.Add(rowMode, 3, 3); grid.Controls.Add(autoMode, 4, 3);
            grid.Controls.Add(curKana, 1, 4); grid.Controls.Add(UI.Cell(d.KanaText), 2, 4); grid.Controls.Add(rowKana, 3, 4); grid.Controls.Add(autoKana, 4, 4);
            root.Controls.Add(grid);

            var sec = UI.Section("設定");
            sec.Margin = new Padding(0, 16, 0, 4);
            root.Controls.Add(sec);

            var opts = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
            optNotify = Opt("戻したときに通知する");
            optStartup = Opt("Windows の起動時に自動で起動");
            opts.Controls.Add(optNotify);
            opts.Controls.Add(optStartup);
            root.Controls.Add(opts);

            var lnkFolder = new LinkLabel { Text = "設定ファイルの場所を開く", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            lnkFolder.LinkClicked += delegate { OpenSettingsFolder(); };
            root.Controls.Add(lnkFolder);

            refresh = new System.Windows.Forms.Timer();
            refresh.Interval = 300;
            refresh.Tick += delegate { RefreshView(); };

            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
            VisibleChanged += delegate
            {
                if (Visible) { LoadSettingsToView(); refresh.Start(); RefreshView(); }
                else refresh.Stop();
            };
        }

        Button RowButton(RestoreParts part)
        {
            var b = new Button { Text = "元に戻す", AutoSize = true, Margin = new Padding(4, 2, 4, 2), Padding = new Padding(4, 0, 4, 0) };
            b.Click += delegate { app.RestoreDefault(true, part); };
            return b;
        }

        CheckBox AutoBox(RestoreParts part)
        {
            var c = new CheckBox { Text = "", AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(8, 4, 8, 4) };
            c.CheckedChanged += delegate { if (!syncing) app.SetAuto(part, c.Checked); };
            tip.SetToolTip(c, "チェックすると、ずれたときにデフォルトへ自動で戻します");
            return c;
        }

        CheckBox Opt(string t)
        {
            var c = new CheckBox { Text = t, AutoSize = true };
            c.CheckedChanged += delegate
            {
                if (syncing) return;
                if (!app.ApplySettings(optNotify.Checked, optStartup.Checked))
                    MessageBox.Show(this, "設定を保存できませんでした。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                LoadSettingsToView();
            };
            return c;
        }

        public void ShowNearTray() { UI.ShowNearTray(this, 0); }

        void LoadSettingsToView()
        {
            syncing = true;
            optNotify.Checked = app.CurrentSettings.Notify;
            optStartup.Checked = TrayApp.IsStartupEnabled();
            syncing = false;
        }

        public void RefreshView()
        {
            var cur = app.ReadCurrentState();
            var d = KeyState.Default;
            string title = app.TargetWindowTitle;
            lblTarget.Text = "対象のウィンドウ： " + (string.IsNullOrEmpty(title) ? "（なし）" : Shorten(title, 48));

            curCaps.Text = cur.CapsText;
            curIme.Text = cur.ImeText;
            curMode.Text = cur.ModeText;
            curKana.Text = cur.KanaText;

            bool capsDiff = cur.Caps != d.Caps;
            bool imeDiff = cur.ImeKnown && cur.ImeOpen != d.ImeOpen;
            bool modeDiff = cur.ImeKnown && cur.ImeOpen &&
                (cur.ConversionMode & KeyState.ModeMask) != (d.ConversionMode & KeyState.ModeMask);
            bool kanaDiff = cur.KanaKnown && cur.Kana != d.Kana;
            bool modeBlocked = cur.ImeKnown && !cur.ImeOpen;

            curCaps.BackColor = capsDiff ? UI.DiffColor : SystemColors.Control;
            curIme.BackColor = imeDiff ? UI.DiffColor : SystemColors.Control;
            curMode.BackColor = (modeDiff || imeDiff) ? UI.DiffColor : SystemColors.Control;
            curKana.BackColor = kanaDiff ? UI.DiffColor : SystemColors.Control;

            rowCaps.Enabled = capsDiff;
            rowIme.Enabled = imeDiff;
            rowMode.Enabled = modeDiff;
            rowKana.Enabled = kanaDiff;
            tip.SetToolTip(rowCaps, capsDiff ? "Caps Lock をデフォルトに戻します" : "デフォルトと同じです");
            tip.SetToolTip(rowIme, imeDiff ? "日本語入力のオン/オフをデフォルトに戻します" : "デフォルトと同じです");
            tip.SetToolTip(rowKana, kanaDiff ? "入力方式をデフォルト（ローマ字入力）に戻します" : "デフォルトと同じです");
            tip.SetToolTip(rowMode, modeDiff ? "入力モードをデフォルトに戻します"
                : (modeBlocked ? "IME がオフのため戻せません。先に IME を戻してください" : "デフォルトと同じです"));

            syncing = true;
            autoCaps.Checked = app.IsAuto(RestoreParts.Caps);
            autoIme.Checked = app.IsAuto(RestoreParts.Ime);
            autoMode.Checked = app.IsAuto(RestoreParts.Mode);
            autoKana.Checked = app.IsAuto(RestoreParts.Kana);
            syncing = false;
        }

        static void OpenSettingsFolder()
        {
            try
            {
                if (File.Exists(Settings.FilePath))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Settings.FilePath + "\"");
                else
                {
                    Directory.CreateDirectory(Settings.Dir);
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + Settings.Dir + "\"");
                }
            }
            catch { }
        }

        static string Shorten(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 1) + "…"; }
    }

    class TrayApp : ApplicationContext
    {
        const string AppName = "Undo Unintended Keymode";
        const string RunName = "UndoUnintendedKeymode";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly UIntPtr OwnMarker = (UIntPtr)0x554B4B52; // 自分で送ったキー入力の目印

        readonly Settings settings;
        readonly NotifyIcon tray;
        readonly Native.LowLevelKeyboardProc hookProc; // GC で回収されないよう保持
        IntPtr hookHandle = IntPtr.Zero;
        readonly SynchronizationContext ui;
        readonly uint myPid;

        readonly System.Windows.Forms.Timer watchTimer;
        bool capsBusy = false;
        DateTime imeAutoCooldown = DateTime.MinValue;

        IntPtr targetWindow = IntPtr.Zero; // 直前にアクティブだった、このツールとタスクバー以外のウィンドウ
        MainForm mainForm;

        IntPtr currentIconHandle = IntPtr.Zero;
        DateTime lastBalloon = DateTime.MinValue;

        public TrayApp()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            settings = Settings.Load();
            using (var p = System.Diagnostics.Process.GetCurrentProcess()) myPid = (uint)p.Id;

            tray = new NotifyIcon();
            tray.Text = AppName;
            var menu = new ContextMenuStrip();
            menu.Items.Add("設定を開く", null, delegate { ShowMain(); });
            menu.Items.Add("終了", null, delegate { ExitThread(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
            tray.Visible = true;

            mainForm = new MainForm(this);
            Sync();

            hookProc = HookCallback;
            using (var p = System.Diagnostics.Process.GetCurrentProcess())
            using (var m = p.MainModule)
            {
                hookHandle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(m.ModuleName), 0);
            }
            if (hookHandle == IntPtr.Zero)
            {
                MessageBox.Show("キーボードの監視を開始できませんでした。\n自動で戻す処理は、少し遅れて（0.4 秒ごとの確認で）動作します。",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            watchTimer = new System.Windows.Forms.Timer();
            watchTimer.Interval = 400;
            watchTimer.Tick += delegate { TrackTargetWindow(); CheckCaps(); AutoRestoreIme(); };
            watchTimer.Start();
            TrackTargetWindow();

            Balloon("起動しました", "アイコンをクリックすると画面を開きます。", true);
        }

        void Sync(bool save = true)
        {
            if (save) settings.Save();
            UpdateIcon();
            if (mainForm != null && mainForm.Visible) mainForm.RefreshView();
        }

        public Settings CurrentSettings { get { return settings; } }

        public bool ApplySettings(bool notify, bool startup)
        {
            settings.Notify = notify;
            bool ok = settings.Save();
            if (IsStartupEnabled() != startup) ok &= SetStartup(startup);
            Sync(false);
            CheckCaps();
            return ok;
        }

        public void ShowMain() { mainForm.ShowNearTray(); }

        void TrackTargetWindow()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            if (pid == myPid) return;
            var cls = new StringBuilder(256);
            Native.GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow" ||
                c == "TopLevelWindowForOverflowXamlIsland" || c == "XamlExplorerHostIslandWindow") return;
            targetWindow = fg;
        }

        bool IsOwnWindowForeground()
        {
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            return pid == myPid;
        }

        public string TargetWindowTitle
        {
            get
            {
                if (targetWindow == IntPtr.Zero || !Native.IsWindow(targetWindow)) return "";
                var sb = new StringBuilder(256);
                Native.GetWindowText(targetWindow, sb, sb.Capacity);
                return sb.ToString();
            }
        }

        public bool IsAuto(RestoreParts p) { return (settings.AutoParts & p) != 0; }

        public KeyState ReadCurrentState()
        {
            var k = new KeyState();
            k.Caps = IsCapsOn();
            var ime = Ime.ReadFrom(Ime.GetImeWndFor(targetWindow));
            if (ime != null)
            {
                k.ImeKnown = true;
                k.ImeOpen = ime.Open;
                k.ConversionMode = ime.ConversionMode;
            }
            bool? kana = KanaInput.IsKana();
            k.KanaKnown = kana.HasValue;
            k.Kana = kana.HasValue && kana.Value;
            return k;
        }

        public void SetAuto(RestoreParts p, bool on)
        {
            if (on) settings.AutoParts |= p; else settings.AutoParts &= ~p;
            Sync();
            if (on) { RestoreDefault(false, p & ~RestoreParts.Caps); CheckCaps(); }
        }

        // manual: 対象ウィンドウを前面に戻してから適用し、結果を通知する
        public void RestoreDefault(bool manual, RestoreParts parts = RestoreParts.All)
        {
            var sav = KeyState.Default;

            if (manual)
            {
                bool fromOwnWindow = IsOwnWindowForeground();
                bool needWindow = (parts & (RestoreParts.Ime | RestoreParts.Mode)) != 0;
                if (needWindow && fromOwnWindow && targetWindow != IntPtr.Zero && Native.IsWindow(targetWindow))
                    Native.SetForegroundWindow(targetWindow);

                Delay(needWindow && fromOwnWindow ? 150 : 10, delegate
                {
                    var changes = ApplyState(sav, targetWindow, parts);
                    Delay(250, delegate
                    {
                        string result = changes.Count == 0 ? "すでにデフォルトと同じです"
                            : "デフォルトに戻しました：" + string.Join("、", changes.ToArray());
                        if (mainForm.Visible) mainForm.RefreshView();
                        Balloon("デフォルトに戻す", result, true);
                    });
                });
            }
            else
            {
                var changes = ApplyState(sav, targetWindow, parts);
                if (changes.Count > 0)
                    Balloon("デフォルトに自動で戻しました", string.Join("、", changes.ToArray()), false);
            }
        }

        List<string> ApplyState(KeyState sav, IntPtr window, RestoreParts parts)
        {
            var changes = new List<string>();
            if ((parts & RestoreParts.Caps) != 0 && IsCapsOn() != sav.Caps)
            {
                SetCaps(sav.Caps, null);
                changes.Add("Caps Lock を" + (sav.Caps ? "オン" : "オフ"));
            }
            if (sav.ImeKnown && (parts & RestoreParts.ImeAll) != 0)
            {
                IntPtr w = Ime.GetImeWndFor(window);
                var now = Ime.ReadFrom(w);
                if (now != null)
                {
                    bool open = now.Open;
                    if ((parts & RestoreParts.Ime) != 0 && now.Open != sav.ImeOpen)
                    {
                        Ime.SetOpen(w, sav.ImeOpen);
                        open = sav.ImeOpen;
                        changes.Add("IME を" + (sav.ImeOpen ? "オン" : "オフ"));
                    }
                    int mode = now.ConversionMode;
                    if ((parts & RestoreParts.Mode) != 0 && open && sav.ImeOpen &&
                        (mode & KeyState.ModeMask) != (sav.ConversionMode & KeyState.ModeMask))
                    {
                        mode = (mode & ~KeyState.ModeMask) | (sav.ConversionMode & KeyState.ModeMask);
                        changes.Add("入力モードを" + KeyState.ModeName(sav.ConversionMode));
                    }
                    if (mode != now.ConversionMode) Ime.SetConversionMode(w, mode);
                }
            }

            if ((parts & RestoreParts.Kana) != 0)
            {
                bool? kana = KanaInput.IsKana();
                if (kana.HasValue && kana.Value != sav.Kana)
                {
                    if (KanaInput.Set(sav.Kana))
                    {
                        changes.Add(sav.Kana ? "かな入力に" : "ローマ字入力に");
                        DateTime c = DateTime.Now.AddMilliseconds(1000);
                        if (c > imeAutoCooldown) imeAutoCooldown = c;
                    }
                    else
                        Balloon("入力方式を戻せませんでした", "Alt を押しながら「カタカナ/ひらがな」キーを押してください。", false);
                }
            }
            return changes;
        }

        void AutoRestoreIme()
        {
            var parts = settings.AutoParts & RestoreParts.ImeAll;
            if (parts == 0) return;
            if (IsOwnWindowForeground()) return;
            if (DateTime.Now < imeAutoCooldown) return;

            var changes = ApplyState(KeyState.Default, targetWindow, parts);
            if (changes.Count > 0)
            {
                DateTime c = DateTime.Now.AddMilliseconds(800);
                if (c > imeAutoCooldown) imeAutoCooldown = c;
                Balloon("デフォルトに自動で戻しました", string.Join("、", changes.ToArray()), false);
            }
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
                int msg = wParam.ToInt32();
                bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;

                if (!injected && up && k.vkCode == Native.VK_CAPITAL)
                    ui.Post(delegate { Delay(60, CheckCaps); }, null);
                else if (!injected && up && (k.vkCode == Native.VK_CONVERT || k.vkCode == 0xF1 || k.vkCode == 0xF2)) // 変換 / カタカナひらがな
                    ui.Post(delegate { Delay(120, AutoRestoreIme); }, null);
            }
            return Native.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        static bool IsCapsOn() { return (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0; }

        bool? DesiredCaps()
        {
            if (IsAuto(RestoreParts.Caps)) return KeyState.Default.Caps;
            return null;
        }

        void CheckCaps()
        {
            if (capsBusy) return;
            bool? want = DesiredCaps();
            if (!want.HasValue || IsCapsOn() == want.Value) return;
            bool target = want.Value;
            SetCaps(target, delegate
            {
                if (IsCapsOn() == target)
                {
                    Balloon("デフォルトに自動で戻しました", "Caps Lock を" + (target ? "オン" : "オフ") + "にしました。", false);
                }
            });
        }

        void SetCaps(bool target, Action done)
        {
            if (IsCapsOn() == target) { if (done != null) done(); return; }
            capsBusy = true;
            PressCaps(false);
            Delay(80, delegate
            {
                if (IsCapsOn() != target)
                {
                    // 日本語キーボードでは Shift + Caps Lock でないと切り替わらないことがある
                    bool userHoldsShift = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
                    if (!userHoldsShift) PressCaps(true);
                }
                Delay(80, delegate
                {
                    capsBusy = false;
                    if (done != null) done();
                });
            });
        }

        static void PressCaps(bool withShift)
        {
            if (withShift) Native.keybd_event(Native.VK_SHIFT, 0x2A, 0, OwnMarker);
            Native.keybd_event(Native.VK_CAPITAL, 0x3A, 0, OwnMarker);
            Native.keybd_event(Native.VK_CAPITAL, 0x3A, Native.KEYEVENTF_KEYUP, OwnMarker);
            if (withShift) Native.keybd_event(Native.VK_SHIFT, 0x2A, Native.KEYEVENTF_KEYUP, OwnMarker);
        }

        public static bool IsStartupEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return k != null && k.GetValue(RunName) != null;
                }
            }
            catch { return false; }
        }

        static bool SetStartup(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    if (enable) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("自動起動の設定に失敗しました。\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        void Balloon(string title, string body, bool force)
        {
            if (!force && !settings.Notify) return;
            if (!force && (DateTime.Now - lastBalloon).TotalSeconds < 3) return;
            lastBalloon = DateTime.Now;
            tray.BalloonTipTitle = title;
            tray.BalloonTipText = string.IsNullOrEmpty(body) ? " " : body;
            tray.BalloonTipIcon = ToolTipIcon.Info;
            tray.ShowBalloonTip(2500);
        }

        static void Delay(int ms, Action action)
        {
            var t = new System.Windows.Forms.Timer();
            t.Interval = Math.Max(1, ms);
            t.Tick += delegate { t.Stop(); t.Dispose(); action(); };
            t.Start();
        }

        void UpdateIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                Color bg = IsAuto(RestoreParts.All) ? Color.FromArgb(30, 150, 90) : Color.FromArgb(32, 120, 200);
                using (var path = RoundRect(new Rectangle(1, 1, 30, 30), 8))
                using (var b = new SolidBrush(bg))
                    g.FillPath(b, path);
                using (var f = new Font("Yu Gothic UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("戻", f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
            }
            IntPtr h = bmp.GetHicon();
            tray.Icon = Icon.FromHandle(h);
            if (currentIconHandle != IntPtr.Zero) Native.DestroyIcon(currentIconHandle);
            currentIconHandle = h;
            bmp.Dispose();

            string tip = AppName;
            if (IsAuto(RestoreParts.All)) tip += "（自動で戻す：オン）";
            tray.Text = tip;
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void ExitThreadCore()
        {
            if (hookHandle != IntPtr.Zero) { Native.UnhookWindowsHookEx(hookHandle); hookHandle = IntPtr.Zero; }
            watchTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (mainForm != null) mainForm.Dispose();
            if (currentIconHandle != IntPtr.Zero) Native.DestroyIcon(currentIconHandle);
            base.ExitThreadCore();
        }
    }
}
