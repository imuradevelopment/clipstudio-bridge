// pipelinetest.cs — 第一回検証：全工程を1プログラムで通し実行（AI介入なし）
// 入力方式: VMulti装置経由のAbsoluteInputReport（clicktest.csと同じ実績ある方式）
//
// 手順:
//   ① キャプチャ（初期状態：電卓なし）
//   ② 電卓を起動
//   ③ キャプチャ（中間状態：電卓あり、表示0）
//   ④ VMulti HID報告でペンクリック 7 × 6 =
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
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int h2, uint f);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern IntPtr FindWindow(string cls, string title);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    // ── VMulti AbsoluteInputReport (10B) ──
    // [0]=0x40(VMultiID) [1]=0x09(長さ) [2]=0x09(reportID) [3]=buttons [4..5]=X [6..7]=Y [8..9]=Pressure
    static HidStream _vmulti;
    static uint _reportCount = 0;

    static void Main(string[] args) {
        SetProcessDPIAware();
        string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");
        Directory.CreateDirectory(outDir);
        string ts = DateTime.Now.ToString("HHmmss");

        Console.WriteLine("=== パイプライン通しテスト ===");

        // ── ① 初期状態キャプチャ（電卓なし） ──
        Console.WriteLine("\n[1] 初期状態キャプチャ（電卓なし）");
        Capture(0, 0, 1280, 800, Path.Combine(outDir, ts + "_1_initial.png"));

        // ── ② 電卓を起動 ──
        Console.WriteLine("\n[2] 電卓を起動");
        System.Diagnostics.Process.Start("explorer.exe",
            "shell:appsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        Thread.Sleep(3000);

        // ── ③ 中間状態キャプチャ（電卓あり、表示は0） ──
        Console.WriteLine("\n[3] 中間状態キャプチャ（電卓あり、表示は0）");
        Capture(0, 0, 1280, 800, Path.Combine(outDir, ts + "_2_calculator.png"));

        // ── ④ ペン報告 7 × 6 = ──
        Console.WriteLine("\n[4] VMulti HID報告でペンクリック 7 × 6 = を送信");
        using (_vmulti = OpenVmulti()) {
            PenTap(89, 378);   // 7
            Thread.Sleep(200);
            PenTap(282, 372);  // ×
            Thread.Sleep(200);
            PenTap(205, 423);  // 6
            Thread.Sleep(200);
            PenTap(282, 525);  // =
            Thread.Sleep(500);
        }
        Console.WriteLine("  ペンクリック送信完了");

        // ── ⑤ 結果キャプチャ ──
        Console.WriteLine("\n[5] 結果キャプチャ（表示は42になるはず）");
        Capture(0, 0, 1280, 800, Path.Combine(outDir, ts + "_3_result.png"));

        // ── ⑥ 電卓を終了 ──
        Console.WriteLine("\n[6] 電卓を終了");
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("CalculatorApp")) p.Kill();
        Thread.Sleep(1000);

        // ── ⑦ 終了後キャプチャ ──
        Console.WriteLine("\n[7] 終了後キャプチャ（電卓なし）");
        Capture(0, 0, 1280, 800, Path.Combine(outDir, ts + "_4_closed.png"));

        Console.WriteLine("\n=== 全工程完了 ===");
        Console.WriteLine("4枚のキャプチャを確認してください。保存先: " + outDir);
    }

    // ── 一次モニタのキャプチャ（GDI+ CopyFromScreen） ──
    static void Capture(int x, int y, int w, int h, string filepath) {
        using (var bmp = new Bitmap(w, h)) {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
            bmp.Save(filepath, ImageFormat.Png);
        }
        Console.WriteLine("  saved: " + filepath);
    }

    // ── VMulti 65/65 collection を開く（clicktest.csと同じ方式） ──
    static HidStream OpenVmulti() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("VMulti device (65/65) not found");
    }

    // ── ペン報告の送信（clicktest.csと同じ方式） ──
    // AbsoluteInputReport(10B): [0]=0x40 [1]=0x09 [2]=0x09 [3]=buttons [4..5]=X [6..7]=Y [8..9]=Pressure
    // 座標は一次モニタの論理座標（0-1280, 0-800）、装置座標0-32767に正規化
    static void PenTap(HidStream s, int logicalX, int logicalY) {
        _reportCount++;
        ushort px = (ushort)((float)logicalX / 1280f * 32767f);
        ushort py = (ushort)((float)logicalY / 800f * 32767f);
        // DOWN (left button)
        SendReport(s, px, py, 4096, 0x01);
        Thread.Sleep(60);
        // UP
        SendReport(s, px, py, 0, 0x00);
        Thread.Sleep(80);
    }

    static void SendReport(HidStream s, ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[10];
        b[0] = 0x40; b[1] = 0x09; b[2] = 0x09; b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        s.Write(b);
    }
}
