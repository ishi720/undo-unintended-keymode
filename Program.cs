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
    /// <summary>アプリのエントリポイント。</summary>
    static class Program
    {
        /// <summary>多重起動を防いだうえで、タスクトレイに常駐するアプリを開始する。</summary>
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

    /// <summary>Caps Lock・IME・入力モード・入力方式の状態をまとめたもの。</summary>
    class KeyState
    {
        /// <summary>Caps Lock がオンかどうか。</summary>
        public bool Caps;
        /// <summary>IME の状態を取得できたかどうか。</summary>
        public bool ImeKnown;
        /// <summary>IME がオン（日本語入力）かどうか。</summary>
        public bool ImeOpen;
        /// <summary>IME の変換モード（IME_CMODE_* の組み合わせ）。</summary>
        public int ConversionMode;

        /// <summary>戻す先のデフォルト状態（Caps Lock オフ、IME オン、ひらがな、ローマ字入力）。</summary>
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

        /// <summary>入力モードの比較に使う変換モードのビット（NATIVE | KATAKANA | FULLSHAPE）。</summary>
        public const int ModeMask = 0x1 | 0x2 | 0x8;
        /// <summary>
        /// 入力方式（かな入力 / ローマ字入力）を取得できたかどうか。
        /// 新しい Microsoft IME では変換モードに反映されないため、レジストリ（<see cref="KanaInput"/>）から読む。
        /// </summary>
        public bool KanaKnown;
        /// <summary>かな入力かどうか（false ならローマ字入力）。</summary>
        public bool Kana;

        /// <summary>変換モードを表示用の名前（ひらがな、全角カタカナ など）にする。</summary>
        /// <param name="mode">IME の変換モード。</param>
        public static string ModeName(int mode)
        {
            const int NATIVE = 0x1, KATAKANA = 0x2, FULLSHAPE = 0x8;
            bool native = (mode & NATIVE) != 0, kata = (mode & KATAKANA) != 0, full = (mode & FULLSHAPE) != 0;
            if (native && !kata) return "ひらがな";
            if (native && kata) return full ? "全角カタカナ" : "半角カタカナ";
            return full ? "全角英数" : "半角英数";
        }

        /// <summary>Caps Lock の状態の表示用テキスト。</summary>
        public string CapsText { get { return Caps ? "オン（大文字）" : "オフ"; } }
        /// <summary>IME のオン/オフの表示用テキスト。</summary>
        public string ImeText { get { return !ImeKnown ? "取得できません" : (ImeOpen ? "オン" : "オフ"); } }
        /// <summary>入力方式の表示用テキスト。</summary>
        public string KanaText { get { return !KanaKnown ? "取得できません" : (Kana ? "かな入力" : "ローマ字入力"); } }
        /// <summary>入力モードの表示用テキスト。</summary>
        public string ModeText
        {
            get
            {
                if (!ImeKnown) return "－";
                return ImeOpen ? ModeName(ConversionMode) : "半角英数（IME オフ）";
            }
        }
    }

    /// <summary>settings.ini に保存するユーザー設定。</summary>
    class Settings
    {
        /// <summary>戻したときにバルーン通知を出すかどうか。</summary>
        public bool Notify = true;
        /// <summary>自動で戻す項目。</summary>
        public RestoreParts AutoParts = 0;

        /// <summary>設定ファイルを置くフォルダー（%APPDATA%\UndoUnintendedKeymode）。</summary>
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UndoUnintendedKeymode"); }
        }
        /// <summary>設定ファイルのパス。</summary>
        public static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        /// <summary>設定ファイルを読み込む。ファイルがない、または読めない場合は既定値を返す。</summary>
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

        /// <summary>辞書から値を取得する。キーがなければ <paramref name="def"/> を返す。</summary>
        static string Get(Dictionary<string, string> d, string key, string def)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : def;
        }
        /// <summary>辞書から "1" / "0" の値を bool で取得する。キーがなければ <paramref name="def"/> を返す。</summary>
        static bool GetBool(Dictionary<string, string> d, string key, bool def)
        {
            string v;
            return d.TryGetValue(key, out v) ? v == "1" : def;
        }
        /// <summary>bool を設定ファイル用の "1" / "0" にする。</summary>
        static string B(bool b) { return b ? "1" : "0"; }

        /// <summary>設定ファイルに保存する。</summary>
        /// <returns>保存できたら true。</returns>
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

    /// <summary>Win32 API の定数・構造体・関数の宣言。</summary>
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

        /// <summary>低レベルキーボードフックのコールバック。</summary>
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

    /// <summary>
    /// Microsoft IME のかな入力設定をレジストリで読み書きする。
    /// kanaMd: 1 = かな入力, 2 = ローマ字入力（書き換えると IME に即時反映される）。
    /// </summary>
    static class KanaInput
    {
        const string KeyPath = @"Software\AppDataLow\Software\Microsoft\IME\15.0\IMEJP\MSIME";

        /// <summary>かな入力の設定を読み取る。</summary>
        /// <returns>かな入力なら true、ローマ字入力なら false、取得できなければ null。</returns>
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

        /// <summary>かな入力の設定を書き込む。</summary>
        /// <param name="kana">true ならかな入力、false ならローマ字入力。</param>
        /// <returns>書き込めたら true。</returns>
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

    /// <summary>IME ウィンドウから読み取った IME の状態。</summary>
    class ImeState
    {
        /// <summary>状態を読み取った IME ウィンドウ。</summary>
        public IntPtr ImeWnd;
        /// <summary>IME がオンかどうか。</summary>
        public bool Open;
        /// <summary>IME の変換モード。</summary>
        public int ConversionMode;
    }

    /// <summary>WM_IME_CONTROL を使って、ほかのウィンドウの IME を読み書きする。</summary>
    static class Ime
    {
        /// <summary>ウィンドウ（フォーカスのある子ウィンドウを優先）の既定の IME ウィンドウを取得する。</summary>
        /// <param name="top">対象のトップレベルウィンドウ。</param>
        /// <returns>IME ウィンドウ。取得できなければ IntPtr.Zero。</returns>
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

        /// <summary>IME ウィンドウに WM_IME_CONTROL を送る。応答しないウィンドウで止まらないよう 50 ms でタイムアウトする。</summary>
        /// <param name="imeWnd">送り先の IME ウィンドウ。</param>
        /// <param name="cmd">IMC_* のコマンド。</param>
        /// <param name="value">コマンドに渡す値。</param>
        /// <param name="result">戻り値。</param>
        /// <returns>送信できたら true。</returns>
        static bool Send(IntPtr imeWnd, int cmd, int value, out int result)
        {
            IntPtr r;
            IntPtr ok = Native.SendMessageTimeout(imeWnd, Native.WM_IME_CONTROL, (IntPtr)cmd, (IntPtr)value,
                Native.SMTO_ABORTIFHUNG, 50, out r);
            result = (int)r.ToInt64();
            return ok != IntPtr.Zero;
        }

        /// <summary>IME のオン/オフと変換モードを読み取る。</summary>
        /// <param name="w">IME ウィンドウ。</param>
        /// <returns>読み取った状態。読み取れなければ null。</returns>
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

        /// <summary>IME のオン/オフを設定する。</summary>
        /// <param name="w">IME ウィンドウ。</param>
        /// <param name="open">true ならオン。</param>
        public static void SetOpen(IntPtr w, bool open)
        {
            int dummy;
            Send(w, Native.IMC_SETOPENSTATUS, open ? 1 : 0, out dummy);
        }

        /// <summary>IME の変換モードを設定する。</summary>
        /// <param name="w">IME ウィンドウ。</param>
        /// <param name="mode">設定する変換モード。</param>
        public static void SetConversionMode(IntPtr w, int mode)
        {
            int dummy;
            Send(w, Native.IMC_SETCONVERSIONMODE, mode, out dummy);
        }
    }

    /// <summary>デフォルトに戻す項目。</summary>
    [Flags]
    enum RestoreParts { Caps = 1, Ime = 2, Mode = 4, Kana = 8, ImeAll = Ime | Mode | Kana, All = Caps | Ime | Mode | Kana }

    /// <summary>フォームとコントロールを作る共通処理。</summary>
    static class UI
    {
        /// <summary>デフォルトと違う項目の背景色。</summary>
        public static readonly Color DiffColor = Color.FromArgb(255, 230, 200);

        /// <summary>フォームに共通の設定（フォント・枠・自動サイズなど）を適用する。</summary>
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

        /// <summary>フォーム全体に敷く縦並びのレイアウトパネルを作って追加する。</summary>
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

        /// <summary>見出しのラベルを作る。</summary>
        public static Label Section(string t)
        {
            return new Label { Text = t, AutoSize = true, Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(32, 90, 160), Margin = new Padding(0, 0, 0, 4) };
        }
        /// <summary>表のヘッダーセルを作る。</summary>
        public static Label Head(string t)
        {
            return new Label { Text = t, Dock = DockStyle.Fill, Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold),
                BackColor = Color.FromArgb(235, 242, 250), Padding = new Padding(6, 4, 6, 4), AutoSize = true };
        }
        /// <summary>表のセルを作る。</summary>
        public static Label Cell(string t)
        {
            return new Label { Text = t, Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 4), AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft };
        }
        /// <summary>ボタンを作る。</summary>
        public static Button Btn(string t)
        {
            return new Button { Text = t, AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        }

        /// <summary>フォームを画面右下（通知領域の近く）に表示して前面に出す。</summary>
        /// <param name="f">表示するフォーム。</param>
        /// <param name="offsetX">右端からさらに左へずらす量（ピクセル）。</param>
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

    /// <summary>キーの状態と設定を表示するメイン画面。</summary>
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
        /// <summary>画面に値を反映している最中かどうか（その間はチェックの変更を無視する）。</summary>
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

        /// <summary>指定した項目を手動でデフォルトに戻す「元に戻す」ボタンを作る。</summary>
        Button RowButton(RestoreParts part)
        {
            var b = new Button { Text = "元に戻す", AutoSize = true, Margin = new Padding(4, 2, 4, 2), Padding = new Padding(4, 0, 4, 0) };
            b.Click += delegate { app.RestoreDefault(true, part); };
            return b;
        }

        /// <summary>指定した項目の「自動で戻す」チェックボックスを作る。</summary>
        CheckBox AutoBox(RestoreParts part)
        {
            var c = new CheckBox { Text = "", AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(8, 4, 8, 4) };
            c.CheckedChanged += delegate { if (!syncing) app.SetAuto(part, c.Checked); };
            tip.SetToolTip(c, "チェックすると、ずれたときにデフォルトへ自動で戻します");
            return c;
        }

        /// <summary>設定のチェックボックスを作る。変更すると設定を保存する。</summary>
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

        /// <summary>画面を通知領域の近くに表示する。</summary>
        public void ShowNearTray() { UI.ShowNearTray(this, 0); }

        /// <summary>現在の設定を設定欄のチェックボックスに反映する。</summary>
        void LoadSettingsToView()
        {
            syncing = true;
            optNotify.Checked = app.CurrentSettings.Notify;
            optStartup.Checked = TrayApp.IsStartupEnabled();
            syncing = false;
        }

        /// <summary>キーの状態を読み直して表示を更新する。デフォルトと違う項目には色を付け、「元に戻す」ボタンを有効にする。</summary>
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

        /// <summary>エクスプローラーで設定ファイル（なければフォルダー）を開く。</summary>
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

        /// <summary>文字列が <paramref name="max"/> 文字を超える場合、末尾を「…」にして切り詰める。</summary>
        static string Shorten(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 1) + "…"; }
    }

    /// <summary>タスクトレイに常駐し、キーの状態を監視してデフォルトに戻すアプリ本体。</summary>
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
        /// <summary>Caps Lock を切り替えている最中かどうか（二重に切り替えないため）。</summary>
        bool capsBusy = false;
        /// <summary>この時刻までは IME を自動で戻す処理をしない。</summary>
        DateTime imeAutoCooldown = DateTime.MinValue;

        IntPtr targetWindow = IntPtr.Zero; // 直前にアクティブだった、このツールとタスクバー以外のウィンドウ
        MainForm mainForm;

        IntPtr currentIconHandle = IntPtr.Zero;
        DateTime lastBalloon = DateTime.MinValue;

        /// <summary>設定を読み込み、トレイアイコン・キーボードフック・監視タイマーを開始する。</summary>
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

        /// <summary>設定の変更をファイル・アイコン・画面に反映する。</summary>
        /// <param name="save">設定ファイルに保存するかどうか。</param>
        void Sync(bool save = true)
        {
            if (save) settings.Save();
            UpdateIcon();
            if (mainForm != null && mainForm.Visible) mainForm.RefreshView();
        }

        /// <summary>現在の設定。</summary>
        public Settings CurrentSettings { get { return settings; } }

        /// <summary>通知と自動起動の設定を変更して保存する。</summary>
        /// <param name="notify">戻したときに通知するかどうか。</param>
        /// <param name="startup">Windows の起動時に自動で起動するかどうか。</param>
        /// <returns>すべて保存できたら true。</returns>
        public bool ApplySettings(bool notify, bool startup)
        {
            settings.Notify = notify;
            bool ok = settings.Save();
            if (IsStartupEnabled() != startup) ok &= SetStartup(startup);
            Sync(false);
            CheckCaps();
            return ok;
        }

        /// <summary>メイン画面を表示する。</summary>
        public void ShowMain() { mainForm.ShowNearTray(); }

        /// <summary>前面のウィンドウがこのツールとタスクバー以外なら、操作対象のウィンドウとして記録する。</summary>
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

        /// <summary>前面のウィンドウがこのツールのものかどうか。</summary>
        bool IsOwnWindowForeground()
        {
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            return pid == myPid;
        }

        /// <summary>操作対象のウィンドウのタイトル。ウィンドウがなければ空文字。</summary>
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

        /// <summary>指定した項目が「自動で戻す」になっているかどうか。</summary>
        public bool IsAuto(RestoreParts p) { return (settings.AutoParts & p) != 0; }

        /// <summary>Caps Lock・操作対象のウィンドウの IME・入力方式の現在の状態を読み取る。</summary>
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

        /// <summary>指定した項目の「自動で戻す」を切り替える。オンにしたときはすぐにデフォルトへ戻す。</summary>
        /// <param name="p">対象の項目。</param>
        /// <param name="on">true なら自動で戻す。</param>
        public void SetAuto(RestoreParts p, bool on)
        {
            if (on) settings.AutoParts |= p; else settings.AutoParts &= ~p;
            Sync();
            if (on) { RestoreDefault(false, p & ~RestoreParts.Caps); CheckCaps(); }
        }

        /// <summary>指定した項目をデフォルトに戻す。</summary>
        /// <param name="manual">true のとき、対象のウィンドウを前面に戻してから適用し、結果を通知する。</param>
        /// <param name="parts">戻す項目。</param>
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

        /// <summary>指定した項目を <paramref name="sav"/> の状態にそろえる。</summary>
        /// <param name="sav">そろえる先の状態。</param>
        /// <param name="window">対象のウィンドウ。</param>
        /// <param name="parts">そろえる項目。</param>
        /// <returns>変更した内容の説明の一覧。</returns>
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

        /// <summary>「自動で戻す」の IME 関連の項目がずれていればデフォルトに戻す。このツールの画面が前面のときやクールダウン中は何もしない。</summary>
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

        /// <summary>低レベルキーボードフック。ユーザーが Caps Lock や 変換 / カタカナひらがな キーを離したら、少し待ってから状態を確認する。</summary>
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

        /// <summary>Caps Lock がオンかどうか。</summary>
        static bool IsCapsOn() { return (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0; }

        /// <summary>Caps Lock のあるべき状態。</summary>
        /// <returns>自動で戻す設定ならデフォルトの状態、そうでなければ null。</returns>
        bool? DesiredCaps()
        {
            if (IsAuto(RestoreParts.Caps)) return KeyState.Default.Caps;
            return null;
        }

        /// <summary>Caps Lock を自動で戻す設定で、状態がずれていれば戻す。</summary>
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

        /// <summary>キー入力を送って Caps Lock を切り替える。</summary>
        /// <param name="target">切り替え後の状態。</param>
        /// <param name="done">切り替え後に呼ぶ処理（null 可）。</param>
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

        /// <summary>Caps Lock キーを押した入力を送る。</summary>
        /// <param name="withShift">Shift と一緒に押すかどうか。</param>
        static void PressCaps(bool withShift)
        {
            if (withShift) Native.keybd_event(Native.VK_SHIFT, 0x2A, 0, OwnMarker);
            Native.keybd_event(Native.VK_CAPITAL, 0x3A, 0, OwnMarker);
            Native.keybd_event(Native.VK_CAPITAL, 0x3A, Native.KEYEVENTF_KEYUP, OwnMarker);
            if (withShift) Native.keybd_event(Native.VK_SHIFT, 0x2A, Native.KEYEVENTF_KEYUP, OwnMarker);
        }

        /// <summary>Windows の起動時に自動で起動する設定になっているかどうか。</summary>
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

        /// <summary>レジストリの Run キーに登録・削除して、自動起動を切り替える。</summary>
        /// <param name="enable">true なら登録する。</param>
        /// <returns>成功したら true。</returns>
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

        /// <summary>通知領域にバルーン通知を出す。</summary>
        /// <param name="title">タイトル。</param>
        /// <param name="body">本文。</param>
        /// <param name="force">true なら通知の設定や連続表示の制限を無視して表示する。</param>
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

        /// <summary>UI スレッドで <paramref name="ms"/> ミリ秒後に <paramref name="action"/> を一度だけ実行する。</summary>
        static void Delay(int ms, Action action)
        {
            var t = new System.Windows.Forms.Timer();
            t.Interval = Math.Max(1, ms);
            t.Tick += delegate { t.Stop(); t.Dispose(); action(); };
            t.Start();
        }

        /// <summary>トレイアイコンとツールチップを描き直す。いずれかの項目が「自動で戻す」なら緑、そうでなければ青。</summary>
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

        /// <summary>角丸四角形のパスを作る。</summary>
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

        /// <summary>フックとタイマーを止め、トレイアイコンとリソースを片付けて終了する。</summary>
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
