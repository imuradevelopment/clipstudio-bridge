// clicktest.cs — 仮想ペンタブ経由のペンクリック（カーソル位置保存・復元つき）
// 使い方: clicktest.exe <logicalX> <logicalY>
// 動作: 現在のカーソル位置を保存 → ペンクリック → カーソル位置を復元
using System;
using System.Threading;
using HidSharp;

static class ClickTest {
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    const int PRIMARY_LOGICAL_W = 1280;
    const int PRIMARY_LOGICAL_H = 800;

    static void Main(string[] args) {
        if (args.Length < 2) { Console.WriteLine("usage: clicktest <logicalX> <logicalY>"); return; }

        // ① 現在のカーソル位置を保存
        POINT saved = new POINT();
        GetCursorPos(out saved);
        Console.WriteLine("saved cursor: (" + saved.X + "," + saved.Y + ")");

        int lx = int.Parse(args[0]), ly = int.Parse(args[1]);

        // ② ペンクリック（vmulti経由）
        using (var s = Open65()) {
            ushort x = (ushort)((float)lx / PRIMARY_LOGICAL_W * 32767f);
            ushort y = (ushort)((float)ly / PRIMARY_LOGICAL_H * 32767f);
            SendPen(s, x, y, 4096); // DOWN（左クリック+筆圧）
            Thread.Sleep(80);
            SendPen(s, x, y, 0);    // UP（離す）
            Thread.Sleep(100);
        }

        // ③ カーソル位置を復元
        SetCursorPos(saved.X, saved.Y);
        Console.WriteLine("cursor restored to (" + saved.X + "," + saved.Y + ")");
        Console.WriteLine("clicked (" + lx + "," + ly + ")");
    }

    static HidStream Open65() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s;
                if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("65/65 collection not found — VMulti driver installed?");
    }

    // AbsoluteInputReport(10B): [0]=0x40 [1]=0x09 [2]=0x09 [3]=Buttons(Left=1) [4..5]=X [6..7]=Y [8..9]=Pressure
    static void SendPen(HidStream s, ushort x, ushort y, ushort pressure) {
        byte[] buf = new byte[10];
        buf[0] = 0x40; buf[1] = 0x09; buf[2] = 0x09; buf[3] = 0x01; // Left button down/up
        buf[4] = (byte)(x & 0xFF); buf[5] = (byte)(x >> 8);
        buf[6] = (byte)(y & 0xFF); buf[7] = (byte)(y >> 8);
        buf[8] = (byte)(pressure & 0xFF); buf[9] = (byte)(pressure >> 8);
        s.Write(buf);
    }
}
