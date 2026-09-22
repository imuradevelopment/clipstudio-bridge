// pipelinetest.cs — 仮想液タブの通し検証（起動から終了まで、指定タイミングのキャプチャ保証）
//
// キャプチャのタイミング(指定どおり。全て全画面=メイン+仮想モニタの両方が写る):
//   仮想液タブ起動 → キャプチャ
//   電卓起動と配置 → キャプチャ
//   操作の前後     → キャプチャ(各タップごと)
//   最終的な表示   → キャプチャ
//   電卓閉じる     → キャプチャ
//   仮想液タブ終了 → キャプチャ
//  に加えて、起動完了〜終了の間は 0.5秒間隔の常時キャプチャ(roll/)を
// バックグラウンドで取り続ける。前後2枚が同じでも間に起きた出来事を
// 見逃さないため。名前付きキャプチャの時点でロール何枚目かをログに残す。
//
// 経路: vmulti(col05制御) → vmulti.sys → OTD(読み取り) → SendInput絶対座標 → 仮想モニタ
//  既知の問題(2026-09-23 run-032014で観測): OTDの注入は仮想マウス(MOUSEEVENTF)なので、
//  カーソルがメイン上に居る時にクリック系イベントが発火するとメインに右クリックメニュー等が出る。
//  → OTDを使わない版への載せ替えは今後の課題(OSペンスタック経路)。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;

static class PipelineTest {
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int h2, uint f);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const byte TIP = 0x01, INRANGE = 0x02;

    static Rectangle _vd, _all;
    static string _capDir;
    static byte _targetReportId;
    static HidStream _vmulti;
    static volatile bool _rolling;
    static int _rollCount;
    static Thread _rollThread;
    static readonly object _shotLock = new object();

    static int Main(string[] args) {
        try { SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2); } catch { }
        _vd = FindVirtualBounds();
        if (_vd.IsEmpty) { Console.WriteLine("ERR: 仮想モニタが見つからない"); return 1; }
        _all = UnionAllScreens();
        _capDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures",
            "run-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_capDir);
        Console.WriteLine("仮想モニタ: (" + _vd.X + "," + _vd.Y + ") " + _vd.Width + "x" + _vd.Height
            + "  全画面: (" + _all.X + "," + _all.Y + ") " + _all.Width + "x" + _all.Height);
        Console.WriteLine("証拠キャプチャ: " + _capDir);

        int exit = 1;
        try {
            // ── [1] 仮想液タブ起動 ──
            Console.WriteLine("\n=== [1] 仮想液タブ起動 ===");
            string daemonLog = StartOtdDaemon();
            _targetReportId = ResolveTargetReportId(daemonLog);
            Console.WriteLine("書き込み対象ReportID: 0x" + _targetReportId.ToString("X2")
                + (_targetReportId == 0x05 ? " (col03)" : " (col04)"));
            if (!VerifyOtdDisplayArea()) return 1;
            _vmulti = OpenVmultiControl();
            if (Probe() != 0) return 1;
            Capture("01_仮想液タブ起動後");          // 指定: 起動 → キャプチャ
            StartRolling();                           // ここから切断まで常時キャプチャ

            // ── [2] 電卓起動と配置 ──
            Console.WriteLine("\n=== [2] 電卓起動・配置 ===");
            Process.Start("explorer.exe", "shell:appsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
            Thread.Sleep(3000);
            if (!MoveCalcTo(_vd.X + 10, _vd.Y + 10)) return 1;
            Thread.Sleep(1500);
            Capture("02_電卓配置後");                  // 指定: 配置 → キャプチャ

            // ── [3] 操作の前後でキャプチャ ──
            Console.WriteLine("\n=== [3] ペン操作 7 × 6 = ===");
            int[][] taps = new int[][] {
                new int[] { 3317, 19332 },  // 7   (UI実測: 中心(2001,354)物理→タブレット換算)
                new int[] { 18068, 19332 }, // ×   (乗算ボタン中心(2361,354)物理)
                new int[] { 13147, 22234 }, // 6   (ボタン中心(2241,407)物理)
                new int[] { 18068, 28015 }, // =   (等号ボタン中心(2361,513)物理)
            };
            string[] names = new string[] { "7", "×", "6", "=" };
            for (int i = 0; i < taps.Length; i++) {
                Capture("03-" + (i + 1) + "a_操作前_" + names[i]);
                Tap((ushort)taps[i][0], (ushort)taps[i][1], 4096);
                Thread.Sleep(300);
                Capture("03-" + (i + 1) + "b_操作後_" + names[i]);
            }

            // ── [4] 最終的な表示 ──
            Thread.Sleep(500);
            Capture("04_最終表示");                     // 指定: 最終表示 → キャプチャ

            exit = 0;
        } finally {
            // ── [5] 電卓閉じる → キャプチャ ──
            Console.WriteLine("\n=== [5] 電卓終了 ===");
            foreach (var p in Process.GetProcessesByName("CalculatorApp"))
                try { p.Kill(); } catch { }
            Thread.Sleep(1000);
            Capture("05_電卓終了後");

            // ── [6] 仮想液タブ終了 → キャプチャ ──
            Console.WriteLine("\n=== [6] 仮想液タブ終了 ===");
            Cleanup();
            StopRolling();                             // 常時キャプチャは切断まで
            Capture("06_仮想液タブ終了後");
            Console.WriteLine("\n=== 完了 exit=" + exit + " キャプチャ: " + _capDir
                + " (常時キャプチャ " + _rollCount + "枚)");
        }
        return exit;
    }

    // ══════════ 常時キャプチャ(接続完了〜切断) ══════════

    static void StartRolling() {
        Directory.CreateDirectory(Path.Combine(_capDir, "roll"));
        _rolling = true; _rollCount = 0;
        _rollThread = new Thread(() => {
            while (_rolling) {
                try {
                    string path = Path.Combine(_capDir, "roll", _rollCount.ToString("0000") + ".jpg");
                    lock (_shotLock)
                    using (var bmp = new Bitmap(_all.Width, _all.Height)) {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(_all.X, _all.Y, 0, 0, new Size(_all.Width, _all.Height));
                        var jp = new EncoderParameters(1);
                        jp.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
                        bmp.Save(path, GetJpegEncoder(), jp);
                    }
                    _rollCount++;
                } catch { _rollCount++; }
                Thread.Sleep(500);
            }
        });
        _rollThread.IsBackground = true;
        _rollThread.Start();
        Console.WriteLine("常時キャプチャ開始(0.5秒間隔 → roll/)");
    }
    static void StopRolling() {
        _rolling = false;
        if (_rollThread != null) _rollThread.Join(5000);
    }
    static ImageCodecInfo GetJpegEncoder() {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
        return null;
    }

    // ══════════ 仮想液タブ起動・終了 ══════════

    static string StartOtdDaemon() {
        foreach (var name in new string[] { "OpenTabletDriver.Daemon", "OpenTabletDriver.UX.Wpf" })
            foreach (var p in Process.GetProcessesByName(name))
                try { p.Kill(); } catch { }
        Thread.Sleep(1500);
        string logPath = Path.Combine(_capDir, "otd-daemon.log");
        var psi = new ProcessStartInfo(@"C:\Users\imura\OpenTabletDriver-0.6.7\OpenTabletDriver-0.6.7_win-x64\OpenTabletDriver.Daemon.exe") {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        bool enabled = false;
        var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (s, e) => {
            if (e.Data == null) return;
            try { File.AppendAllText(logPath, e.Data + "\n"); } catch { }
            if (e.Data.Contains("Driver is enabled")) enabled = true;
        };
        proc.ErrorDataReceived += (s, e) => { if (e.Data != null) try { File.AppendAllText(logPath, "[err] " + e.Data + "\n"); } catch { } };
        if (!proc.Start()) throw new Exception("OTDデーモン起動失敗");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        Console.WriteLine("OTDデーモン起動 PID=" + proc.Id + "。初期化完了まで待機...");
        var sw = Stopwatch.StartNew();
        while (!enabled && sw.ElapsedMilliseconds < 30000) Thread.Sleep(300);
        if (!enabled) throw new Exception("OTDデーモンが時間内に初期化されない（ログ: " + logPath + "）");
        Console.WriteLine("初期化完了 (" + sw.ElapsedMilliseconds + "ms)");
        return logPath;
    }

    static byte ResolveTargetReportId(string daemonLog) {
        string log = File.ReadAllText(daemonLog);
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(log, @"col0(\d)")) {
            string col = m.ToString();
            if (col == "col03") return 0x05;
            if (col == "col04") return 0x06;
        }
        throw new Exception("デーモンログから対象コレクション(col03/col04)を特定できない:\n" + log);
    }

    static bool VerifyOtdDisplayArea() {
        string json = File.ReadAllText(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenTabletDriver", "settings.json"));
        string abs = ExtractObject(json, "AbsoluteModeSettings");
        string disp = ExtractObject(abs, "Display");
        double w = GetNum(disp, "Width"), h = GetNum(disp, "Height");
        double x = GetNum(disp, "X"), y = GetNum(disp, "Y");
        Console.WriteLine("OTD Display area: " + w + "x" + h + "@<" + x + "," + y + ">");
        double l = x - w / 2, t = y - h / 2, r = x + w / 2, b = y + h / 2;
        if (!(l >= _vd.X && t >= _vd.Y && r <= _vd.X + _vd.Width && b <= _vd.Y + _vd.Height)) {
            Console.WriteLine("ERR: Display area が仮想モニタ外にはみ出す (" + l + "," + t + ")-(" + r + "," + b + ")");
            return false;
        }
        return true;
    }

    static int Probe() {
        WritePen(16383, 16383, 0, INRANGE);
        Thread.Sleep(400);
        WritePen(16383, 16383, 0, 0);
        Thread.Sleep(300);
        POINT cp; GetCursorPos(out cp);
        bool ok = _vd.Contains(cp.X, cp.Y);
        Console.WriteLine("probe着地: (" + cp.X + "," + cp.Y + ") → " + (ok ? "仮想モニタ内 OK" : "仮想モニタ外 FAIL"));
        return ok ? 0 : 1;
    }

    static void Cleanup() {
        try { if (_vmulti != null) { WritePen(16383, 16383, 0, INRANGE); WritePen(16383, 16383, 0, 0); } } catch { }
        foreach (var p in Process.GetProcessesByName("OpenTabletDriver.Daemon"))
            try { p.Kill(); } catch { }
        Console.WriteLine("仮想液タブ停止（ペンlift + OTDデーモン停止）");
    }

    // ══════════ 電卓 ══════════

    static bool MoveCalcTo(int x, int y) {
        foreach (var p in Process.GetProcessesByName("ApplicationFrameHost")) {
            if (p.MainWindowTitle == "電卓") {
                SetWindowPos(p.MainWindowHandle, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4 | 0x10);
                Console.WriteLine("電卓を (" + x + "," + y + ") へ移動");
                return true;
            }
        }
        Console.WriteLine("ERR: 電卓ウィンドウが見つからない");
        return false;
    }

    // ══════════ ペン書き込み ══════════

    static HidStream OpenVmultiControl() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("VMulti control collection (65/65) not found");
    }

    static void Tap(ushort x, ushort y, ushort pressure) {
        Console.WriteLine("tap (" + x + "," + y + ") 筆圧=" + pressure);
        WritePen(x, y, 0, INRANGE);
        Thread.Sleep(60);
        WritePen(x, y, pressure, (byte)(INRANGE | TIP));
        Thread.Sleep(60);
        WritePen(x, y, 0, INRANGE);
        Thread.Sleep(60);
        WritePen(x, y, 0, 0);
        Thread.Sleep(100);
    }

    static void WritePen(ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[12];
        b[0] = 0x40;      // VMultiID
        b[1] = 0x0B;      // ReportLength
        b[2] = _targetReportId;
        b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        b[10] = 0; b[11] = 0;
        _vmulti.Write(b);
    }

    // ══════════ キャプチャ ══════════

    static void Capture(string name) {
        try {
            string path = Path.Combine(_capDir, name + ".png");
            lock (_shotLock)
            using (var bmp = new Bitmap(_all.Width, _all.Height)) {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(_all.X, _all.Y, 0, 0, new Size(_all.Width, _all.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("  capture: " + name + ".png"
                + (_rolling ? "  (時点: roll/" + _rollCount.ToString("0000") + ".jpg)" : ""));
        } catch (Exception ex) {
            Console.WriteLine("  capture失敗(" + name + "): " + ex.Message);
        }
    }

    static Rectangle FindVirtualBounds() {
        foreach (var sc in System.Windows.Forms.Screen.AllScreens)
            if (!sc.Primary) return sc.Bounds;
        return Rectangle.Empty;
    }

    static Rectangle UnionAllScreens() {
        var r = Rectangle.Empty;
        foreach (var sc in System.Windows.Forms.Screen.AllScreens)
            r = r.IsEmpty ? sc.Bounds : Rectangle.Union(r, sc.Bounds);
        return r;
    }

    // ══════════ settings.json 最小読み取り ══════════

    static string ExtractObject(string json, string key) {
        int i = json.IndexOf("\"" + key + "\"");
        if (i < 0) throw new Exception("key not found: " + key);
        int c = json.IndexOf('{', i);
        int depth = 0, j = c;
        for (; j < json.Length; j++) {
            if (json[j] == '{') depth++;
            else if (json[j] == '}') { depth--; if (depth == 0) break; }
        }
        return json.Substring(c, j - c + 1);
    }
    static double GetNum(string obj, string key) {
        int i = obj.IndexOf("\"" + key + "\"");
        if (i < 0) throw new Exception("key not found: " + key);
        int c = obj.IndexOf(':', i) + 1;
        while (c < obj.Length && (obj[c] == ' ' || obj[c] == '\t' || obj[c] == '\r' || obj[c] == '\n')) c++;
        int e = c;
        while (e < obj.Length && (char.IsDigit(obj[e]) || obj[e] == '-' || obj[e] == '.' || obj[e] == 'e' || obj[e] == 'E' || obj[e] == '+')) e++;
        return double.Parse(obj.Substring(c, e - c), System.Globalization.CultureInfo.InvariantCulture);
    }
}
