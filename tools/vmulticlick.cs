using System;
using System.Threading;
using HidSharp;
static class VClick {
    static void Main(string[] args) {
        // args: virtualDisplayX virtualDisplayY (0-800, 0-600の仮想ディスプレイ内座標)
        int vdx = int.Parse(args[0]), vdy = int.Parse(args[1]);
        int vdispW = 800, vdispH = 600, vdispOffsetX = 1920;
        int desktopW = 2720, desktopH = 1200;
        // 仮想ディスプレイ内座標 → 全デスクトップ座標
        int absX = vdispOffsetX + vdx, absY = vdy;
        // 正規化 → 0-32767
        ushort px = (ushort)((float)absX / desktopW * 32767f);
        ushort py = (ushort)((float)absY / desktopH * 32767f);
        HidStream s = Open();
        // DOWN
        byte[] d = Report(px, py, 4096, 0x01);
        s.Write(d); Thread.Sleep(90);
        // UP
        byte[] u = Report(px, py, 0, 0x00);
        s.Write(u); Thread.Sleep(150);
        s.Close();
        Console.WriteLine("clicked at virtual display (" + vdx + "," + vdy + ") → pen(" + px + "," + py + ")");
    }
    static HidStream Open() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s; if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("no 65/65");
    }
    static byte[] Report(ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[10];
        b[0] = 0x40; b[1] = 0x09; b[2] = 0x09; b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        return b;
    }
}
