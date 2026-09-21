using System;
using System.Threading;
using HidSharp;
static class ClickTest {
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct POINT { public int X, Y; }
    static void Main(string[] args) {
        int lx = int.Parse(args[0]), ly = int.Parse(args[1]);
        HidStream s = Open();
        ushort x = (ushort)((float)lx / 1280f * 32767f);
        ushort y = (ushort)((float)ly / 800f * 32767f);
        // LEFT DOWN (Buttons bit0=1)
        Send(s, x, y, 4096, 0x01);
        Thread.Sleep(90);
        // LEFT UP (bit0=0)
        Send(s, x, y, 0, 0x00);
        Thread.Sleep(150);
        s.Close();
        Console.WriteLine("clicked (" + lx + "," + ly + ")");
    }
    static HidStream Open() {
        foreach (var dev in DeviceList.Local.GetHidDevices(255, 47820)) {
            if (dev.GetMaxInputReportLength() == 65 && dev.GetMaxOutputReportLength() == 65) {
                HidStream s; if (dev.TryOpen(out s)) return s;
            }
        }
        throw new Exception("no 65/65");
    }
    static void Send(HidStream s, ushort x, ushort y, ushort pressure, byte buttons) {
        byte[] b = new byte[10];
        b[0] = 0x40; b[1] = 0x09; b[2] = 0x09; b[3] = buttons;
        b[4] = (byte)(x & 0xFF); b[5] = (byte)(x >> 8);
        b[6] = (byte)(y & 0xFF); b[7] = (byte)(y >> 8);
        b[8] = (byte)(pressure & 0xFF); b[9] = (byte)(pressure >> 8);
        s.Write(b);
    }
}
