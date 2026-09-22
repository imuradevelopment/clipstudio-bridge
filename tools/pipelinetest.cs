// pipelinetest.cs — 第一回検証：全工程を1プログラムで通し実行（AI介入なし）
//
// 手順:
//   ① キャプチャ（初期状態：電卓なし）
//   ② 電卓を起動 → 仮想モニタに移動
//   ③ キャプチャ（中間状態：電卓あり、表示0）
//   ④ InjectSyntheticPointerInputでペン報告 7 × 6 =
//   ⑤ キャプチャ（結果：表示42）
//   ⑥ 電卓を終了
//   ⑦ キャプチャ（終了後：電卓なし）
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;

static class PipelineTest {
    // 仮想モニタの物理座標・サイズ
    const int VD_X = 1920;
    const int VD_Y = 0;
    const int VD_W = 800;
    const int VD_H = 600;
    // デスクトップ全体のサイズ（ペン報告の正規化用）
    const int DT_W = 2720;
    const int DT_H = 1200;

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int h2, uint f);
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

    static IntPtr penDev;
    static uint frameId = 0;

    static void Main(string[] args) {
        SetProcessDPIAware();
        string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");
        Directory.CreateDirectory(outDir);
        string ts = DateTime.Now.ToString("HHmmss");

        Console.WriteLine("=== 第一回検証：パイプライン通しテスト ===");

        // ── ① 初期状態キャプチャ（電卓なし） ──
        Console.WriteLine("\n[1] 初期状態キャプチャ（電卓なし）");
        Capture(Path.Combine(outDir, ts + "_1_initial.png"));

        // ── ② 電卓を起動し仮想モニタに移動 ──
        Console.WriteLine("\n[2] 電卓を起動し仮想モニタに配置");
        System.Diagnostics.Process.Start("explorer.exe",
            "shell:appsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        Thread.Sleep(3000);
        MoveCalcTo(VD_X + 10, VD_Y + 10);
        Thread.Sleep(2000);

        // ── ③ 中間状態キャプチャ（電卓あり、表示は0） ──
        Console.WriteLine("\n[3] 中間状態キャプチャ（電卓あり、表示は0）");
        Capture(Path.Combine(outDir, ts + "_2_calculator.png"));

        // ── ④ ペン報告 7 × 6 = ──
        Console.WriteLine("\n[4] ペン報告で 7 × 6 = を送信");
        IntPtr pen = CreateSyntheticPointerDevice(PT_PEN, 1, 1);
        if (pen == IntPtr.Zero) {
            Console.WriteLine("ERR: CreateSyntheticPointerDevice failed err=" + Marshal.GetLastWin32Error());
            return;
        }

        try {
            // 電卓のボタン座標（仮想モニタキャプチャ800×600から実測）
            PenTap(pen, VD_X + 55,  VD_Y + 372, 4096); // 7
            Thread.Sleep(300);
            PenTap(pen, VD_X + 295, VD_Y + 372, 4096); // ×
            Thread.Sleep(300);
            PenTap(pen, VD_X + 205, VD_Y + 425, 4096); // 6
            Thread.Sleep(300);
            PenTap(pen, VD_X + 295, VD_Y + 527, 4096); // =
            Thread.Sleep(500);
        } finally {
            DestroySyntheticPointerDevice(pen);
        }
        Console.WriteLine("  ペン報告送信完了");

        // ── ⑤ 結果キャプチャ ──
        Console.WriteLine("\n[5] 結果キャプチャ（表示は42になるはず）");
        Capture(Path.Combine(outDir, ts + "_3_result.png"));

        // ── ⑥ 電卓を終了 ──
        Console.WriteLine("\n[6] 電卓を終了");
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("CalculatorApp")) p.Kill();
        Thread.Sleep(1000);

        // ── ⑦ 終了後キャプチャ ──
        Console.WriteLine("\n[7] 終了後キャプチャ（電卓なし）");
        Capture(Path.Combine(outDir, ts + "_4_closed.png"));

        Console.WriteLine("\n=== 全工程完了 ===");
        Console.WriteLine("4枚のキャプチャを確認してください。保存先: " + outDir);
    }

    // ── 仮想モニタのキャプチャ（GDI+ CopyFromScreen） ──
    static void Capture(string filepath) {
        using (var bmp = new Bitmap(VD_W, VD_H)) {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(VD_X, VD_Y, 0, 0, new Size(VD_W, VD_H));
            bmp.Save(filepath, ImageFormat.Png);
        }
        Console.WriteLine("  saved: " + filepath);
    }

    // ── 電卓を仮想モニタ上の指定位置に移動 ──
    static void MoveCalcTo(int x, int y) {
        var procs = System.Diagnostics.Process.GetProcessesByName("ApplicationFrameHost");
        foreach (var p in procs) {
            if (p.MainWindowTitle == "電卓") {
                SetWindowPos(p.MainWindowHandle, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4);
                Console.WriteLine("  moved to (" + x + "," + y + ")");
                return;
            }
        }
        Console.WriteLine("  WARN: 電卓window not found");
    }

    // ── ペン報告の生成と注入 ──
    static void PenTap(IntPtr pen, int absX, int absY, uint pressure) {
        frameId++;
        POINTER_TYPE_INFO t = new POINTER_TYPE_INFO();
        t.type = PT_PEN;
        t.penInfo.pointerInfo.pointerType = PT_PEN;
        t.penInfo.pointerInfo.pointerId = 1;
        t.penInfo.pointerInfo.frameId = frameId;
        t.penInfo.pointerInfo.pointerFlags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_PRIMARY;
        t.penInfo.pointerInfo.ptPixelLocation.X = absX;
        t.penInfo.pointerInfo.ptPixelLocation.Y = absY;
        t.penInfo.pointerInfo.historyCount = 1;
        t.penInfo.penMask = PEN_MASK_PRESSURE;
        t.penInfo.pressure = pressure;
        // DOWN
        if (!Inject(pen, ref t, "DOWN")) return;
        Thread.Sleep(40);
        // UPDATE（微小移動でクリックを確定させる）
        t.penInfo.pointerInfo.frameId = ++frameId;
        t.penInfo.pointerInfo.pointerFlags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
        if (!Inject(pen, ref t, "UPDATE")) return;
        Thread.Sleep(30);
        // UP
        t.penInfo.pointerInfo.frameId = ++frameId;
        t.penInfo.pointerInfo.pointerFlags = POINTER_FLAG_UP | POINTER_FLAG_INRANGE;
        t.penInfo.pressure = 0;
        if (!Inject(pen, ref t, "UP")) return;
        Thread.Sleep(60);
    }

    static bool Inject(IntPtr pen, ref POINTER_TYPE_INFO t, string stage) {
        if (InjectSyntheticPointerInput(pen, ref t, 1)) return true;
        Console.WriteLine("ERR: inject at " + stage + " err=" + Marshal.GetLastWin32Error());
        return false;
    }
}
