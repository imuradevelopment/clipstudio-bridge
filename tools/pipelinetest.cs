// pipelinetest.cs — 第一回検証：パイプライン通しテスト（読み→ペン操作→読み）
// 一つのプログラムで全工程を順番に実行する。
//
// 手順:
//   1. 仮想モニタのフレームバッファをキャプチャ（読み：初期状態）
//   2. ペン報告で電卓のボタンをクリック（書き：vmulti経由）
//   3. 仮想モニタのフレームバッファをキャプチャ（読み：結果確認）
//
// 使い方: pipelinetest.exe
using System;
using System.Threading;
using HidSharp;

static class PipelineTest {
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    const int PRIMARY_W = 1280;
    const int PRIMARY_H = 800;

    static void Main(string[] args) {
        Console.WriteLine("=== 第一回検証：パイプライン通しテスト ===");
        Console.WriteLine();

        // ── STEP 0: 準備 ──
        string outDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
        System.IO.Directory.CreateDirectory(outDir);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        Console.WriteLine("[準備] 仮想モニタのフレームバッファをキャプチャします（読み：初期状態）");
        CaptureVirtualDisplay(outDir, timestamp + "_step1_initial.png");

        Console.WriteLine();
        Console.WriteLine("[準備] 電卓を仮想モニタ上に起動します");
        LaunchCalculator();
        Thread.Sleep(2000);

        Console.WriteLine("[読み] 中間状態をキャプチャします（電卓あり、表示は0）");
        CaptureVirtualDisplay(outDir, timestamp + "_step2_calculator.png");

        Console.WriteLine();
        Console.WriteLine("[書き] ペン報告で電卓のボタンをクリックします");
        Console.WriteLine("  クリック順: 7 → × → 6 → = ");
        Console.WriteLine("  期待結果: 電卓の表示が「42」になる");

        PenClick(89, 378);   // 7
        PenClick(325, 375);  // ×
        PenClick(245, 430);  // 6
        PenClick(325, 533);  // =
        Console.WriteLine("  4クリック送信完了");

        Console.WriteLine();
        Console.WriteLine("[読み] 結果を確認します（フレームバッファキャプチャ）");
        CaptureVirtualDisplay(outDir, timestamp + "_step3_result.png");

        Console.WriteLine();
        Console.WriteLine("[終了] 電卓を閉じます");
        CloseCalculator();

        Console.WriteLine("[読み] 終了後の状態をキャプチャします");
        CaptureVirtualDisplay(outDir, timestamp + "_step4_closed.png");

        Console.WriteLine();
        Console.WriteLine("=== 検証完了 ===");
        Console.WriteLine("キャプチャ画像を確認して、各段階の状態を検証してください。");
        Console.WriteLine("保存先: " + outDir);
    }

    // ── 電卓の起動 ──
    static void LaunchCalculator() {
        System.Diagnostics.Process.Start("explorer.exe", "shell:appsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
    }

    // ── 電卓の終了 ──
    static void CloseCalculator() {
        var proc = System.Diagnostics.Process.GetProcessesByName("CalculatorApp");
        foreach (var p in proc) p.Kill();
        Thread.Sleep(1000);
    }

    // ── 仮想モニタのフレームバッファキャプチャ ──
    static void CaptureVirtualDisplay(string outDir, string filename) {
        // ComputerUseのキャプチャを使う（エージェント側で実行）
        // ここではC#内で実装せず、エージェント側のComputerUseで撮影する
        // ※設計：エージェントがキャプチャを実行し、画像ファイルを保存
        Console.WriteLine("  [キャプチャ] " + filename + " （エージェント側で実行）");
    }

    // ── ペンクリック（vmulti経由） ──
    static void PenClick(int logicalX, int logicalY) {
        ushort x = (ushort)((float)logicalX / PRIMARY_W * 32767f);
        ushort y = (ushort)((float)logicalY / PRIMARY_H * 32767f);

        using (var s = Open65()) {
            SendReport(s, x, y, 4096, 0x01); // DOWN (left button + pressure)
            Thread.Sleep(80);
            SendReport(s, x, y, 0, 0x00);    // UP
            Thread.Sleep(100);
        }
        Console.WriteLine("  pen click: (" + logicalX + "," + logicalY + ")");
    }

    static HidStream Open65() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("VMulti device (65/65) not found — driver installed?");
    }

    static void SendReport(HidStream s, ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] buf = new byte[10];
        buf[0] = 0x40; buf[1] = 0x09; buf[2] = 0x09; buf[3] = buttons;
        buf[4] = (byte)(x & 0xFF); buf[5] = (byte)(x >> 8);
        buf[6] = (byte)(y & 0xFF); buf[7] = (byte)(y >> 8);
        buf[8] = (byte)(pressure & 0xFF); buf[9] = (byte)(pressure >> 8);
        s.Write(buf);
    }
}
