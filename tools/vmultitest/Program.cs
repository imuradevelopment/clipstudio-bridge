// vmultitest — 公式ライブラリ(VoiDPlugins.Library.VMulti)経由でペン報告を送る動作確認
using System;
using System.Threading;
using VoiDPlugins.Library.VMulti;
using VoiDPlugins.Library.VMulti.Device;

static class Program {
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    static void Main() {
        var inst = new VMultiInstance<AbsoluteInputReport>("VMultiAbs", new AbsoluteInputReport());

        POINT before = new POINT();
        GetCursorPos(out before);
        Console.WriteLine("cursor before: (" + before.X + "," + before.Y + ")");

        // DOWN（筆圧4096）
        unsafe {
            inst.Pointer->X = 0x4000;
            inst.Pointer->Y = 0x4000;
            inst.Pointer->Pressure = 4096;
        }
        inst.Write();
        Thread.Sleep(60);

        // MOVE ×8（X+、Y-、筆圧漸増）
        for (int i = 1; i <= 8; i++) {
            unsafe {
                inst.Pointer->X = (ushort)(0x4000 + i * 0x500);
                inst.Pointer->Y = (ushort)(0x4000 - i * 0x280);
                inst.Pointer->Pressure = (ushort)(4096 + i * 500);
            }
            inst.Write();
            Thread.Sleep(30);
        }

        // UP（筆圧0＝ペン離す）
        unsafe {
            inst.Pointer->X = 0x6000;
            inst.Pointer->Y = 0x2000;
            inst.Pointer->Pressure = 0;
        }
        inst.Write();
        Thread.Sleep(300);

        POINT after = new POINT();
        GetCursorPos(out after);
        Console.WriteLine("cursor after: (" + after.X + "," + after.Y + ")");
        Console.WriteLine((before.X != after.X || before.Y != after.Y) ? "RESULT: CURSOR MOVED (pen events reached OS)" : "RESULT: cursor did not move");
    }
}
