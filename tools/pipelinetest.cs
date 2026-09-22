// pipelinetest.cs — 第一回検証: 仮想液タブの「読み→書き→読み」を電卓で通す。
//
// モード:
//   run                       … 全工程を1実行で通す（本命・InjectSyntheticPointerInput版）
//                                ①仮想モニタ領域取得 ②初期キャプチャ ③電卓起動→仮想モニタへ移動
//                                ④中間キャプチャ ⑤ペン注入 7×6= ⑥結果キャプチャ ⑦電卓終了 ⑧終了後キャプチャ
//   move                      … ③④のみ（VMulti版tap用の準備）
//   tap x1 y1 [x2 y2 ...]     … VMulti HID報告でタップ→結果キャプチャ（比較試験用）
//                                ※実行前にクリックなし移動報告で着地が仮想モニタ内か確認、外なら中止
//   close                     … 電卓終了+終了後キャプチャ
//
// 書き込み方式の位置づけ:
//   run  = InjectSyntheticPointerInput（OS公式API・デスクトップ絶対座標直指定。
//          デジタイザ↔ディスプレイの自動ペアリング規則に依存せず仮想モニタを直接指定できる。
//          過去に仮想モニタ上の電卓表示を変化させた観測実績あり）
//   tap  = VMulti HID報告（実装置エミュ。現状ペアリングがメインモニタ固定のためブロッカー検証用）
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;

static class PipelineTest {
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    // Per-Monitor DPI Aware にして全座標を物理ピクセルに統一する。
    // この環境はDPI 150%で、System-Awareだと仮想モニタが(2880,0)1200x900に見えて
    // 物理(1920,0)800x600と食い違い、キャプチャ・注入ともに実在しない領域を叩く。
    static void MakePerMonitorDpiAware() {
        try { if (SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)) return; } catch { }
        SetProcessDPIAware();
    }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int h2, uint f);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr CreateSyntheticPointerDevice(uint type, uint maxCount, uint mode);
    [DllImport("user32.dll")] static extern bool InjectSyntheticPointerInput(IntPtr dev, ref POINTER_TYPE_INFO info, uint count);
    [DllImport("user32.dll")] static extern void DestroySyntheticPointerDevice(IntPtr dev);

    const uint PT_PEN = 3;
    const uint POINTER_FLAG_DOWN = 0x00010000;
    const uint POINTER_FLAG_UPDATE = 0x00020000;
    const uint POINTER_FLAG_UP = 0x00040000;
    const uint POINTER_FLAG_INRANGE = 0x00000002;
    const uint POINTER_FLAG_INCONTACT = 0x00000004;
    const uint POINTER_FLAG_PRIMARY = 0x00000200;
    const uint PEN_MASK_PRESSURE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong performanceCount;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_PEN_INFO {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TYPE_INFO {
        public uint type;
        public POINTER_PEN_INFO penInfo;
    }

    static Rectangle _vd; // 仮想モニタ領域（デスクトップ絶対座標・動的取得）
    static uint frameId = 0;

    static int Main(string[] args) {
        MakePerMonitorDpiAware();
        string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");
        Directory.CreateDirectory(outDir);
        string ts = DateTime.Now.ToString("HHmmss");

        _vd = FindVirtualBounds();
        if (_vd.IsEmpty) { Console.WriteLine("ERR: 仮想モニタが見つからない"); return 1; }
        Console.WriteLine("仮想モニタ: (" + _vd.X + "," + _vd.Y + ") " + _vd.Width + "x" + _vd.Height);

        string mode = args.Length > 0 ? args[0] : "";
        switch (mode) {
            case "run":   return DoRun(outDir, ts);
            case "move":  return DoMove(outDir, ts);
            case "tap":   return DoTap(outDir, ts, args);
            case "close": return DoClose(outDir, ts);
            default:
                Console.WriteLine("usage: pipelinetest run | move | tap x1 y1 [...] | close");
                return 1;
        }
    }

    static Rectangle FindVirtualBounds() {
        foreach (var sc in System.Windows.Forms.Screen.AllScreens)
            if (!sc.Primary) return sc.Bounds;
        return Rectangle.Empty;
    }

    // ── 全工程通し（InjectSyntheticPointerInput版・本命） ──
    static int DoRun(string outDir, string ts) {
        Console.WriteLine("\n[1] 初期キャプチャ（電卓なし・仮想モニタ領域）");
        Capture(_vd, Path.Combine(outDir, ts + "_1_initial.png"));

        Console.WriteLine("\n[2] 電卓を起動し仮想モニタへ移動");
        System.Diagnostics.Process.Start("explorer.exe",
            "shell:appsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        Thread.Sleep(3000);
        if (!MoveCalcTo(_vd.X + 10, _vd.Y + 10)) return 1;
        Thread.Sleep(1500);

        Console.WriteLine("\n[3] 中間キャプチャ（電卓あり・表示0のはず）");
        Capture(_vd, Path.Combine(outDir, ts + "_2_calculator.png"));

        Console.WriteLine("\n[4] ペン注入 7 × 6 =（InjectSyntheticPointerInput・仮想モニタ絶対座標）");
        IntPtr pen = CreateSyntheticPointerDevice(PT_PEN, 1, 1);
        if (pen == IntPtr.Zero) {
            Console.WriteLine("ERR: CreateSyntheticPointerDevice failed err=" + Marshal.GetLastWin32Error());
            return 1;
        }
        try {
            // 電卓のボタン位置（仮想モニタローカルpx。電卓は (10,10) に配置した前提の旧実測値）
            SynTap(pen, _vd.X + 55,  _vd.Y + 372, 4096);  // 7
            Thread.Sleep(300);
            SynTap(pen, _vd.X + 295, _vd.Y + 372, 4096);  // ×
            Thread.Sleep(300);
            SynTap(pen, _vd.X + 205, _vd.Y + 425, 4096);  // 6
            Thread.Sleep(300);
            SynTap(pen, _vd.X + 295, _vd.Y + 527, 4096);  // =
            Thread.Sleep(500);
        } finally {
            DestroySyntheticPointerDevice(pen);
        }
        Console.WriteLine("  ペン注入完了");

        Console.WriteLine("\n[5] 結果キャプチャ（表示42になれば第一回検証成立）");
        Capture(_vd, Path.Combine(outDir, ts + "_3_result.png"));

        DoClose(outDir, ts);
        Console.WriteLine("\n=== run 完了。キャプチャ4枚を確認: " + outDir);
        return 0;
    }

    // ── 電卓を仮想モニタへ（UWPはApplicationFrameHostがホスト。実績ある方式） ──
    static bool MoveCalcTo(int x, int y) {
        var procs = System.Diagnostics.Process.GetProcessesByName("ApplicationFrameHost");
        foreach (var p in procs) {
            if (p.MainWindowTitle == "電卓") {
                SetWindowPos(p.MainWindowHandle, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4 | 0x10);
                Console.WriteLine("  電卓を (" + x + "," + y + ") へ移動");
                return true;
            }
        }
        Console.WriteLine("  ERR: 電卓ウィンドウが見つからない");
        return false;
    }

    // ── VMulti版の準備（move）とtap ──
    static int DoMove(string outDir, string ts) {
        Console.WriteLine("\n[1] 初期キャプチャ");
        Capture(_vd, Path.Combine(outDir, ts + "_1_initial.png"));
        Console.WriteLine("\n[2] 電卓を起動し仮想モニタへ移動");
        System.Diagnostics.Process.Start("explorer.exe",
            "shell:appsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        Thread.Sleep(3000);
        if (!MoveCalcTo(_vd.X + 10, _vd.Y + 10)) return 1;
        Thread.Sleep(1500);
        Console.WriteLine("\n[3] 中間キャプチャ");
        Capture(_vd, Path.Combine(outDir, ts + "_2_calculator.png"));
        return 0;
    }

    static int DoTap(string outDir, string ts, string[] args) {
        if (args.Length < 3 || (args.Length - 1) % 2 != 0) {
            Console.WriteLine("usage: pipelinetest tap x1 y1 [x2 y2 ...]");
            return 1;
        }
        using (var s = OpenVmulti()) {
            Console.WriteLine("\n[4] 着地確認（クリックなし移動報告）");
            VSend(s, 16383, 16383, 4096, 0x00);
            Thread.Sleep(300);
            VSend(s, 16383, 16383, 0, 0x00);
            Thread.Sleep(200);
            POINT cp; GetCursorPos(out cp);
            bool inside = _vd.Contains(cp.X, cp.Y);
            Console.WriteLine("  着地: (" + cp.X + "," + cp.Y + ") → " + (inside ? "仮想モニタ内 OK" : "仮想モニタ外"));
            if (!inside) {
                Console.WriteLine("  中止: ペンの紐付け先が仮想モニタではない");
                return 1;
            }
            Console.WriteLine("\n[5] VMulti HID報告でペンタップ");
            for (int i = 1; i + 1 < args.Length; i += 2) {
                int vx = int.Parse(args[i]), vy = int.Parse(args[i + 1]);
                VTap(s, vx, vy);
                Console.WriteLine("  tap (" + vx + "," + vy + ")");
                Thread.Sleep(200);
            }
        }
        Console.WriteLine("\n[6] 結果キャプチャ");
        Capture(_vd, Path.Combine(outDir, ts + "_3_result.png"));
        return 0;
    }

    static int DoClose(string outDir, string ts) {
        Console.WriteLine("\n[7] 電卓を終了");
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("CalculatorApp")) p.Kill();
        Thread.Sleep(1000);
        Console.WriteLine("\n[8] 終了後キャプチャ");
        Capture(_vd, Path.Combine(outDir, ts + "_4_closed.png"));
        return 0;
    }

    // ── 仮想モニタ領域のキャプチャ ──
    static void Capture(Rectangle r, string filepath) {
        using (var bmp = new Bitmap(r.Width, r.Height)) {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height));
            bmp.Save(filepath, ImageFormat.Png);
        }
        Console.WriteLine("  saved: " + filepath);
    }

    // ── InjectSyntheticPointerInput によるペンタップ（down/update/up） ──
    static void SynTap(IntPtr pen, int absX, int absY, uint pressure) {
        POINTER_TYPE_INFO t = new POINTER_TYPE_INFO();
        t.type = PT_PEN;
        t.penInfo.pointerInfo.pointerType = PT_PEN;
        t.penInfo.pointerInfo.pointerId = 1;
        t.penInfo.pointerInfo.frameId = ++frameId;
        t.penInfo.pointerInfo.pointerFlags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_PRIMARY;
        t.penInfo.pointerInfo.ptPixelLocation.X = absX;
        t.penInfo.pointerInfo.ptPixelLocation.Y = absY;
        t.penInfo.pointerInfo.historyCount = 1;
        t.penInfo.penMask = PEN_MASK_PRESSURE;
        t.penInfo.pressure = pressure;
        if (!Inject(pen, ref t, "DOWN")) return;
        Thread.Sleep(40);
        t.penInfo.pointerInfo.frameId = ++frameId;
        t.penInfo.pointerInfo.pointerFlags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
        if (!Inject(pen, ref t, "UPDATE")) return;
        Thread.Sleep(30);
        t.penInfo.pointerInfo.frameId = ++frameId;
        t.penInfo.pointerInfo.pointerFlags = POINTER_FLAG_UP | POINTER_FLAG_INRANGE;
        t.penInfo.pressure = 0;
        Inject(pen, ref t, "UP");
        Thread.Sleep(60);
    }

    static bool Inject(IntPtr pen, ref POINTER_TYPE_INFO t, string stage) {
        if (InjectSyntheticPointerInput(pen, ref t, 1)) return true;
        Console.WriteLine("ERR: inject at " + stage + " err=" + Marshal.GetLastWin32Error());
        return false;
    }

    // ── VMulti HID報告（比較試験用） ──
    static HidStream OpenVmulti() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("VMulti device (65/65) not found");
    }

    static void VTap(HidStream s, int vx, int vy) {
        ushort px = (ushort)((float)vx / _vd.Width * 32767f);
        ushort py = (ushort)((float)vy / _vd.Height * 32767f);
        VSend(s, px, py, 4096, 0x01);
        Thread.Sleep(60);
        VSend(s, px, py, 0, 0x00);
        Thread.Sleep(80);
    }

    static void VSend(HidStream s, ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[10];
        b[0] = 0x40; b[1] = 0x09; b[2] = 0x09; b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        s.Write(b);
    }
}
